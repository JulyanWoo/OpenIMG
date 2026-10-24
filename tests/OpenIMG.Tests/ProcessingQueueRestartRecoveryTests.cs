using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIMG.Application.Abstractions.Orchestration;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Orchestration;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using OpenIMG.Infrastructure.Persistence;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class ProcessingQueueRestartRecoveryTests
{
    [Fact]
    public async Task StoppingQueueKeepsInterruptedImageResumableAndDoesNotReprocessCompletedImages()
    {
        var project = new Project { Name = "Restart recovery" };
        var completed = NewAsset(project.Id, "00-completed.jpg", ImageProcessingStatus.Accepted);
        var interrupted = NewAsset(project.Id, "10-interrupted.jpg", ImageProcessingStatus.Restoring);
        var pending = NewAsset(project.Id, "20-pending.jpg", ImageProcessingStatus.Pending);
        var repository = new MemoryAssetRepository([completed, interrupted, pending]);
        var projects = new SingleProjectRepository(project);
        var interruptingOrchestrator = new BlockingOrchestrator();
        var firstQueue = new ProcessingQueue(interruptingOrchestrator, repository, projects, NullLogger<ProcessingQueue>.Instance);

        await firstQueue.StartAsync(project.Id);
        await interruptingOrchestrator.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await firstQueue.StopAsync();

        Assert.Equal(ImageProcessingStatus.Pending, repository.GetPersistedStatus(interrupted.Id));
        Assert.Equal(ImageProcessingStatus.Pending, repository.GetPersistedStatus(pending.Id));
        Assert.Equal(ImageProcessingStatus.Accepted, repository.GetPersistedStatus(completed.Id));

        var resumedIds = new ConcurrentBag<Guid>();
        var resumedQueue = new ProcessingQueue(
            new CompletingOrchestrator(repository, resumedIds),
            repository,
            projects,
            NullLogger<ProcessingQueue>.Instance);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resumedQueue.StateChanged += (_, args) =>
        {
            if (args.Message == "Queue finished or idle.") finished.TrySetResult();
        };

        try
        {
            await resumedQueue.StartAsync(project.Id);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.DoesNotContain(completed.Id, resumedIds);
            Assert.Contains(interrupted.Id, resumedIds);
            Assert.Contains(pending.Id, resumedIds);
            Assert.Equal(2, resumedIds.Count);
        }
        finally
        {
            await resumedQueue.StopAsync();
        }
    }

    [Fact]
    public async Task StoppingDuringPossiblySubmittedGenerationRoutesImageToReviewInsteadOfRetrying()
    {
        var project = new Project { Name = "Ambiguous operation" };
        var asset = NewAsset(project.Id, "ambiguous.jpg", ImageProcessingStatus.Restoring);
        var job = new ProcessingJob { ImageAssetId = asset.Id, Status = ImageProcessingStatus.Restoring };
        job.Attempts.Add(new RestorationAttempt
        {
            ProcessingJobId = job.Id,
            ProviderOperationState = ProviderOperationState.NeedsRecovery,
            CompletedAt = DateTimeOffset.UtcNow
        });
        asset.ProcessingJobs.Add(job);
        var repository = new MemoryAssetRepository([asset]);
        var orchestrator = new BlockingOrchestrator();
        var queue = new ProcessingQueue(orchestrator, repository, new SingleProjectRepository(project), NullLogger<ProcessingQueue>.Instance);

        try
        {
            await queue.StartAsync(project.Id);
            await orchestrator.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await queue.StopAsync();

            Assert.Equal(ImageProcessingStatus.NeedsReview, repository.GetPersistedStatus(asset.Id));
        }
        finally
        {
            await queue.StopAsync();
        }
    }

    [Fact]
    public async Task ProviderWarningPausesQueueBeforeTheNextImageIsSubmitted()
    {
        var project = new Project { Name = "Provider warning stop" };
        var first = NewAsset(project.Id, "00-warning.jpg", ImageProcessingStatus.Pending);
        var second = NewAsset(project.Id, "10-next.jpg", ImageProcessingStatus.Pending);
        var repository = new MemoryAssetRepository([first, second]);
        var orchestrator = new ProviderWarningOrchestrator();
        var queue = new ProcessingQueue(orchestrator, repository, new SingleProjectRepository(project), NullLogger<ProcessingQueue>.Instance);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.StateChanged += (_, args) =>
        {
            if (args.IsPaused)
                paused.TrySetResult();
        };

        try
        {
            await queue.StartAsync(project.Id);
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(queue.IsRunning);
            Assert.True(queue.IsPaused);
            Assert.Null(queue.CurrentProcessingAsset);
            Assert.Equal(1, orchestrator.ProcessedCount);
            Assert.Equal(ImageProcessingStatus.Pending, repository.GetPersistedStatus(second.Id));
        }
        finally
        {
            await queue.StopAsync();
        }
    }

    private static ImageAsset NewAsset(Guid projectId, string fileName, ImageProcessingStatus status) => new()
    {
        ProjectId = projectId,
        OriginalFileName = fileName,
        OriginalFilePath = Path.Combine(Path.GetTempPath(), fileName),
        Status = status
    };

    private sealed class BlockingOrchestrator : IImageProcessingOrchestrator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessingResult> ProcessAssetAsync(Project project, ImageAsset asset, Action<string>? progressReporter = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking test operation should only finish by cancellation.");
        }
    }

    private sealed class CompletingOrchestrator(MemoryAssetRepository repository, ConcurrentBag<Guid> processedIds) : IImageProcessingOrchestrator
    {
        public async Task<ProcessingResult> ProcessAssetAsync(Project project, ImageAsset asset, Action<string>? progressReporter = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedIds.Add(asset.Id);
            asset.Status = ImageProcessingStatus.Accepted;
            await repository.UpdateAsync(asset, cancellationToken);
            return new ProcessingResult { Success = true, Decision = ValidationDecision.Accepted };
        }
    }

    private sealed class ProviderWarningOrchestrator : IImageProcessingOrchestrator
    {
        public int ProcessedCount { get; private set; }

        public Task<ProcessingResult> ProcessAssetAsync(Project project, ImageAsset asset, Action<string>? progressReporter = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessedCount++;
            return Task.FromResult(new ProcessingResult
            {
                Success = false,
                Decision = ValidationDecision.NeedsReview,
                ErrorMessage = "GPT provider warning observed.",
                QueuePauseRequested = true
            });
        }
    }

    private sealed class SingleProjectRepository(Project project) : IProjectRepository
    {
        public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(id == project.Id ? project : null);
        public Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Project>>([project]);
        public Task<Project> CreateAsync(Project value, CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task UpdateAsync(Project value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryAssetRepository : IImageAssetRepository
    {
        private readonly Dictionary<Guid, ImageAsset> _assets;

        public MemoryAssetRepository(IEnumerable<ImageAsset> assets) => _assets = assets.ToDictionary(asset => asset.Id, Clone);

        public ImageProcessingStatus GetPersistedStatus(Guid id) => _assets[id].Status;

        public Task<IReadOnlyList<ImageAsset>> GetByProjectIdAsync(Guid projectId, ImageProcessingStatus? statusFilter = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ImageAsset> result = _assets.Values
                .Where(asset => asset.ProjectId == projectId && (!statusFilter.HasValue || asset.Status == statusFilter.Value))
                .OrderBy(asset => asset.OriginalFileName, StringComparer.Ordinal)
                .Select(Clone)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task UpdateAsync(ImageAsset asset, CancellationToken cancellationToken = default)
        {
            _assets[asset.Id].Status = asset.Status;
            return Task.CompletedTask;
        }

        public Task<ImageAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_assets.TryGetValue(id, out var asset) ? Clone(asset) : null);
        public Task<ImageAsset?> GetByHashAsync(Guid projectId, string sha256Hash, CancellationToken cancellationToken = default) => Task.FromResult<ImageAsset?>(null);
        public Task<int> ResetProjectProcessingStateAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<ImageAsset> AddAsync(ImageAsset asset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddRangeAsync(IEnumerable<ImageAsset> assets, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProcessingJob> CreateJobAsync(Guid assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProcessingJob?> GetLatestJobForAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateJobAsync(ProcessingJob job, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RestorationAttempt> AddAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RestorationAttempt>> GetAttemptsForJobAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveIncompleteAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static ImageAsset Clone(ImageAsset asset) => new()
        {
            Id = asset.Id,
            ProjectId = asset.ProjectId,
            OriginalFileName = asset.OriginalFileName,
            OriginalFilePath = asset.OriginalFilePath,
            Sha256Hash = asset.Sha256Hash,
            Status = asset.Status,
            ProcessingJobs = asset.ProcessingJobs.Select(Clone).ToList()
        };

        private static ProcessingJob Clone(ProcessingJob job)
        {
            var clone = new ProcessingJob
            {
                Id = job.Id,
                ImageAssetId = job.ImageAssetId,
                Status = job.Status,
                AttemptCount = job.AttemptCount,
                CreatedAt = job.CreatedAt,
                UpdatedAt = job.UpdatedAt
            };
            foreach (var attempt in job.Attempts)
                clone.Attempts.Add(new RestorationAttempt
                {
                    Id = attempt.Id,
                    ProcessingJobId = attempt.ProcessingJobId,
                    ProviderOperationState = attempt.ProviderOperationState,
                    CompletedAt = attempt.CompletedAt
                });
            return clone;
        }
    }
}

[Trait("Category", TestCategories.Integration)]
public sealed class ProcessingQueueRestartPersistenceTests
{
    [Fact]
    public async Task InterruptedCheckpointAndAssetStateSurviveDatabaseReopen()
    {
        var workspace = TestArtifactWorkspace.Create("queue-restart-persistence");
        var databasePath = Path.Combine(workspace, "restart.db");
        var connectionString = $"Data Source={databasePath}";
        var factory = new TestDbContextFactory(connectionString);
        var projectId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        try
        {
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
                context.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Restart persistence test",
                    SourceDirectory = Path.Combine(workspace, "source"),
                    OutputDirectory = Path.Combine(workspace, "output")
                });
                context.ImageAssets.Add(new ImageAsset
                {
                    Id = assetId,
                    ProjectId = projectId,
                    OriginalFileName = "interrupted.jpg",
                    OriginalFilePath = Path.Combine(workspace, "source", "interrupted.jpg"),
                    Sha256Hash = new string('a', 64),
                    Status = ImageProcessingStatus.Restoring,
                    ProcessingJobs =
                    [
                        new ProcessingJob
                        {
                            Id = jobId,
                            Status = ImageProcessingStatus.Restoring,
                            CurrentStep = "Generating GPT restoration",
                            Attempts =
                            [
                                new RestorationAttempt
                                {
                                    Id = attemptId,
                                    AttemptNumber = 1,
                                    ProviderOperationId = "restart-test-operation",
                                    ProviderOperationState = ProviderOperationState.GenerationPrepared
                                }
                            ]
                        }
                    ]
                });
                await context.SaveChangesAsync();
            }

            // A new repository/context factory read models the next application process.
            var afterRestart = await new ImageAssetRepository(new TestDbContextFactory(connectionString))
                .GetByProjectIdAsync(projectId);

            var persisted = Assert.Single(afterRestart);
            Assert.Equal(assetId, persisted.Id);
            Assert.Equal(ImageProcessingStatus.Restoring, persisted.Status);
            var job = Assert.Single(persisted.ProcessingJobs);
            Assert.Equal("Generating GPT restoration", job.CurrentStep);
            var attempt = Assert.Single(job.Attempts);
            Assert.Equal(attemptId, attempt.Id);
            Assert.Equal("restart-test-operation", attempt.ProviderOperationId);
            Assert.Equal(ProviderOperationState.GenerationPrepared, attempt.ProviderOperationState);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<OpenImgDbContext>
    {
        public OpenImgDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<OpenImgDbContext>().UseSqlite(connectionString).Options);

        public Task<OpenImgDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
