using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using OpenIMG.Application.Abstractions.AI;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Abstractions.Storage;
using OpenIMG.Application.Abstractions.Validation;
using OpenIMG.Application.Analysis;
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Orchestration;
using OpenIMG.Application.Prompts;
using OpenIMG.Application.Validation;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Integration)]
public class ShadowModeIsolationTests
{
[Trait("Category", TestCategories.Calibration)]
    private class FakeAiProvider : IImageGenerationProvider
    {
        public int GenerationCallCount { get; private set; }
        public string? LastPrompt { get; private set; }
        public FurnitureAnalysis BaselineAnalysis { get; set; } = new()
        {
            FurnitureType = "upper kitchen cabinet",
            Confidence = 0.96,
            MainMaterials = new List<string> { "melamine" },
            MainColors = new List<string> { "white" },
            AccentColors = new List<string> { "silver" },
            Components = new List<FurnitureComponent>
            {
                new()
                {
                    Id = "door_1",
                    Type = "door",
                    Description = "White melamine door",
                    Region = new ProtectedRegion { X = 0.1, Y = 0.1, Width = 0.8, Height = 0.8, RegionType = "door", Importance = 1.0 }
                }
            },
            ProtectedRegions = new List<ProtectedRegion>
            {
                new() { X = 0.1, Y = 0.1, Width = 0.8, Height = 0.8, RegionType = "door", Importance = 1.0 }
            }
        };

        public Task<string> GenerateRestorationAsync(string imagePath, string restorationPrompt, string tempDownloadDirectory, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(tempDownloadDirectory);
            var dest = Path.Combine(tempDownloadDirectory, "restored.jpg");
            File.Copy(imagePath, dest, overwrite: true);
            return Task.FromResult(dest);
        }

        public Task<string> GenerateRestorationScopedAsync(string imagePath, string restorationPrompt, string tempDownloadDirectory, GenerationOwnershipContext context, CancellationToken cancellationToken = default)
        {
            GenerationCallCount++;
            LastPrompt = restorationPrompt;
            var opDir = Path.Combine(tempDownloadDirectory, context.GenerationOperationId);
            Directory.CreateDirectory(opDir);
            var dest = Path.Combine(opDir, "candidate.jpg");
            File.Copy(imagePath, dest, overwrite: true);
            context.DownloadOwnedByCurrentOperation = true;
            context.DownloadedTempPath = dest;
            return Task.FromResult(dest);
        }

        public Task<string> GenerateRestorationMultiViewScopedAsync(IReadOnlyList<string> imagePaths, string restorationPrompt, string tempDownloadDirectory, GenerationOwnershipContext context, CancellationToken cancellationToken = default)
        {
            if (imagePaths.Count == 0) throw new ArgumentException("At least one image is required.", nameof(imagePaths));
            return GenerateRestorationScopedAsync(imagePaths[0], restorationPrompt, tempDownloadDirectory, context, cancellationToken);
        }

    }

[Trait("Category", TestCategories.Calibration)]
    private class CorruptedLocalAnalysisEngine : ILocalAnalysisEngine
    {
        public Task<LocalFurnitureContract> AnalyzeAsync(string imagePath, CancellationToken cancellationToken = default)
        {
            // Intentionally return completely incorrect, corrupted data
            return Task.FromResult(new LocalFurnitureContract
            {
                SourceFileName = Path.GetFileName(imagePath),
                SourceSha256 = "COMPLETELY_WRONG_HASH",
                Status = LocalAnalysisStatus.Success,
                FurnitureBounds = new NormalizedRectangle(0.95, 0.95, 0.02, 0.02), // wrong corner box
                FurnitureConfidence = 0.05,
                LocalConfidence = 0.08,
                SceneComplexity = 0.99,
                DetectedObjects = new List<LocalDetectedObject>
                {
                    new() { Id = "fake_1", ClassName = "alien_spaceship", Confidence = 0.02 }
                },
                StructuralRegions = new List<LocalStructuralRegion>
                {
                    new() { Id = "fake_struct", Region = new NormalizedRectangle(0.95, 0.95, 0.01, 0.01), RegionType = StructuralRegionType.UnknownStructuralRegion, Confidence = 0.01 }
                },
                HardwareCandidates = new List<HardwareCandidate>(),
                ColorRegions = new List<LocalColorRegion>
                {
                    new() { Region = new NormalizedRectangle(0.0, 0.0, 1.0, 1.0), RepresentativeRgb = "#FF00FF", DominantLab = new LabColor(10, 80, -60), AreaPercentage = 1.0, IsDominant = true }
                },
                MaterialHints = new List<LocalMaterialHint>
                {
                    new() { Material = MaterialHintType.Unknown, Confidence = 0.01, Rationale = "Totally wrong" }
                },
                Warnings = new List<string> { "Simulated corrupted local analysis data" }
            });
        }
    }

    private sealed class ValidLocalAnalysisEngine : ILocalAnalysisEngine
    {
        public Task<LocalFurnitureContract> AnalyzeAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalFurnitureContract
            {
                SourceFileName = Path.GetFileName(imagePath),
                SourceSha256 = "A1B2C3D4E5F6",
                AuthorityPolicyVersion = LocalContractConflictDetector.CurrentAuthorityPolicyVersion,
                Status = LocalAnalysisStatus.Success,
                FurnitureBounds = new NormalizedRectangle(0.1, 0.1, 0.8, 0.8),
                FurnitureConfidence = 0.92,
                FurnitureEnvelopeAuthority = ConstraintAuthority.Strict,
                LocalConfidence = 0.90,
                StructuralRegions =
                [
                    new LocalStructuralRegion
                    {
                        Id = "panel-main",
                        Region = new NormalizedRectangle(0.12, 0.12, 0.76, 0.76),
                        RegionType = StructuralRegionType.UnknownStructuralRegion,
                        Confidence = 0.9
                    }
                ]
            });
    }

[Trait("Category", TestCategories.Calibration)]
    private class FakeValidator : ILocalImageValidator
    {
        public FurnitureAnalysis? ReceivedAnalysis { get; private set; }

        public Task<LocalValidationReport> ValidateAsync(
            string originalImagePath,
            string restoredImagePath,
            FurnitureAnalysis analysis,
            string diagnosticsDirectory,
            string assetBaseName,
            CancellationToken cancellationToken = default)
        {
            ReceivedAnalysis = analysis;
            return Task.FromResult(new LocalValidationReport
            {
                Passed = true,
                RegistrationModel = RegistrationModel.Affine,
                RegistrationConfidence = 0.95,
                StructuralSimilarity = 0.92,
                FurnitureSsim = 0.92,
                SilhouetteSimilarity = 0.90,
                EdgeSimilarity = 0.88,
                FurnitureEdgeSimilarity = 0.88,
                EdgePrecision = 0.88,
                EdgeRecall = 0.85,
                NormalizedColorDistributionDifference = 0.05,
                ChangedAreaPercentage = 0.02,
                LargestChangedRegionPercentage = 0.01,
                ProtectedAreaChangedPercentage = 0.01
            });
        }
    }

[Trait("Category", TestCategories.Calibration)]
    private class FakeFileService : IFileSystemService
    {
        public Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult("A1B2C3D4E5F6");

        public bool VerifySourceIntegrity(string filePath, string expectedSha256) => true;

        public void NormalizeOrientation(string filePath) { }

        public (int Width, int Height)? GetImageDimensions(string filePath) => (200, 200);

        public IReadOnlyList<string> GetSupportedImageFiles(string directory) => new List<string>();

        public void EnsureProjectOutputDirectories(string projectOutputDirectory)
        {
            Directory.CreateDirectory(projectOutputDirectory);
            Directory.CreateDirectory(Path.Combine(projectOutputDirectory, "Accepted"));
            Directory.CreateDirectory(Path.Combine(projectOutputDirectory, "Review"));
            Directory.CreateDirectory(Path.Combine(projectOutputDirectory, "Failed"));
            Directory.CreateDirectory(Path.Combine(projectOutputDirectory, "Metadata"));
            Directory.CreateDirectory(Path.Combine(projectOutputDirectory, "Diagnostics"));
            Directory.CreateDirectory(Path.Combine(projectOutputDirectory, "Temp"));
        }

        public Task<string> GenerateThumbnailAsync(string imagePath, string destinationDirectory, int maxWidth = 240, int maxHeight = 240, CancellationToken cancellationToken = default) =>
            Task.FromResult(imagePath);

        public Task SaveSidecarMetadataAsync<T>(string metadataDirectory, string baseFileName, T sidecarData, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(metadataDirectory);
            var json = JsonSerializer.Serialize(sidecarData, new JsonSerializerOptions { WriteIndented = true });
            return File.WriteAllTextAsync(Path.Combine(metadataDirectory, $"{baseFileName}.json"), json, cancellationToken);
        }
    }

[Trait("Category", TestCategories.Calibration)]
    private class FakeAssetRepository : IImageAssetRepository
    {
        private ProcessingJob? _job;
        public IReadOnlyList<RestorationAttempt> ExistingAttempts { get; set; } = Array.Empty<RestorationAttempt>();
        public ProcessingJob? ExistingJob { get => _job; set => _job = value; }

        public Task<ImageAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ImageAsset?>(null);
        public Task<ImageAsset?> GetByHashAsync(Guid projectId, string sha256Hash, CancellationToken cancellationToken = default) => Task.FromResult<ImageAsset?>(null);
        public Task<IReadOnlyList<ImageAsset>> GetByProjectIdAsync(Guid projectId, ImageProcessingStatus? statusFilter = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ImageAsset>>(new List<ImageAsset>());
        public Task<int> ResetProjectProcessingStateAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<ImageAsset> AddAsync(ImageAsset asset, CancellationToken cancellationToken = default) => Task.FromResult(asset);
        public Task AddRangeAsync(IEnumerable<ImageAsset> assets, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(ImageAsset asset, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ProcessingJob> CreateJobAsync(Guid assetId, CancellationToken cancellationToken = default)
        {
            _job = new ProcessingJob { ImageAssetId = assetId };
            return Task.FromResult(_job);
        }

        public Task<ProcessingJob?> GetLatestJobForAssetAsync(Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_job);

        public Task UpdateJobAsync(ProcessingJob job, CancellationToken cancellationToken = default)
        {
            _job = job;
            return Task.CompletedTask;
        }

        public Task<RestorationAttempt> AddAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default) =>
            Task.FromResult(attempt);

        public Task UpdateAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RestorationAttempt>> GetAttemptsForJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ExistingAttempts);

        public Task RemoveIncompleteAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task UnusableLocalAnalysisStopsBeforeGptGenerationWithoutGeminiFallback()
    {
        // Setup temporary directories and image
        var tempDir = Path.Combine(Path.GetTempPath(), $"openimg_shadow_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var sourceImagePath = Path.Combine(tempDir, "test_cabinet.jpg");

        // Create a minimal 200x200 JPEG image
        using (var mat = new Mat(200, 200, MatType.CV_8UC3, new Scalar(255, 255, 255)))
        {
            mat.SaveImage(sourceImagePath);
        }

        try
        {
            var options = new OpenImgOptions
            {
                LocalAnalysisMode = LocalAnalysisMode.Preferred,
                RestorationProfile = "Conservative",
                MaxAutomaticAttempts = 1
            };

            var aiProvider = new FakeAiProvider();
            var validator = new FakeValidator();
            var fileService = new FakeFileService();
            var assetRepo = new FakeAssetRepository();
            var decisionEngine = new ValidationDecisionEngine(options);
            var corruptedLocalEngine = new CorruptedLocalAnalysisEngine();
            var orchestrator = new ImageProcessingOrchestrator(
                aiProvider,
                validator,
                fileService,
                assetRepo,
                decisionEngine,
                options,
                NullLogger<ImageProcessingOrchestrator>.Instance,
                retryPolicy: null,
                localAnalysisEngine: corruptedLocalEngine);

            var project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "TestProject",
                OutputDirectory = Path.Combine(tempDir, "output")
            };

            var asset = new ImageAsset
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                OriginalFileName = "test_cabinet.jpg",
                OriginalFilePath = sourceImagePath,
                Sha256Hash = "A1B2C3D4E5F6",
                Width = 200,
                Height = 200,
                Status = ImageProcessingStatus.Pending
            };

            // Current product behavior is fail-closed: local analysis is the
            // only source-side input and unusable evidence must stop GPT.
            var result = await orchestrator.ProcessAssetAsync(project, asset);

            Assert.False(result.Success);
            Assert.Equal(ValidationDecision.NeedsReview, result.Decision);
            Assert.Equal(0, aiProvider.GenerationCallCount);
            Assert.Null(validator.ReceivedAnalysis);

            var localAnalysisPath = Path.Combine(project.OutputDirectory, "Metadata", "test_cabinet__local_analysis.json");
            Assert.True(File.Exists(localAnalysisPath));
            var localJson = await File.ReadAllTextAsync(localAnalysisPath);
            Assert.Contains("COMPLETELY_WRONG_HASH", localJson);
            Assert.Contains("alien_spaceship", localJson);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task SingleViewOrchestratorUsesLocalContractAndConfiguredGptGenerationProvider()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openimg_gpt_product_flow_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var sourceImagePath = Path.Combine(tempDir, "single-view.jpg");
        using (var mat = new Mat(200, 200, MatType.CV_8UC3, new Scalar(180, 180, 180)))
            mat.SaveImage(sourceImagePath);

        try
        {
            var options = new OpenImgOptions { LocalAnalysisMode = LocalAnalysisMode.Preferred, MaxAutomaticAttempts = 1 };
            var gptProvider = new FakeAiProvider();
            var validator = new FakeValidator();
            var assetRepository = new FakeAssetRepository();
            var orchestrator = new ImageProcessingOrchestrator(
                gptProvider,
                validator,
                new FakeFileService(),
                assetRepository,
                new ValidationDecisionEngine(options),
                options,
                NullLogger<ImageProcessingOrchestrator>.Instance,
                localAnalysisEngine: new ValidLocalAnalysisEngine());
            var project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "SingleViewProductFlow",
                OutputDirectory = Path.Combine(tempDir, "output")
            };
            var asset = new ImageAsset
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                OriginalFileName = Path.GetFileName(sourceImagePath),
                OriginalFilePath = sourceImagePath,
                Sha256Hash = "A1B2C3D4E5F6",
                Width = 200,
                Height = 200,
                Status = ImageProcessingStatus.Pending
            };

            var result = await orchestrator.ProcessAssetAsync(project, asset);

            Assert.Equal(1, gptProvider.GenerationCallCount);
            Assert.NotNull(gptProvider.LastPrompt);
            Assert.Contains("source", gptProvider.LastPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(validator.ReceivedAnalysis);
            Assert.NotEqual(ValidationDecision.Rejected, result.Decision);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Theory]
    [InlineData(ProviderOperationState.GenerationSubmitted, false)]
    [InlineData(ProviderOperationState.NeedsRecovery, true)]
    public async Task SubmittedOperationAfterRestartRoutesToRecoveryWithoutGeneratingAgain(
        ProviderOperationState operationState,
        bool completedBeforeCrash)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openimg_recovery_guard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var sourceImagePath = Path.Combine(tempDir, "source.png");
        using (var mat = new Mat(64, 64, MatType.CV_8UC3, new Scalar(180, 180, 180)))
            mat.SaveImage(sourceImagePath);

        try
        {
            var options = new OpenImgOptions { LocalAnalysisMode = LocalAnalysisMode.Preferred, MaxAutomaticAttempts = 1 };
            var existingJob = new ProcessingJob { Id = Guid.NewGuid(), ImageAssetId = Guid.NewGuid(), AttemptCount = 1 };
            var existingAttempt = new RestorationAttempt
            {
                ProcessingJobId = existingJob.Id,
                AttemptNumber = 1,
                ProviderOperationId = "gpt-op-may-exist",
                ProviderOperationState = operationState,
                CompletedAt = completedBeforeCrash ? DateTimeOffset.UtcNow : null
            };
            var repository = new FakeAssetRepository
            {
                ExistingJob = existingJob,
                ExistingAttempts = [existingAttempt]
            };
            var provider = new FakeAiProvider();
            var orchestrator = new ImageProcessingOrchestrator(
                provider,
                new FakeValidator(),
                new FakeFileService(),
                repository,
                new ValidationDecisionEngine(options),
                options,
                NullLogger<ImageProcessingOrchestrator>.Instance);
            var project = new Project { Id = Guid.NewGuid(), OutputDirectory = Path.Combine(tempDir, "output") };
            var asset = new ImageAsset
            {
                Id = existingJob.ImageAssetId,
                ProjectId = project.Id,
                OriginalFileName = Path.GetFileName(sourceImagePath),
                OriginalFilePath = sourceImagePath,
                Sha256Hash = "A1B2C3D4E5F6",
                Status = ImageProcessingStatus.Restoring
            };

            var result = await orchestrator.ProcessAssetAsync(project, asset);

            Assert.Equal(ValidationDecision.NeedsReview, result.Decision);
            Assert.Equal(RestorationFailureReason.OperationRecoveryRequired, result.FailureReason);
            Assert.Equal(0, provider.GenerationCallCount);
            Assert.Equal(ProviderOperationState.NeedsRecovery, existingAttempt.ProviderOperationState);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
