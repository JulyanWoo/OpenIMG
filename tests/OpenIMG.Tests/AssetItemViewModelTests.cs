using System.IO;
using OpenIMG.App.ViewModels;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;

namespace OpenIMG.Tests;

public sealed class AssetItemViewModelTests
{
    [Fact]
    [Trait("Category", TestCategories.Unit)]
    public void RefreshState_UsesPersistedCandidateInsteadOfStaleAssetSnapshot()
    {
        var workspace = TestArtifactWorkspace.Create(nameof(RefreshState_UsesPersistedCandidateInsteadOfStaleAssetSnapshot));
        try
        {
            var candidatePath = Path.Combine(workspace, "restored.png");
            File.WriteAllBytes(candidatePath, [1, 2, 3]);

            var assetId = Guid.NewGuid();
            var staleAsset = new ImageAsset
            {
                Id = assetId,
                Status = ImageProcessingStatus.Pending
            };
            var viewModel = new AssetItemViewModel(staleAsset);

            var persistedAsset = new ImageAsset
            {
                Id = assetId,
                Status = ImageProcessingStatus.NeedsReview,
                ProcessingJobs =
                [
                    new ProcessingJob
                    {
                        AttemptCount = 1,
                        Attempts =
                        [
                            new RestorationAttempt
                            {
                                GeneratedImagePath = candidatePath,
                                CompletedAt = DateTimeOffset.UtcNow
                            }
                        ]
                    }
                ]
            };

            viewModel.RefreshState(persistedAsset);

            Assert.Equal(ImageProcessingStatus.NeedsReview, viewModel.Status);
            Assert.Equal(candidatePath, viewModel.RestoredImagePath);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", TestCategories.Unit)]
    public void RefreshState_FallsBackToNewestCandidateThatStillExists()
    {
        var workspace = TestArtifactWorkspace.Create(nameof(RefreshState_FallsBackToNewestCandidateThatStillExists));
        try
        {
            var existingCandidate = Path.Combine(workspace, "earlier-candidate.png");
            File.WriteAllBytes(existingCandidate, [1, 2, 3]);
            var missingCandidate = Path.Combine(workspace, "missing-candidate.png");
            var asset = new ImageAsset
            {
                ProcessingJobs =
                [
                    new ProcessingJob
                    {
                        CreatedAt = DateTimeOffset.UtcNow,
                        Attempts =
                        [
                            new RestorationAttempt
                            {
                                GeneratedImagePath = existingCandidate,
                                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
                            },
                            new RestorationAttempt
                            {
                                GeneratedImagePath = missingCandidate,
                                CompletedAt = DateTimeOffset.UtcNow
                            }
                        ]
                    }
                ]
            };

            var viewModel = new AssetItemViewModel(asset);

            Assert.Equal(existingCandidate, viewModel.RestoredImagePath);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
