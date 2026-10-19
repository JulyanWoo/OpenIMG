namespace OpenIMG.Application.Abstractions.AI;

/// <summary>Signals that GPT image generation is blocked by a local or provider-reported usage limit.</summary>
public sealed class ProviderUsageLimitException : Exception
{
    public DateTimeOffset? NextEligibleAt { get; }

    public ProviderUsageLimitException(string message, DateTimeOffset? nextEligibleAt = null, Exception? innerException = null)
        : base(message, innerException)
    {
        NextEligibleAt = nextEligibleAt;
    }
}
