using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;
using OpenIMG.Application.Restoration;
using OpenIMG.App.ViewModels;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using OpenIMG.Infrastructure.Persistence;
using OpenIMG.Infrastructure.Storage;

namespace OpenIMG.Tests;

public sealed class HistoricalQuickActionsTests
{
    [Theory]
    [Trait("Category", TestCategories.Integration)]
    [InlineData("LegacyBlocked", ImageRetryResult.ManualRecoveryRequired)]
    [InlineData("LegacyReplacement", ImageRetryResult.Queued)]
    [InlineData("Recoverable", ImageRetryResult.Queued)]
    [InlineData("EncodedRecoverable", ImageRetryResult.Queued)]
    [InlineData("Failed", ImageRetryResult.Queued)]
    [InlineData("InProgress", ImageRetryResult.InProgress)]
    [InlineData("MissingSource", ImageRetryResult.MissingSource)]
    public async Task RequeueWorksOnExistingHistoricalDatabaseRowsAndPreservesEvidence(string scenario, ImageRetryResult expected)
    {
        var root = TestArtifactWorkspace.Create("historical-quick-actions");
        try
        {
            var factory = new Factory(Path.Combine(root, "history.db"));
            var source = Path.Combine(root, "original.png");
            await File.WriteAllTextAsync(source, "source remains immutable");
            var project = new Project { Name = "Existing library", SourceDirectory = root, OutputDirectory = Path.Combine(root, "results") };
            var asset = new ImageAsset
            {
                ProjectId = project.Id, OriginalFileName = "original.png", OriginalFilePath = source,
                Sha256Hash = "SOURCE", Status = scenario == "InProgress" ? ImageProcessingStatus.Restoring : ImageProcessingStatus.NeedsReview,
                CreatedAt = DateTimeOffset.Parse("2020-01-01T00:00:00Z")
            };
            var job = new ProcessingJob { ImageAssetId = asset.Id, AttemptCount = 1, CreatedAt = asset.CreatedAt };
            var attempt = new RestorationAttempt
            {
                ProcessingJobId = job.Id, ProviderOperationId = "historical-operation", RestorationPrompt = "Historical prompt",
                HumanReviewJson = "preserved historical human notes", AttemptNumber = 1, CreatedAt = asset.CreatedAt,
                ProviderOperationState = scenario == "Failed" ? ProviderOperationState.Failed : ProviderOperationState.NeedsRecovery
            };
            if (scenario is "Recoverable" or "EncodedRecoverable")
                attempt.RawResponse = GenerationRecoveryCheckpoint.Capture(new GenerationOwnershipContext
                {
                    GenerationOperationId = attempt.ProviderOperationId!, AssetId = asset.Id.ToString(),
                    ExpectedSourceSha256 = asset.Sha256Hash, ConversationPathAfterSubmit = "/c/historical-conversation"
                }, attempt.RestorationPrompt);
            if (scenario == "EncodedRecoverable")
            {
                var legacy = JsonSerializer.Deserialize<GenerationRecoveryCheckpoint>(attempt.RawResponse!)!;
                attempt.RawResponse = JsonSerializer.Serialize(legacy with { ConversationPath = "/c/historical%2Dconversation" });
            }
            job.Attempts.Add(attempt);
            asset.ProcessingJobs.Add(job);
            await using (var setup = factory.CreateDbContext())
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Projects.Add(project);
                setup.ImageAssets.Add(asset);
                await setup.SaveChangesAsync();
            }
            if (scenario == "MissingSource") File.Delete(source);
            var repository = new ImageAssetRepository(factory);
            var result = await new ImageRetryService(repository).EnqueueAsync(asset.Id, scenario == "LegacyReplacement");
            Assert.Equal(expected, result);
            var saved = (await repository.GetByIdAsync(asset.Id))!;
            var savedAttempt = Assert.Single(Assert.Single(saved.ProcessingJobs).Attempts);
            Assert.Equal(attempt.Id, savedAttempt.Id);
            Assert.Equal("Historical prompt", savedAttempt.RestorationPrompt);
            Assert.Equal("preserved historical human notes", savedAttempt.HumanReviewJson);
            Assert.Equal(expected == ImageRetryResult.Queued ? ImageProcessingStatus.Pending : asset.Status, saved.Status);
            Assert.Equal(scenario == "LegacyReplacement" ? ProviderOperationState.Failed : attempt.ProviderOperationState, savedAttempt.ProviderOperationState);
            if (scenario is "Recoverable" or "EncodedRecoverable") Assert.NotNull(GenerationRecoveryCheckpoint.Restore(savedAttempt, saved, saved.Sha256Hash));
            if (scenario != "MissingSource") Assert.Equal("source remains immutable", await File.ReadAllTextAsync(source));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [Trait("Category", TestCategories.Unit)]
    [InlineData(true)]
    [InlineData(false)]
    public void HistoricalNeedsReviewIsClassifiedByActualFileAvailability(bool exists)
    {
        var root = TestArtifactWorkspace.Create("historical-result-state");
        try
        {
            var candidate = Path.Combine(root, "candidate.png");
            if (exists) File.WriteAllBytes(candidate, [1, 2, 3]);
            var asset = HistoricalAsset(root, candidate);
            var item = new AssetItemViewModel(asset);
            Assert.Equal(!exists, item.WorkState.MissingResult);
            Assert.Equal(exists, item.WorkState.HasCurrentResult);
            Assert.Equal(exists ? "Por revisar" : "Sin resultado", item.StatusLabel);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", TestCategories.Integration)]
    public async Task HistoricalResultsAreCopiedOnceWithProvenanceWithoutApprovalOrOverwrite()
    {
        var root = TestArtifactWorkspace.Create("historical-result-copies");
        try
        {
            var sourceFolder = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceFolder);
            var original = Path.Combine(sourceFolder, "original.png");
            var candidate = Path.Combine(root, "result.png");
            using (var image = new Mat(24, 32, MatType.CV_8UC3, Scalar.White)) image.SaveImage(candidate);
            File.Copy(candidate, original);
            var asset = HistoricalAsset(sourceFolder, candidate);
            asset.OriginalFilePath = original;
            var files = new FileSystemService();
            var attempt = ImageWorkState.Read(asset).LatestAttempt!;
            attempt.GeneratedSha256 = await files.ComputeSha256Async(candidate);
            var copies = new ResultCopyService(files);
            var destination = Path.Combine(root, "copies");
            var results = await Task.WhenAll(copies.SaveAsync(asset, destination), copies.SaveAsync(asset, destination));
            Assert.Single(results, saved => saved);
            var savedImage = Assert.Single(Directory.GetFiles(destination, "*.png"));
            Assert.Equal(attempt.GeneratedSha256, await files.ComputeSha256Async(savedImage));
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(savedImage + ".openimg.json"));
            Assert.Equal("ReviewCopy", metadata.RootElement.GetProperty("purpose").GetString());
            Assert.Equal(attempt.Id, metadata.RootElement.GetProperty("attemptId").GetGuid());
            Assert.Null(attempt.HumanReviewJson);
            Assert.Equal(ImageProcessingStatus.NeedsReview, asset.Status);
            Assert.Equal(attempt.GeneratedSha256, await files.ComputeSha256Async(original));
            await File.WriteAllTextAsync(savedImage, "pre-existing changed copy");
            await Assert.ThrowsAsync<IOException>(() => copies.SaveAsync(asset, destination));
            Assert.Equal("pre-existing changed copy", await File.ReadAllTextAsync(savedImage));
            await Assert.ThrowsAsync<InvalidOperationException>(() => copies.SaveAsync(asset, sourceFolder));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", TestCategories.Unit)]
    public void EmptyHistoricalDownloadIsMissingInsteadOfAReviewableResult()
    {
        var root = TestArtifactWorkspace.Create("empty-historical-download");
        try
        {
            var candidate = Path.Combine(root, "empty.png");
            File.WriteAllBytes(candidate, []);
            var item = new AssetItemViewModel(HistoricalAsset(root, candidate));
            Assert.True(item.WorkState.MissingResult);
            Assert.False(item.WorkState.HasCurrentResult);
            Assert.Null(item.RestoredImagePath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", TestCategories.Integration)]
    public async Task HistoricalPreviousResultCanBeSavedWhileLatestFailedAttemptRemainsIdentifiable()
    {
        var root = TestArtifactWorkspace.Create("previous-result-copy");
        try
        {
            var candidate = Path.Combine(root, "old.png");
            using (var image = new Mat(24, 32, MatType.CV_8UC3, Scalar.White)) image.SaveImage(candidate);
            var asset = HistoricalAsset(Path.Combine(root, "source"), candidate);
            asset.ProcessingJobs.First().Attempts.Add(new RestorationAttempt { CreatedAt = DateTimeOffset.UtcNow, ProviderOperationState = ProviderOperationState.Failed });
            Assert.True(ImageWorkState.Read(asset).MissingResult);
            var destination = Path.Combine(root, "copies");
            Assert.True(await new ResultCopyService(new FileSystemService()).SaveAsync(asset, destination));
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(destination, "*.json"))));
            Assert.False(metadata.RootElement.GetProperty("isLatestAttempt").GetBoolean());
            Assert.True(ImageWorkState.Read(asset).MissingResult);
        }
        finally { Directory.Delete(root, true); }
    }

    private static ImageAsset HistoricalAsset(string sourceFolder, string candidate)
    {
        var oldDate = DateTimeOffset.Parse("2019-01-01T00:00:00Z");
        return new ImageAsset
        {
            OriginalFileName = "original.png", OriginalFilePath = Path.Combine(sourceFolder, "original.png"),
            Status = ImageProcessingStatus.NeedsReview, CreatedAt = oldDate,
            ProcessingJobs = [new ProcessingJob { CreatedAt = oldDate, Attempts = [new RestorationAttempt
            {
                GeneratedImagePath = candidate, CreatedAt = oldDate, CompletedAt = oldDate, FinalDecision = ValidationDecision.NeedsReview
            }] }]
        };
    }

    private sealed class Factory(string path) : IDbContextFactory<OpenImgDbContext>
    {
        public OpenImgDbContext CreateDbContext() => new(new DbContextOptionsBuilder<OpenImgDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        public Task<OpenImgDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
