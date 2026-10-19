using OpenIMG.Application.Analysis;
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Prompts;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class FurnitureAnalysisContractTests
{
    private readonly FurnitureAnalysisContractAdapter _adapter = new();

    [Fact]
    public void LocalProjectionPreservesConfidenceAuthorityAndNegativeEvidence()
    {
        var local = new LocalFurnitureContract
        {
            SourceFileName = "source.jpg",
            SourceSha256 = "abc123",
            LocalConfidence = 0.82,
            FurnitureConfidence = 0.91,
            FurnitureEnvelopeAuthority = ConstraintAuthority.Strict,
            StructuralRegions = new List<LocalStructuralRegion>
            {
                new() { Id = "panel-1", Confidence = 0.88, Region = new NormalizedRectangle(0.1, 0.1, 0.5, 0.4) }
            },
            HardwareCandidates = new List<HardwareCandidate>
            {
                new() { Id = "hardware-1", Confidence = 0.93, Region = new NormalizedRectangle(0.55, 0.2, 0.04, 0.2) }
            },
            ProtectedAbsenceRegions = new List<ProtectedAbsenceRegion>
            {
                new() { ConstraintId = "absence-1", Strict = true, Confidence = 0.95 }
            },
            PersistentSceneDetails = new List<SceneDetailCandidate>
            {
                new() { CandidateId = "switch-1", Strict = true, Confidence = 0.90 }
            },
            AuthorityRecords = new List<ContractAuthorityRecord>
            {
                new() { ConstraintId = "panel-1", Type = "structural", Confidence = 0.88, Authority = ConstraintAuthority.Strict }
            }
        };

        var contract = _adapter.FromLocal(local);

        Assert.Equal("source.jpg", contract.SourceIdentity.FileName);
        Assert.Equal("abc123", contract.SourceIdentity.Sha256);
        Assert.Equal(AnalysisSource.Local, contract.AnalysisSource);
        Assert.Equal(0.82, contract.ConfidenceMetadata.Overall, 3);
        Assert.Equal(ConstraintAuthority.Strict, contract.FurnitureEnvelopeAuthority);
        Assert.Single(contract.StructuralRegions);
        Assert.Single(contract.Hardware);
        Assert.Single(contract.ProtectedAbsenceRegions);
        Assert.Single(contract.PersistentSceneDetails);
        Assert.Single(contract.AuthorityRecords);
    }

    [Fact]
    public void LocalProjectionCarriesOnlyStrictlyAuthorizedCleanupTargets()
    {
        var region = new NormalizedRectangle(0.72, 0.72, 0.12, 0.18);
        var local = new LocalFurnitureContract
        {
            SceneObjects =
            [
                new LocalDetectedObject
                {
                    Id = "temporary-bag",
                    ClassName = "bag",
                    Confidence = 0.93,
                    Region = region,
                    IsRemovableClutter = true
                },
                new LocalDetectedObject
                {
                    Id = "uncertain-object",
                    ClassName = "object",
                    Confidence = 0.55,
                    Region = new NormalizedRectangle(0.1, 0.7, 0.1, 0.1),
                    IsRemovableClutter = true
                }
            ],
            AuthorityRecords =
            [
                new ContractAuthorityRecord { ConstraintId = "temporary-bag", Authority = ConstraintAuthority.StrictCleanupAllowed },
                new ContractAuthorityRecord { ConstraintId = "uncertain-object", Authority = ConstraintAuthority.AdvisoryCleanupAllowed }
            ]
        };

        var contract = _adapter.FromLocal(local);

        var target = Assert.Single(contract.CleanupTargets);
        Assert.Equal("temporary-bag", target.CleanupTargetId);
        Assert.Equal(region, target.Region);
        Assert.Equal(ConstraintAuthority.StrictCleanupAllowed, target.Authority);
        Assert.True(target.ExpectedRemoval);
    }

    [Fact]
    public void ProviderProjectionKeepsMeasuredColorSeparateFromSemanticLabels()
    {
        var analysis = new FurnitureAnalysis
        {
            FurnitureType = "cabinet",
            Confidence = 0.77,
            MainColors = new List<string> { "warm walnut" },
            Components = new List<FurnitureComponent>
            {
                new() { Id = "drawer-1", Color = "warm walnut", Material = "wood", Confidence = 0.80 }
            }
        };

        var contract = _adapter.FromProvider(analysis, "source.jpg", "sha256");

        Assert.Equal("warm walnut", contract.MainColors.Single());
        Assert.Empty(contract.ColorRegions);
        Assert.Equal(0.77, contract.ConfidenceMetadata.Overall, 3);
        Assert.Equal(ConstraintAuthority.Strict, contract.SourceIdentity.Authority);
    }

    [Fact]
    public void CanonicalPromptProjectionPreservesExistingPromptBehavior()
    {
        var analysis = new FurnitureAnalysis
        {
            FurnitureType = "cabinet",
            GeometryDescription = "single visible cabinet assembly",
            Confidence = 0.90,
            MainMaterials = new List<string> { "wood" },
            MainColors = new List<string> { "walnut" },
            Components = new List<FurnitureComponent>
            {
                new()
                {
                    Id = "panel-1",
                    Type = "structural_panel",
                    Description = "visible side panel",
                    Confidence = 0.90,
                    Region = new ProtectedRegion { X = 0.1, Y = 0.1, Width = 0.5, Height = 0.5 }
                }
            },
            ProtectedRegions = new List<ProtectedRegion>
            {
                new() { X = 0.1, Y = 0.1, Width = 0.5, Height = 0.5, RegionType = "furniture_silhouette" }
            }
        };
        var options = new OpenImgOptions { RestorationProfile = "HighQualityConservative" };
        var contract = _adapter.FromProvider(analysis, "source.jpg", "sha256");

        var legacyPrompt = PromptCompiler.BuildRestorationPrompt(
            analysis, 1, null, options: options, analysisSource: AnalysisSource.GeminiFallback);
        var canonicalPrompt = PromptCompiler.BuildRestorationPrompt(
            contract, _adapter, 1, null, options: options, analysisSource: AnalysisSource.GeminiFallback);

        Assert.Equal(legacyPrompt, canonicalPrompt);
    }

    [Fact]
    public void HumanReviewIsIndependentFromAutomatedDecision()
    {
        var result = new ProcessingResult
        {
            Decision = ValidationDecision.Accepted,
            HumanReview = new HumanReviewAssessment()
        };

        Assert.Equal(ValidationDecision.Accepted, result.AutomatedDecision);
        Assert.Equal(HumanReviewStatus.Pending, result.HumanReview.Status);
        Assert.NotEqual(HumanReviewStatus.Approved, result.HumanReview.Status);
    }
}
