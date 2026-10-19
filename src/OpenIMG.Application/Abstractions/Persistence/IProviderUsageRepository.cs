using OpenIMG.Application.Configuration;
using OpenIMG.Application.Policies;

namespace OpenIMG.Application.Abstractions.Persistence;

/// <summary>Atomically reserves local provider usage before an image is submitted for generation.</summary>
public interface IProviderUsageRepository
{
    Task<ProviderUsageBudgetDecision> TryReserveAsync(
        string provider,
        string operationId,
        ProviderUsageBudgetOptions options,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a reservation only when the provider confirms no send click occurred.</summary>
    Task ReleaseUnsubmittedReservationAsync(
        string provider,
        string operationId,
        CancellationToken cancellationToken = default);
}
