namespace OpenIMG.Application.Configuration;

public sealed class DualModelRestorationOptions
{
    public bool Enabled { get; set; }
    public int ProcessingConcurrency { get; set; } = 1;
    public ProviderUsageBudgetOptions GeminiBudget { get; set; } = new();
    public ProviderUsageBudgetOptions GptBudget { get; set; } = new()
    {
        Enabled = false,
        MaxOperationsPerWindow = 40,
        WindowDurationMinutes = 180,
        SafetyReserve = 5,
        OptionalMaxOperationsPerDay = 120
    };
}
