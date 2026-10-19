using Microsoft.EntityFrameworkCore;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Policies;
using OpenIMG.Core.Entities;

namespace OpenIMG.Infrastructure.Persistence;

/// <summary>Stores operation reservations in the existing OpenIMG SQLite database.</summary>
public sealed class ProviderUsageRepository : IProviderUsageRepository
{
    private static readonly SemaphoreSlim ReservationGate = new(1, 1);
    private readonly IDbContextFactory<OpenImgDbContext> _contextFactory;
    private readonly ProviderUsageBudget _budget;

    public ProviderUsageRepository(IDbContextFactory<OpenImgDbContext> contextFactory, ProviderUsageBudget budget)
    {
        _contextFactory = contextFactory;
        _budget = budget;
    }

    public async Task<ProviderUsageBudgetDecision> TryReserveAsync(
        string provider,
        string operationId,
        ProviderUsageBudgetOptions options,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(options);

        var providerKey = provider.Trim().ToUpperInvariant();
        var normalizedOperationId = operationId.Trim();
        await ReservationGate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var alreadyReserved = await context.ProviderUsageRecords.AnyAsync(
                record => record.Provider == providerKey && record.OperationId == normalizedOperationId,
                cancellationToken);
            if (alreadyReserved)
            {
                return new ProviderUsageBudgetDecision(
                    false, "ProviderOperationAlreadyReserved", 0, options.EffectiveAutomaticBudget, null);
            }

            var history = await context.ProviderUsageRecords
                .AsNoTracking()
                .Where(record => record.Provider == providerKey)
                .Select(record => new ProviderUsageEntry(record.Provider, record.StartedAt, record.ConsumesBudget))
                .ToListAsync(cancellationToken);
            var decision = _budget.Evaluate(providerKey, options, history, startedAt);
            if (!decision.Allowed)
                return decision;

            context.ProviderUsageRecords.Add(new ProviderUsageRecord
            {
                Provider = providerKey,
                OperationId = normalizedOperationId,
                StartedAt = startedAt.ToUniversalTime(),
                ConsumesBudget = true
            });
            await context.SaveChangesAsync(cancellationToken);
            return decision;
        }
        finally
        {
            ReservationGate.Release();
        }
    }

    public async Task ReleaseUnsubmittedReservationAsync(
        string provider,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        await ReservationGate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var normalizedProvider = provider.Trim().ToUpperInvariant();
            var normalizedOperationId = operationId.Trim();
            var reservation = await context.ProviderUsageRecords.SingleOrDefaultAsync(
                record => record.Provider == normalizedProvider && record.OperationId == normalizedOperationId,
                cancellationToken);
            if (reservation == null)
                return;

            reservation.ConsumesBudget = false;
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            ReservationGate.Release();
        }
    }
}
