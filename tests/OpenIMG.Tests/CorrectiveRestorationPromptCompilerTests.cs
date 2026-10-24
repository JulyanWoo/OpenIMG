using OpenIMG.Application.Configuration;
using OpenIMG.Application.Policies;
using OpenIMG.Application.Restoration;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class CorrectiveRestorationPromptCompilerTests
{
    [Fact]
    public void PromptSeparatesPhysicalTruthFromPhotographicAndCleanupAuthority()
    {
        var contract = new CorrectiveRestorationContract
        {
            Route = CorrectiveRestorationRoute.DeltaCorrection,
            PreserveFromDraft = [
                CorrectiveInstruction.Policy("PhotographicImprovement"),
                CorrectiveInstruction.Policy("GeminiCleanup")]
        };

        var prompt = new CorrectiveRestorationPromptCompiler().Compile(contract);

        Assert.Contains("PHYSICAL STRUCTURE: the original is authoritative", prompt);
        Assert.Contains("PHOTOGRAPHIC IMPROVEMENT AND APPROVED CLEANUP: preserve valid improvements", prompt);
        Assert.Contains("Preserve the draft's photographic enhancement", prompt);
        Assert.Contains("Do not restore loose clutter merely because it appears in the original", prompt);
        Assert.DoesNotContain("IMAGE 1 is the absolute authority for everything", prompt);
        Assert.DoesNotContain("IMAGE 2 is the global authority", prompt);
    }

    [Fact]
    public void PromptPreservesGeminiCleanupWithoutNeedingPerObjectCleanupLabels()
    {
        var contract = new CorrectiveRestorationContract
        {
            PreserveFromDraft = [CorrectiveInstruction.Policy("GeminiCleanup")]
        };

        var prompt = new CorrectiveRestorationPromptCompiler().Compile(contract);

        Assert.Contains("Do not restore loose clutter merely because it appears in the original", prompt);
        Assert.Contains("removal of temporary loose clutter", prompt);
        Assert.DoesNotContain("ambiguous object", prompt);
    }

    [Fact]
    public void PromptRequiresDeltaCorrectionsAndLeavesDraftUnchangedWhenNoPermanentErrorIsProven()
    {
        var prompt = new CorrectiveRestorationPromptCompiler().Compile(new CorrectiveRestorationContract
        {
            Route = CorrectiveRestorationRoute.DeltaCorrection,
            PreserveFromDraft = [CorrectiveInstruction.Policy("PhotographicImprovement")]
        });

        Assert.Contains("If the contract has no restore/remove instructions, return the Gemini draft essentially unchanged", prompt);
        Assert.Contains("RESTORE FROM ORIGINAL:\r\n- None identified by local evidence", prompt);
        Assert.Contains("REMOVE UNSUPPORTED FROM DRAFT:\r\n- None identified by local evidence", prompt);
        Assert.DoesNotContain("Observed material families", prompt);
        Assert.DoesNotContain("Observed color identities", prompt);
        Assert.DoesNotContain("27 components", prompt);
    }

    [Fact]
    public void BudgetUsesRollingWindowAndSafetyReserve()
    {
        var policy = new ProviderUsageBudget();
        var now = DateTimeOffset.UtcNow;
        var options = new ProviderUsageBudgetOptions
        {
            Enabled = true,
            MaxOperationsPerWindow = 3,
            SafetyReserve = 1,
            WindowDurationMinutes = 60
        };
        var history = new[]
        {
            new ProviderUsageEntry("GPT", now.AddMinutes(-20)),
            new ProviderUsageEntry("GPT", now.AddMinutes(-10)),
            new ProviderUsageEntry("Gemini", now.AddMinutes(-5))
        };

        var decision = policy.Evaluate("GPT", options, history, now);

        Assert.False(decision.Allowed);
        Assert.Equal("ProviderBudgetExceeded", decision.Reason);
        Assert.Equal(2, decision.OperationsInWindow);
        Assert.Equal(2, decision.EffectiveBudget);
        Assert.Equal(now.AddMinutes(40), decision.NextEligibleAt);
    }

    [Fact]
    public void BudgetAlsoEnforcesIndependentRollingDailyCeiling()
    {
        var now = DateTimeOffset.UtcNow;
        var options = new ProviderUsageBudgetOptions
        {
            Enabled = true,
            MaxOperationsPerWindow = 40,
            WindowDurationMinutes = 180,
            OptionalMaxOperationsPerDay = 4
        };
        var history = Enumerable.Range(1, 4)
            .Select(minutes => new ProviderUsageEntry("GPT", now.AddHours(-5).AddMinutes(minutes)))
            .ToArray();

        var decision = new ProviderUsageBudget().Evaluate("GPT", options, history, now);

        Assert.False(decision.Allowed);
        Assert.Equal("ProviderDailyBudgetExceeded", decision.Reason);
        Assert.Equal(4, decision.OperationsInDay);
        Assert.Equal(4, decision.EffectiveDailyBudget);
        Assert.NotNull(decision.NextEligibleAt);
    }

    [Fact]
    public void DisabledBudgetDoesNotBlockProvider()
    {
        var now = DateTimeOffset.UtcNow;
        var existingHistory = Enumerable.Range(0, 500)
            .Select(index => new ProviderUsageEntry("GPT", now.AddMinutes(-index)))
            .ToArray();
        var decision = new ProviderUsageBudget().Evaluate(
            "GPT",
            new ProviderUsageBudgetOptions
            {
                Enabled = false,
                MaxOperationsPerWindow = 40,
                WindowDurationMinutes = 180,
                SafetyReserve = 5,
                OptionalMaxOperationsPerDay = 120
            },
            existingHistory,
            now);

        Assert.True(decision.Allowed);
        Assert.Equal("BudgetDisabled", decision.Reason);
        Assert.Equal(0, decision.EffectiveBudget);
        Assert.Null(decision.EffectiveDailyBudget);
    }

    [Fact]
    public void DualModelPolicyIsDisabledByDefaultAndNeverInvokesBarrier4()
    {
        var policy = new DualModelRestorationPolicy(new OpenImgOptions());

        Assert.False(policy.Enabled);
        Assert.False(policy.ShouldInvokeBarrier4);
        Assert.False(policy.AllowsAutomaticRetry);
    }
}
