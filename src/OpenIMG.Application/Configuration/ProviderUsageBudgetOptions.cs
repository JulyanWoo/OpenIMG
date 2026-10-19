namespace OpenIMG.Application.Configuration;

/// <summary>
/// Configuration only. Values are not provider truth; they are an optional
/// local safety ceiling supplied by the operator.
/// </summary>
public sealed class ProviderUsageBudgetOptions
{
    public bool Enabled { get; set; }
    public int MaxOperationsPerWindow { get; set; }
    public int WindowDurationMinutes { get; set; }
    public int SafetyReserve { get; set; }
    public int? OptionalMaxOperationsPerDay { get; set; }

    public TimeSpan WindowDuration => TimeSpan.FromMinutes(Math.Max(0, WindowDurationMinutes));
    public int EffectiveAutomaticBudget => Math.Max(0, MaxOperationsPerWindow - Math.Max(0, SafetyReserve));
}
