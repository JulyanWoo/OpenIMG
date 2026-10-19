using OpenIMG.Application.Analysis;
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Prompts;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using Xunit;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public class PromptCompilerTests
{
    [Fact]
    public void BuildAnalysisPrompt_ContainsStrictRulesAndVersion()
    {
        var prompt = PromptCompiler.BuildAnalysisPrompt("v1.0");

        Assert.Contains("Prompt Version: v1.0", prompt);
        Assert.Contains("ANALYSIS ONLY", prompt);
        Assert.Contains("DO NOT edit the image", prompt);
        Assert.Contains("DO NOT generate an image", prompt);
        Assert.Contains("DO NOT redesign", prompt);
        Assert.Contains("STRICT JSON ONLY", prompt);
        Assert.Contains("furnitureType", prompt);
        Assert.Contains("components", prompt);
        Assert.Contains("protectedRegions", prompt);
    }

    [Fact]
    public void BuildRestorationPrompt_InjectsAnalysisAndProtections()
    {
        var analysis = new FurnitureAnalysis
        {
            FurnitureType = "kitchen island",
            GeometryDescription = "Rectangular base with quartz countertop and 4 drawers",
            MainColors = new List<string> { "matte white" },
            AccentColors = new List<string> { "brushed gold" },
            ColorLocations = new Dictionary<string, string>
            {
                { "brushed gold", "drawer bar handles only" },
                { "matte white", "all outer panels and drawer fronts" }
            },
            Components = new List<FurnitureComponent>
            {
                new()
                {
                    Id = "drawer_top_left",
                    Type = "drawer",
                    Description = "Top left utensil drawer",
                    Color = "matte white",
                    Material = "lacquered MDF",
                    IsStructural = true
                }
            },
            ProtectedFeatures = new List<string> { "4 drawer configuration", "waterfall quartz edge" }
        };

        var prompt = PromptCompiler.BuildRestorationPrompt(analysis, attemptNumber: 1);

        Assert.Contains("PHOTOGRAPHIC RESTORATION ONLY - NOT FURNITURE REDESIGN", prompt);
        Assert.Contains("This is strict photographic restoration.", prompt);
        Assert.Contains("Preserve permanent furniture, architecture, and fixtures exactly.", prompt);
        Assert.Contains("kitchen island", prompt);
        Assert.Contains("matte white", prompt);
        Assert.Contains("brushed gold", prompt);
        Assert.Contains("drawer bar handles only", prompt);
        Assert.Contains("drawer_top_left", prompt);
        Assert.Contains("DO NOT alter furniture geometry", prompt);
        Assert.Contains("DO NOT add, remove, or modify cabinet doors", prompt);
        Assert.Contains("ALLOWED OPERATIONS (PHOTOGRAPHIC QUALITY IMPROVEMENT ONLY)", prompt);
        Assert.DoesNotContain("ELIMINATE CLUTTER & TRASH", prompt);
        Assert.DoesNotContain("REMOVE PEOPLE & UNWANTED REFLECTIONS", prompt);
        Assert.DoesNotContain("CORRECTIVE INSTRUCTION FROM PREVIOUS ATTEMPT FAILURE", prompt);
    }

    [Fact]
    public void BuildRestorationPrompt_PermissiveProfile_EnablesCleanupAndZoomOut()
    {
        var analysis = new FurnitureAnalysis
        {
            FurnitureType = "kitchen island",
            GeometryDescription = "Rectangular base with quartz countertop",
            MainColors = new List<string> { "matte white" },
            FramingAnalysis = new ImageFramingAnalysis
            {
                IsFurnitureCropped = true,
                SafeZoomOutRecommended = true,
                CropEdges = new List<string> { "top" },
                RecommendedExpansion = new RecommendedExpansion { TopPercent = 10 }
            }
        };

        var options = new OpenImgOptions
        {
            RestorationProfile = "Permissive",
            PhotographicEnhancementProfile = PhotographicEnhancementProfile.Moderate,
            SceneCleanupMode = SceneCleanupMode.SafeTemporaryOnly,
            PersonRemovalMode = PersonRemovalMode.SafeWhenClearlyTemporary,
            AllowSceneCleanup = true,
            AllowPersonRemoval = true,
            AllowReflectionCleanup = true,
            AllowZoomOut = true
        };

        var prompt = PromptCompiler.BuildRestorationPrompt(analysis, attemptNumber: 1, options: options);

        Assert.Contains("SCENE CLEANUP - REQUIRED (SafeTemporaryOnly)", prompt);
        Assert.Contains("PERSON REMOVAL POLICY (SEPARATE FROM GENERAL CLUTTER CLEANUP)", prompt);
        Assert.Contains("Remove a person only when clearly incidental", prompt);
        Assert.Contains("REFLECTION CLEANUP (SEPARATE POLICY)", prompt);
        Assert.Contains("SAFE ZOOM-OUT / REFRAMING DIRECTIVE", prompt);
        Assert.Contains("The furniture is visibly cropped at edges: top.", prompt);
    }

    [Fact]
    public void BuildRestorationPrompt_Phase3v2_ConservativeProfile_StrictPreservationLock()
    {
        var analysis = new FurnitureAnalysis
        {
            FurnitureType = "kitchen island",
            GeometryDescription = "Rectangular base with quartz countertop and 4 drawers",
            MainColors = new List<string> { "matte white" },
            AccentColors = new List<string> { "brushed gold" },
            Components = new List<FurnitureComponent>
            {
                new()
                {
                    Id = "drawer_top_left",
                    Type = "drawer",
                    Description = "Top left utensil drawer",
                    Color = "matte white",
                    Material = "lacquered MDF",
                    IsStructural = true
                }
            },
            ProtectedFeatures = new List<string> { "4 drawer configuration" },
            FramingAnalysis = new ImageFramingAnalysis
            {
                IsFurnitureCropped = true,
                SafeZoomOutRecommended = true,
                CropEdges = new List<string> { "top", "right" }
            }
        };

        var options = new OpenImgOptions
        {
            RestorationProfile = "Conservative",
            PhotographicEnhancementProfile = PhotographicEnhancementProfile.Light,
            SceneCleanupMode = SceneCleanupMode.Disabled,
            PersonRemovalMode = PersonRemovalMode.Disabled,
            AllowSceneCleanup = false,
            AllowPersonRemoval = false,
            AllowReflectionCleanup = false,
            AllowZoomOut = false,
            AllowOutpainting = false
        };

        var prompt = PromptCompiler.BuildRestorationPrompt(analysis, attemptNumber: 1, options: options);

        // Required explicit Phase 3-v2 baseline text
        Assert.Contains("This is strict photographic restoration.", prompt);
        Assert.Contains("Preserve permanent furniture, architecture, and fixtures exactly.", prompt);
        Assert.Contains("Do not add, replace, reconstruct, relocate, or redesign any permanent physical object", prompt);
        Assert.Contains("Do not zoom out.", prompt);
        Assert.Contains("Do not outpaint.", prompt);
        Assert.Contains("Do not extend the image beyond the original frame.", prompt);
        Assert.Contains("Do not change crop, framing, camera perspective, or composition.", prompt);
        Assert.Contains("Only improve photographic quality.", prompt);

        // Preservation instructions for:
        // - scene
        Assert.Contains("Preserve permanent furniture, architecture, and fixtures exactly.", prompt);
        // - objects
        Assert.Contains("SCENE CLEANUP: Disabled", prompt);
        // - people
        Assert.Contains("person", prompt);
        Assert.Contains("preserving the complete original scene, people", prompt);
        // - reflections
        Assert.Contains("reflection", prompt);
        Assert.Contains("reflections", prompt);
        // - framing
        Assert.Contains("Do not change crop, framing, camera perspective, or composition.", prompt);
        // - perspective
        Assert.Contains("camera perspective", prompt);
        Assert.Contains("proportions, perspective", prompt);

        // MUST NOT contain instructions requesting:
        // - cleanup
        Assert.DoesNotContain("CONTROLLED SCENE CLEANUP", prompt);
        Assert.DoesNotContain("ELIMINATE CLUTTER", prompt);
        Assert.DoesNotContain("removing clutter and dirt", prompt);
        // - object removal
        Assert.DoesNotContain("Remove only clearly non-furniture elements", prompt);
        // - person removal
        Assert.DoesNotContain("Remove any people", prompt);
        Assert.DoesNotContain("Remove people", prompt);
        // - reflection removal
        Assert.DoesNotContain("Only remove incidental reflections", prompt);
        Assert.DoesNotContain("Eliminate distracting reflections", prompt);
        // - zoom-out
        Assert.DoesNotContain("SAFE ZOOM-OUT / REFRAMING DIRECTIVE", prompt);
        Assert.DoesNotContain("You may apply a conservative slight zoom-out", prompt);
        // - outpainting (requesting outpaint)
        Assert.DoesNotContain("safe conservative zoom-out specified above", prompt);
    }

    [Fact]
    public void BuildRestorationPrompt_Attempt2_AppendsFailureFeedback()
    {
        var analysis = new FurnitureAnalysis
        {
            FurnitureType = "crib",
            MainColors = new List<string> { "white" }
        };

        var feedback = "Previous attempt incorrectly colored the side slats blue. They must remain white.";
        var prompt = PromptCompiler.BuildRestorationPrompt(analysis, attemptNumber: 2, failureFeedback: feedback);

        Assert.Contains("Attempt: 2", prompt);
        Assert.Contains("CORRECTIVE INSTRUCTION FROM PREVIOUS ATTEMPT FAILURE", prompt);
        Assert.Contains(feedback, prompt);
    }

    [Fact]
    public void BuildRestorationPrompt_CompactsSyntheticLocalAnalysisWithinBudget()
    {
        var contract = new LocalFurnitureContract
        {
            SourceFileName = "synthetic-cabinet.jpg",
            SourceSha256 = "synthetic-fixture",
            FurnitureBounds = new NormalizedRectangle(0.05, 0.05, 0.9, 0.9),
            FurnitureConfidence = 0.95,
            StructuralRegions = Enumerable.Range(0, 24)
                .Select(index => new LocalStructuralRegion
                {
                    Id = $"synthetic-shelf-{index + 1}",
                    Region = new NormalizedRectangle(0.08, 0.08 + index * 0.03, 0.84, 0.015),
                    RegionType = StructuralRegionType.HorizontalDivision,
                    Confidence = 0.92,
                    Orientation = "Horizontal"
                })
                .ToList()
        };
        var adapter = new LocalToFurnitureAnalysisAdapter();
        var adapted = adapter.Adapt(contract);
        var report = new LocalPromptCompactor().Compact(contract, adapted);
        var prompt = PromptCompiler.BuildRestorationPrompt(
            adapted,
            attemptNumber: 1,
            failureFeedback: null,
            version: "v1.1",
            options: new OpenImgOptions { RestorationProfile = "HighQualityConservative" },
            analysisSource: AnalysisSource.Local,
            assemblies: report.Assemblies,
            individualCriticalRegionIds: report.IndividualCriticalRegionIds);

        Assert.InRange(prompt.Length, 1, 14000);
        Assert.Contains("synthetic-shelf", string.Join(" ", report.Assemblies.SelectMany(assembly => assembly.MemberRegionIds)));
    }
}
