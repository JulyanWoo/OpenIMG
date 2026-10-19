namespace OpenIMG.Core.Entities;

/// <summary>Durable local accounting for a provider image-generation attempt.</summary>
public sealed class ProviderUsageRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public bool ConsumesBudget { get; set; } = true;
}
