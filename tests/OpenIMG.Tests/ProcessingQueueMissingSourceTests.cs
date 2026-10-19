using System.IO;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Abstractions.Orchestration;
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Orchestration;
using OpenIMG.Application.Validation;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Infrastructure.Storage;

namespace OpenIMG.Tests;

public sealed class ProcessingQueueMissingSourceTests
{
    [Fact]
    [Trait("Category", TestCategories.Integration)]
    public async Task MissingSourceIsPersistedAsFailedAndDoesNotBlockNextAsset()
    {
        var workspace = TestArtifactWorkspace.Create(nameof(MissingSourceIsPersistedAsFailedAndDoesNotBlockNextAsset));
        var project = new Project
        {
            Name = "Queue regression",
            OutputDirectory = Path.Combine(workspace, "output")
        };
        var assets = new[]
        {
            NewMissingAsset(project.Id, workspace, "first-missing.jpg"),
            NewMissingAsset(project.Id, workspace, "second-missing.jpg")
        };
        var assetRepository = new DetachedImageAssetRepository(assets);
        var projectRepository = new SingleProjectRepository(project);
        var started = new ConcurrentDictionary<Guid, int>();
        var queueFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var orchestrator = new ImageProcessingOrchestrator(
            generationProvider: null!,
            localValidator: null!,
            fileService: new FileSystemService(),
            assetRepository: assetRepository,
            decisionEngine: null!,
            options: new OpenImgOptions
            {
                OperationsDirectory = Path.Combine(workspace, "operations"),
                MaxAutomaticAttempts = 1
            },
            logger: NullLogger<ImageProcessingOrchestrator>.Instance);
        var queue = new ProcessingQueue(
            orchestrator,
            assetRepository,
            projectRepository,
            NullLogger<ProcessingQueue>.Instance);

        queue.AssetProgress += (_, args) =>
        {
            if (args.Step == "Starting processing")
                started.AddOrUpdate(args.AssetId, 1, (_, count) => count + 1);
        };
        queue.StateChanged += (_, args) =>
        {
            if (args.Message == "Queue finished or idle.")
                queueFinished.TrySetResult();
        };

        try
        {
            await queue.StartAsync(project.Id);
            await queueFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(2, started.Count);
            Assert.All(assets, asset => Assert.Equal(1, started.GetValueOrDefault(asset.Id)));
            Assert.All(assets, asset => Assert.Equal(ImageProcessingStatus.Failed, assetRepository.GetPersistedStatus(asset.Id)));
            Assert.Equal(2, assetRepository.UpdateCount);
            Assert.False(queue.IsRunning);
        }
        finally
        {
            await queue.StopAsync();
            await Task.WhenAny(queueFinished.Task, Task.Delay(TimeSpan.FromSeconds(1)));
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static ImageAsset NewMissingAsset(Guid projectId, string workspace, string fileName) => new()
    {
        ProjectId = projectId,
        OriginalFileName = fileName,
        OriginalFilePath = Path.Combine(workspace, "missing-sources", fileName),
        Status = ImageProcessingStatus.Pending
    };

    private sealed class SingleProjectRepository(Project project) : IProjectRepository
    {
        public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(id == project.Id ? project : null);
        public Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Project>>([project]);
        public Task<Project> CreateAsync(Project value, CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task UpdateAsync(Project value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DetachedImageAssetRepository : IImageAssetRepository
    {
        private readonly Dictionary<Guid, ImageAsset> _persisted;

        public DetachedImageAssetRepository(IEnumerable<ImageAsset> assets) =>
            _persisted = assets.ToDictionary(asset => asset.Id, Clone);

        public int UpdateCount { get; private set; }

        public ImageProcessingStatus GetPersistedStatus(Guid id) => _persisted[id].Status;

        public Task<IReadOnlyList<ImageAsset>> GetByProjectIdAsync(Guid projectId, ImageProcessingStatus? statusFilter = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ImageAsset> result = _persisted.Values
                .Where(asset => asset.ProjectId == projectId && (!statusFilter.HasValue || asset.Status == statusFilter.Value))
                .OrderBy(asset => asset.OriginalFileName, StringComparer.Ordinal)
                .Select(Clone)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<int> ResetProjectProcessingStateAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task UpdateAsync(ImageAsset asset, CancellationToken cancellationToken = default)
        {
            _persisted[asset.Id].Status = asset.Status;
            UpdateCount++;
            return Task.CompletedTask;
        }

        public Task<ImageAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_persisted.TryGetValue(id, out var asset) ? Clone(asset) : null);
        public Task<ImageAsset?> GetByHashAsync(Guid projectId, string sha256Hash, CancellationToken cancellationToken = default) => Task.FromResult<ImageAsset?>(null);
        public Task<ImageAsset> AddAsync(ImageAsset asset, CancellationToken cancellationToken = default) => Task.FromResult(asset);
        public Task AddRangeAsync(IEnumerable<ImageAsset> values, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ProcessingJob> CreateJobAsync(Guid assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProcessingJob?> GetLatestJobForAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.FromResult<ProcessingJob?>(null);
        public Task UpdateJobAsync(ProcessingJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RestorationAttempt> AddAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RestorationAttempt>> GetAttemptsForJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RestorationAttempt>>([]);
        public Task RemoveIncompleteAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static ImageAsset Clone(ImageAsset asset) => new()
        {
            Id = asset.Id,
            ProjectId = asset.ProjectId,
            OriginalFileName = asset.OriginalFileName,
            OriginalFilePath = asset.OriginalFilePath,
            Sha256Hash = asset.Sha256Hash,
            Status = asset.Status
        };
    }
}
