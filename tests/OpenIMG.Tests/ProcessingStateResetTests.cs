using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using OpenIMG.Application.Abstractions.Orchestration;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Orchestration;
using Microsoft.EntityFrameworkCore;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Infrastructure.Persistence;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Integration)]
public sealed class ProcessingStateResetTests
{
    [Fact]
    public async Task ResetReturnsProjectAssetsToPendingAndPreservesOperationHistoryAndFiles()
    {
        var workspace = TestArtifactWorkspace.Create(nameof(ResetReturnsProjectAssetsToPendingAndPreservesOperationHistoryAndFiles));
        var databasePath = Path.Combine(workspace, "reset.db");
        var factory = new TestDbContextFactory($"Data Source={databasePath}");

        try
        {
            await using (var setup = await factory.CreateDbContextAsync())
                await setup.Database.EnsureCreatedAsync();

            var project = new Project
            {
                Name = "Reset test",
                SourceDirectory = Path.Combine(workspace, "source"),
                OutputDirectory = Path.Combine(workspace, "output")
            };
            var otherProject = new Project
            {
                Name = "Other project",
                SourceDirectory = Path.Combine(workspace, "other-source"),
                OutputDirectory = Path.Combine(workspace, "other-output")
            };
            var sourcePath = Path.Combine(workspace, "original.jpg");
            var candidatePath = Path.Combine(workspace, "preserved-candidate.png");
            const string candidateContents = "candidate owned by historical GPT operation";
            await File.WriteAllTextAsync(sourcePath, "immutable original");
            await File.WriteAllTextAsync(candidatePath, candidateContents);

            var completedAsset = new ImageAsset
            {
                ProjectId = project.Id,
                OriginalFileName = "completed.jpg",
                OriginalFilePath = sourcePath,
                Sha256Hash = "SOURCE-HASH",
                Status = ImageProcessingStatus.NeedsReview
            };
            var completedJob = new ProcessingJob
            {
                ImageAssetId = completedAsset.Id,
                Status = ImageProcessingStatus.NeedsReview,
                AttemptCount = 1,
                CurrentStep = "Rejected by Barrier 3",
                ErrorMessage = "Historical decision",
                FailureReason = RestorationFailureReason.Unknown,
                LocalFurnitureContractJson = "cached source analysis"
            };
            completedJob.Attempts.Add(new RestorationAttempt
            {
                ProcessingJobId = completedJob.Id,
                AttemptNumber = 1,
                ProviderOperationId = "owned-operation-1",
                ProviderOperationState = ProviderOperationState.Finalized,
                GeneratedImagePath = candidatePath,
                GeneratedSha256 = "CANDIDATE-HASH",
                RestorationPrompt = "historical prompt",
                FinalDecision = ValidationDecision.Rejected,
                HumanReviewJson = "human review remains historical",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            completedAsset.ProcessingJobs.Add(completedJob);

            var ambiguousAsset = new ImageAsset
            {
                ProjectId = project.Id,
                OriginalFileName = "incomplete.jpg",
                OriginalFilePath = sourcePath,
                Sha256Hash = "SOURCE-HASH-2",
                Status = ImageProcessingStatus.Failed
            };
            var ambiguousJob = new ProcessingJob
            {
                ImageAssetId = ambiguousAsset.Id,
                Status = ImageProcessingStatus.Failed,
                AttemptCount = 1,
                ErrorMessage = "Generation may still exist"
            };
            ambiguousJob.Attempts.Add(new RestorationAttempt
            {
                ProcessingJobId = ambiguousJob.Id,
                AttemptNumber = 1,
                ProviderOperationId = "owned-operation-incomplete",
                ProviderOperationState = ProviderOperationState.GenerationMayExist,
                FailureDetails = "Recovery required before another GPT submission"
            });
            ambiguousAsset.ProcessingJobs.Add(ambiguousJob);

            var unrelatedAsset = new ImageAsset
            {
                ProjectId = otherProject.Id,
                OriginalFileName = "other.jpg",
                OriginalFilePath = sourcePath,
                Sha256Hash = "OTHER-HASH",
                Status = ImageProcessingStatus.Accepted
            };
            unrelatedAsset.ProcessingJobs.Add(new ProcessingJob
            {
                ImageAssetId = unrelatedAsset.Id,
                Status = ImageProcessingStatus.Accepted,
                AttemptCount = 3
            });

            await using (var setup = await factory.CreateDbContextAsync())
            {
                setup.Projects.AddRange(project, otherProject);
                setup.ImageAssets.AddRange(completedAsset, ambiguousAsset, unrelatedAsset);
                await setup.SaveChangesAsync();
            }

            var repository = new ImageAssetRepository(factory);
            var resetCount = await repository.ResetProjectProcessingStateAsync(project.Id);

            Assert.Equal(2, resetCount);
            var resetAssets = await repository.GetByProjectIdAsync(project.Id);
            Assert.All(resetAssets, asset => Assert.Equal(ImageProcessingStatus.Pending, asset.Status));

            var resetCompleted = Assert.Single(resetAssets, asset => asset.OriginalFileName == "completed.jpg");
            Assert.Equal(2, resetCompleted.ProcessingJobs.Count);
            var resetCompletedJob = resetCompleted.ProcessingJobs.OrderByDescending(job => job.CreatedAt).First();
            Assert.Equal(ImageProcessingStatus.Pending, resetCompletedJob.Status);
            Assert.Equal(0, resetCompletedJob.AttemptCount);
            Assert.Empty(resetCompletedJob.Attempts);

            var historicalJob = Assert.Single(resetCompleted.ProcessingJobs, job => job.Id == completedJob.Id);
            Assert.Equal(ImageProcessingStatus.NeedsReview, historicalJob.Status);
            Assert.Equal("cached source analysis", historicalJob.LocalFurnitureContractJson);
            var preservedAttempt = Assert.Single(historicalJob.Attempts);
            Assert.Equal("owned-operation-1", preservedAttempt.ProviderOperationId);
            Assert.Equal(ProviderOperationState.Finalized, preservedAttempt.ProviderOperationState);
            Assert.Equal(candidatePath, preservedAttempt.GeneratedImagePath);
            Assert.Equal("historical prompt", preservedAttempt.RestorationPrompt);
            Assert.Equal("human review remains historical", preservedAttempt.HumanReviewJson);
            Assert.Equal(candidateContents, await File.ReadAllTextAsync(candidatePath));
            Assert.Equal("immutable original", await File.ReadAllTextAsync(sourcePath));

            var resetAmbiguous = Assert.Single(resetAssets, asset => asset.OriginalFileName == "incomplete.jpg");
            var recoveryJob = Assert.Single(resetAmbiguous.ProcessingJobs);
            Assert.Equal(ImageProcessingStatus.Pending, recoveryJob.Status);
            var preservedIncompleteAttempt = Assert.Single(recoveryJob.Attempts);
            Assert.Null(preservedIncompleteAttempt.CompletedAt);
            Assert.Equal("owned-operation-incomplete", preservedIncompleteAttempt.ProviderOperationId);
            Assert.Equal(ProviderOperationState.GenerationMayExist, preservedIncompleteAttempt.ProviderOperationState);

            var untouched = Assert.Single(await repository.GetByProjectIdAsync(otherProject.Id));
            Assert.Equal(ImageProcessingStatus.Accepted, untouched.Status);
            Assert.Equal(ImageProcessingStatus.Accepted, Assert.Single(untouched.ProcessingJobs).Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ResetStopsAndWaitsForActiveQueueOperationBeforeChangingPersistence()
    {
        var project = new Project { Name = "Queue reset", OutputDirectory = Path.GetTempPath() };
        var asset = new ImageAsset
        {
            ProjectId = project.Id,
            OriginalFileName = "active.jpg",
            OriginalFilePath = Path.Combine(Path.GetTempPath(), "active.jpg"),
            Sha256Hash = "ACTIVE-HASH",
            Status = ImageProcessingStatus.Pending
        };
        var orchestrator = new BlockingOrchestrator();
        var repository = new QueueResetRepository([asset], () => orchestrator.Completed);
        var queue = new ProcessingQueue(
            orchestrator,
            repository,
            new SingleProjectRepository(project),
            NullLogger<ProcessingQueue>.Instance);

        try
        {
            await queue.StartAsync(project.Id);
            await orchestrator.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var resetTask = queue.ResetProjectAsync(project.Id);
            await orchestrator.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(resetTask.IsCompleted);
            Assert.Equal(0, repository.ResetCallCount);
            Assert.True(queue.IsRunning);
            await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StartAsync(project.Id));

            orchestrator.AllowCompletion.TrySetResult();
            var resetCount = await resetTask.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(1, resetCount);
            Assert.True(orchestrator.Completed);
            Assert.True(repository.ResetWasCalledAfterOperationCompleted);
            Assert.Equal(1, repository.ResetCallCount);
            Assert.Equal(ImageProcessingStatus.Pending, asset.Status);
            Assert.False(queue.IsRunning);
            Assert.Equal(0, queue.TotalItems);
            Assert.Equal(0, queue.ProcessedItems);
        }
        finally
        {
            orchestrator.AllowCompletion.TrySetResult();
            await queue.StopAsync();
        }
    }

    private sealed class BlockingOrchestrator : IImageProcessingOrchestrator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Completed { get; private set; }

        public async Task<OpenIMG.Core.Models.ProcessingResult> ProcessAssetAsync(
            Project project,
            ImageAsset asset,
            Action<string>? progressReporter = null,
            CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            Started.TrySetResult();
            await AllowCompletion.Task;
            Completed = true;
            return new OpenIMG.Core.Models.ProcessingResult { Success = true, Decision = ValidationDecision.Accepted };
        }
    }

    private sealed class SingleProjectRepository(Project project) : IProjectRepository
    {
        public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Project?>(id == project.Id ? project : null);
        public Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Project>>([project]);
        public Task<Project> CreateAsync(Project value, CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task UpdateAsync(Project value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class QueueResetRepository(
        IReadOnlyList<ImageAsset> assets,
        Func<bool> isOperationComplete) : IImageAssetRepository
    {
        public int ResetCallCount { get; private set; }
        public bool ResetWasCalledAfterOperationCompleted { get; private set; }

        public Task<int> ResetProjectProcessingStateAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            ResetCallCount++;
            ResetWasCalledAfterOperationCompleted = isOperationComplete();
            var matching = assets.Where(asset => asset.ProjectId == projectId).ToArray();
            foreach (var asset in matching)
                asset.Status = ImageProcessingStatus.Pending;
            return Task.FromResult(matching.Length);
        }

        public Task<ImageAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(assets.FirstOrDefault(asset => asset.Id == id));
        public Task<ImageAsset?> GetByHashAsync(Guid projectId, string sha256Hash, CancellationToken cancellationToken = default) =>
            Task.FromResult(assets.FirstOrDefault(asset => asset.ProjectId == projectId && asset.Sha256Hash == sha256Hash));
        public Task<IReadOnlyList<ImageAsset>> GetByProjectIdAsync(Guid projectId, ImageProcessingStatus? statusFilter = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ImageAsset>>(assets.Where(asset => asset.ProjectId == projectId && (!statusFilter.HasValue || asset.Status == statusFilter)).ToArray());
        public Task<ImageAsset> AddAsync(ImageAsset asset, CancellationToken cancellationToken = default) => Task.FromResult(asset);
        public Task AddRangeAsync(IEnumerable<ImageAsset> values, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(ImageAsset asset, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OpenIMG.Core.Entities.ProcessingJob> CreateJobAsync(Guid assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OpenIMG.Core.Entities.ProcessingJob?> GetLatestJobForAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.FromResult<OpenIMG.Core.Entities.ProcessingJob?>(null);
        public Task UpdateJobAsync(OpenIMG.Core.Entities.ProcessingJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RestorationAttempt> AddAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default) => Task.FromResult(attempt);
        public Task UpdateAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RestorationAttempt>> GetAttemptsForJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RestorationAttempt>>([]);
        public Task RemoveIncompleteAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<OpenImgDbContext>
    {
        private DbContextOptions<OpenImgDbContext> CreateOptions() =>
            new DbContextOptionsBuilder<OpenImgDbContext>().UseSqlite(connectionString).Options;

        public OpenImgDbContext CreateDbContext() => new(CreateOptions());

        public Task<OpenImgDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
