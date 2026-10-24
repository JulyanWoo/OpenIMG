using OpenIMG.Application.Configuration;

namespace OpenIMG.Application.Policies;

public sealed record ProviderUsageEntry(
    string Provider,
    DateTimeOffset StartedAt,
    bool ConsumesBudget = true);

public sealed record ProviderUsageBudgetDecision(
    bool Allowed,
    string Reason,
    int OperationsInWindow,
    int EffectiveBudget,
    DateTimeOffset? NextEligibleAt)
{
    public int OperationsInDay { get; init; }
    public int? EffectiveDailyBudget { get; init; }
}

/// <summary>
/// Deterministic rolling-window budget policy. Persistence is supplied by the
/// caller through the usage-history collection; no in-memory counter is reset
/// on a timer and no provider limit is hard-coded here.
/// </summary>
public sealed class ProviderUsageBudget
{
    public ProviderUsageBudgetDecision Evaluate(
        string provider,
        ProviderUsageBudgetOptions options,
        IEnumerable<ProviderUsageEntry> history,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(history);

        if (!options.Enabled)
        {
            return new(true, "BudgetDisabled", 0, 0, null)
            {
                EffectiveDailyBudget = null
            };
        }

        var window = options.WindowDuration;
        var effectiveDailyBudget = options.OptionalMaxOperationsPerDay is int configuredDailyBudget
            ? Math.Max(0, configuredDailyBudget - Math.Max(0, options.SafetyReserve))
            : (int?)null;
        if (window <= TimeSpan.Zero || options.EffectiveAutomaticBudget <= 0 ||
            (options.OptionalMaxOperationsPerDay.HasValue && effectiveDailyBudget <= 0))
        {
            return new(false, "BudgetConfigurationInvalid", 0, options.EffectiveAutomaticBudget, null)
            {
                EffectiveDailyBudget = effectiveDailyBudget
            };
        }

        var cutoff = now - window;
        var relevant = history
            .Where(entry => entry.ConsumesBudget)
            .Where(entry => string.Equals(entry.Provider, provider, StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.StartedAt >= cutoff && entry.StartedAt <= now)
            .OrderBy(entry => entry.StartedAt)
            .ToArray();

        var daily = history
            .Where(entry => entry.ConsumesBudget)
            .Where(entry => string.Equals(entry.Provider, provider, StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.StartedAt >= now.AddDays(-1) && entry.StartedAt <= now)
            .OrderBy(entry => entry.StartedAt)
            .ToArray();

        var windowExceeded = relevant.Length >= options.EffectiveAutomaticBudget;
        var dayExceeded = effectiveDailyBudget.HasValue && daily.Length >= effectiveDailyBudget.Value;
        if (!windowExceeded && !dayExceeded)
        {
            return new(true, "BudgetAvailable", relevant.Length, options.EffectiveAutomaticBudget, null)
            {
                OperationsInDay = daily.Length,
                EffectiveDailyBudget = effectiveDailyBudget
            };
        }

        var releaseTimes = new List<DateTimeOffset>(2);
        if (windowExceeded)
            releaseTimes.Add(relevant[0].StartedAt + window);
        if (dayExceeded)
            releaseTimes.Add(daily[0].StartedAt.AddDays(1));

        var reason = dayExceeded ? "ProviderDailyBudgetExceeded" : "ProviderBudgetExceeded";
        return new(false, reason, relevant.Length, options.EffectiveAutomaticBudget, releaseTimes.Max())
        {
            OperationsInDay = daily.Length,
            EffectiveDailyBudget = effectiveDailyBudget
        };
    }
}
