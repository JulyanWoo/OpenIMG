using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Policies;
using OpenIMG.Infrastructure.Persistence;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Integration)]
public sealed class ProviderUsageRepositoryTests
{
    [Fact]
    public async Task DisabledGptCapAllowsReservationsBeyondHourlyAndDailyHistoryButKeepsDuplicateProtection()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"openimg-gpt-unlimited-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new TestDbContextFactory($"Data Source={databasePath}");
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
                var now = DateTimeOffset.UtcNow;
                context.ProviderUsageRecords.AddRange(Enumerable.Range(0, 130).Select(index => new OpenIMG.Core.Entities.ProviderUsageRecord
                {
                    Provider = "GPT",
                    OperationId = $"historical-{index}",
                    StartedAt = now.AddMinutes(-index)
                }));
                await context.SaveChangesAsync();
            }

            var repository = new ProviderUsageRepository(factory, new ProviderUsageBudget());
            var noLocalCap = new ProviderUsageBudgetOptions
            {
                Enabled = false,
                MaxOperationsPerWindow = 40,
                WindowDurationMinutes = 180,
                SafetyReserve = 5,
                OptionalMaxOperationsPerDay = 120
            };
            var nowForReservation = DateTimeOffset.UtcNow;

            var first = await repository.TryReserveAsync("GPT", "unlimited-operation", noLocalCap, nowForReservation);
            var duplicate = await repository.TryReserveAsync("GPT", "unlimited-operation", noLocalCap, nowForReservation.AddSeconds(1));

            Assert.True(first.Allowed);
            Assert.False(duplicate.Allowed);
            Assert.Equal("ProviderOperationAlreadyReserved", duplicate.Reason);
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(131, await verification.ProviderUsageRecords.CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task RollingGptCeilingIsDurableAndOneOperationCannotBeReservedTwice()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"openimg-gpt-budget-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new TestDbContextFactory($"Data Source={databasePath}");
            await using (var context = await factory.CreateDbContextAsync())
                await context.Database.EnsureCreatedAsync();

            var repository = new ProviderUsageRepository(factory, new ProviderUsageBudget());
            var budget = new ProviderUsageBudgetOptions
            {
                Enabled = true,
                MaxOperationsPerWindow = 2,
                WindowDurationMinutes = 180,
                SafetyReserve = 0
            };
            var now = DateTimeOffset.UtcNow;

            var first = await repository.TryReserveAsync("GPT", "operation-1", budget, now);
            var duplicate = await repository.TryReserveAsync("GPT", "operation-1", budget, now.AddMinutes(1));
            var second = await repository.TryReserveAsync("GPT", "operation-2", budget, now.AddMinutes(2));
            var atLimit = await repository.TryReserveAsync("GPT", "operation-3", budget, now.AddMinutes(3));

            Assert.True(first.Allowed);
            Assert.False(duplicate.Allowed);
            Assert.Equal("ProviderOperationAlreadyReserved", duplicate.Reason);
            Assert.True(second.Allowed);
            Assert.False(atLimit.Allowed);
            Assert.Equal("ProviderBudgetExceeded", atLimit.Reason);

            await using (var verification = await factory.CreateDbContextAsync())
                Assert.Equal(2, await verification.ProviderUsageRecords.CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ExplicitlyUnsubmittedOperationDoesNotConsumeTheDurableBudget()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"openimg-gpt-release-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new TestDbContextFactory($"Data Source={databasePath}");
            await using (var context = await factory.CreateDbContextAsync())
                await context.Database.EnsureCreatedAsync();

            var repository = new ProviderUsageRepository(factory, new ProviderUsageBudget());
            var budget = new ProviderUsageBudgetOptions
            {
                Enabled = true,
                MaxOperationsPerWindow = 1,
                WindowDurationMinutes = 180,
                SafetyReserve = 0
            };
            var now = DateTimeOffset.UtcNow;

            Assert.True((await repository.TryReserveAsync("GPT", "not-sent", budget, now)).Allowed);
            await repository.ReleaseUnsubmittedReservationAsync("GPT", "not-sent");
            Assert.True((await repository.TryReserveAsync("GPT", "sent", budget, now.AddMinutes(1))).Allowed);

            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(2, await verification.ProviderUsageRecords.CountAsync());
            Assert.Equal(1, await verification.ProviderUsageRecords.CountAsync(record => record.ConsumesBudget));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<OpenImgDbContext>
    {
        private DbContextOptions<OpenImgDbContext> CreateOptions() =>
            new DbContextOptionsBuilder<OpenImgDbContext>().UseSqlite(connectionString).Options;

        public OpenImgDbContext CreateDbContext() => new(CreateOptions());

        public Task<OpenImgDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
