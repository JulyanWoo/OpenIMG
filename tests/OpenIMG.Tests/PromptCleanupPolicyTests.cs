using OpenIMG.Application.Configuration;
using OpenIMG.Application.Prompts;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using Xunit;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class PromptCleanupPolicyTests
{
    [Theory]
    [InlineData(PhotographicEnhancementProfile.Light)]
    [InlineData(PhotographicEnhancementProfile.Moderate)]
    [InlineData(PhotographicEnhancementProfile.Strong)]
    public void SafeCleanup_IsRequiredRegardlessOfPhotoProfileOrDetectorFindings(
        PhotographicEnhancementProfile photoProfile)
    {
        var prompt = PromptCompiler.BuildRestorationPrompt(
            new FurnitureAnalysis(),
            options: new OpenImgOptions
            {
                RestorationProfile = "Conservative",
                AllowSceneCleanup = false,
                PhotographicEnhancementProfile = photoProfile,
                SceneCleanupMode = SceneCleanupMode.SafeTemporaryOnly,
                PersonRemovalMode = PersonRemovalMode.Disabled
            });

        Assert.Contains("SCENE CLEANUP - REQUIRED (SafeTemporaryOnly)", prompt);
        Assert.Contains("remove clearly temporary, loose, non-structural clutter, even if local analysis detects none", prompt);
        Assert.Contains("detections are evidence, not a gate", prompt);
        Assert.Contains("Never guess hidden structure", prompt);
        Assert.DoesNotContain("You may remove it only", prompt);
        Assert.Contains("Person removal is disabled.", prompt);
    }

    [Fact]
    public void DisabledCleanup_DoesNotRequestLooseObjectRemoval()
    {
        var prompt = PromptCompiler.BuildRestorationPrompt(
            new FurnitureAnalysis(),
            options: new OpenImgOptions
            {
                PhotographicEnhancementProfile = PhotographicEnhancementProfile.Strong,
                SceneCleanupMode = SceneCleanupMode.Disabled,
                PersonRemovalMode = PersonRemovalMode.Disabled
            });

        Assert.Contains("SCENE CLEANUP: Disabled", prompt);
        Assert.DoesNotContain("SCENE CLEANUP - REQUIRED", prompt);
        Assert.Contains("Person removal is disabled.", prompt);
    }

    [Fact]
    public void PersonRemoval_IsIndependentAndConstrainedWhenEnabled()
    {
        var prompt = PromptCompiler.BuildRestorationPrompt(
            new FurnitureAnalysis(),
            options: new OpenImgOptions
            {
                SceneCleanupMode = SceneCleanupMode.Disabled,
                PersonRemovalMode = PersonRemovalMode.SafeWhenClearlyTemporary
            });

        Assert.Contains("SCENE CLEANUP: Disabled", prompt);
        Assert.Contains("PERSON REMOVAL POLICY (SEPARATE FROM GENERAL CLUTTER CLEANUP)", prompt);
        Assert.Contains("Remove a person only when clearly incidental to the furniture photograph", prompt);
        Assert.Contains("If the person touches or occludes furniture", prompt);
    }

    [Theory]
    [InlineData(PhotographicEnhancementProfile.Light)]
    [InlineData(PhotographicEnhancementProfile.Moderate)]
    [InlineData(PhotographicEnhancementProfile.Strong)]
    public void CleanupIntent_IsNotGatedByDetectorOrPhotoProfile(PhotographicEnhancementProfile profile)
    {
        var intent = new LocalImageQualityAssessment().MapToRestorationIntent(
            profile,
            SceneCleanupMode.SafeTemporaryOnly,
            safeTemporaryClutterPresent: false);

        Assert.Equal(SceneCleanupMode.SafeTemporaryOnly, intent.SceneCleanup);
    }
}
