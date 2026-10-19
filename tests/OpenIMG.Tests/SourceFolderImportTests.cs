using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using OpenIMG.App.Services;
using OpenIMG.App.ViewModels;
using OpenIMG.Application.Abstractions.AI;
using OpenIMG.Application.Abstractions.Orchestration;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Abstractions.Storage;
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Importing;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Infrastructure.Storage;

namespace OpenIMG.Tests;

public sealed class SourceFolderImportTests
{
    [Fact]
    [Trait("Category", TestCategories.Integration)]
    public async Task ImageAddedToWatchedFolderIsImportedIntoTheOpenLibrary()
    {
        var workspace = TestArtifactWorkspace.Create(nameof(ImageAddedToWatchedFolderIsImportedIntoTheOpenLibrary));
        var sourceFolder = Path.Combine(workspace, "source");
        var outputFolder = Path.Combine(workspace, "output");
        Directory.CreateDirectory(sourceFolder);

        var project = new Project
        {
            Name = "Folder watcher test",
            SourceDirectory = string.Empty,
            OutputDirectory = outputFolder
        };
        var assetRepository = new InMemoryAssetRepository();
        var monitor = new ProjectSourceFolderMonitor(NullLogger<ProjectSourceFolderMonitor>.Instance);
        var importer = new ProjectSourceImageImporter(
            assetRepository,
            new FileSystemService(),
            NullLogger<ProjectSourceImageImporter>.Instance);
        var viewModel = new MainViewModel(
            new InMemoryProjectRepository(project),
            assetRepository,
            new FileSystemService(),
            new NoOpProcessingQueue(),
            webClient: null!,
            monitor,
            importer,
            new OpenImgOptions { DefaultOutputDirectory = outputFolder },
            NullLogger<MainViewModel>.Instance)
        {
            CurrentProject = project
        };

        try
        {
            // Selecting/importing an empty folder enables monitoring without a test-only hook.
            await viewModel.ImportFolderAsync(sourceFolder);

            var added = new TaskCompletionSource<ImageAsset>(TaskCreationOptions.RunContinuationsAsynchronously);
            assetRepository.AssetAdded += asset => added.TrySetResult(asset);
            var imagePath = Path.Combine(sourceFolder, "added-from-explorer.png");
            using (var image = new Mat(new Size(32, 24), MatType.CV_8UC3, Scalar.White))
                Cv2.ImWrite(imagePath, image);

            var importedAsset = await added.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(project.Id, importedAsset.ProjectId);
            Assert.Equal("added-from-explorer.png", importedAsset.OriginalFileName);
            Assert.Equal(Path.GetFullPath(imagePath), importedAsset.OriginalFilePath);
            Assert.Equal(ImageProcessingStatus.Pending, importedAsset.Status);
            Assert.NotEmpty(importedAsset.Sha256Hash);
            Assert.NotNull(importedAsset.ThumbnailPath);

            Assert.True(SpinWait.SpinUntil(
                () => viewModel.AllAssets.Count == 1 && viewModel.PendingCount == 1,
                TimeSpan.FromSeconds(2)));
            Assert.Equal(1, viewModel.PendingCount);
            Assert.Single(assetRepository.Assets);
        }
        finally
        {
            viewModel.Dispose();
            monitor.Dispose();
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class InMemoryProjectRepository(Project project) : IProjectRepository
    {
        public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Project?>(id == project.Id ? project : null);

        public Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Project>>([project]);

        public Task<Project> CreateAsync(Project value, CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task UpdateAsync(Project value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InMemoryAssetRepository : IImageAssetRepository
    {
        private readonly object _gate = new();
        private readonly List<ImageAsset> _assets = [];

        public event Action<ImageAsset>? AssetAdded;

        public IReadOnlyList<ImageAsset> Assets
        {
            get
            {
                lock (_gate)
                    return _assets.ToArray();
            }
        }

        public Task<ImageAsset> AddAsync(ImageAsset asset, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _assets.Add(asset);
            AssetAdded?.Invoke(asset);
            return Task.FromResult(asset);
        }

        public Task<ImageAsset?> GetByHashAsync(Guid projectId, string sha256Hash, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                return Task.FromResult(_assets.FirstOrDefault(asset => asset.ProjectId == projectId && asset.Sha256Hash == sha256Hash));
        }

        public Task<IReadOnlyList<ImageAsset>> GetByProjectIdAsync(Guid projectId, ImageProcessingStatus? statusFilter = null, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                IReadOnlyList<ImageAsset> result = _assets
                    .Where(asset => asset.ProjectId == projectId && (!statusFilter.HasValue || asset.Status == statusFilter.Value))
                    .ToArray();
                return Task.FromResult(result);
            }
        }

        public Task<int> ResetProjectProcessingStateAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var assets = _assets.Where(asset => asset.ProjectId == projectId).ToArray();
                foreach (var asset in assets)
                    asset.Status = ImageProcessingStatus.Pending;
                return Task.FromResult(assets.Length);
            }
        }

        public Task<ImageAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                return Task.FromResult(_assets.FirstOrDefault(asset => asset.Id == id));
        }

        public Task AddRangeAsync(IEnumerable<ImageAsset> assets, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(ImageAsset asset, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ProcessingJob> CreateJobAsync(Guid assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProcessingJob?> GetLatestJobForAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.FromResult<ProcessingJob?>(null);
        public Task UpdateJobAsync(ProcessingJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RestorationAttempt> AddAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RestorationAttempt>> GetAttemptsForJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RestorationAttempt>>([]);
        public Task RemoveIncompleteAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpProcessingQueue : IProcessingQueue
    {
        public bool IsRunning => false;
        public bool IsPaused => false;
        public ImageAsset? CurrentProcessingAsset => null;
        public string CurrentStatusMessage => "Idle";
        public int TotalItems => 0;
        public int ProcessedItems => 0;
        public event EventHandler<QueueStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<AssetProgressEventArgs>? AssetProgress { add { } remove { } }
        public Task StartAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task CancelCurrentAsync() => Task.CompletedTask;
        public Task RetryAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SkipAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task<int> ResetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
