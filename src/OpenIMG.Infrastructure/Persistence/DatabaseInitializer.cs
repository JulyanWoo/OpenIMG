using Microsoft.EntityFrameworkCore;

namespace OpenIMG.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    public static async Task InitializeAndMigrateAsync(OpenImgDbContext dbContext, CancellationToken cancellationToken = default)
    {
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS ProviderUsageRecords (Id TEXT NOT NULL CONSTRAINT PK_ProviderUsageRecords PRIMARY KEY, Provider TEXT NOT NULL, OperationId TEXT NOT NULL, StartedAt TEXT NOT NULL, ConsumesBudget INTEGER NOT NULL);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_ProviderUsageRecords_Provider_OperationId ON ProviderUsageRecords (Provider, OperationId);",
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ProviderUsageRecords_Provider_StartedAt ON ProviderUsageRecords (Provider, StartedAt);",
            cancellationToken);

        // Safe idempotent column additions for schema evolution
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN CalibrationRemoteValidationJson TEXT;",
                cancellationToken);
        }
        catch
        {
            // Column already exists or table does not exist
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN AnalysisSource INTEGER;",
                cancellationToken);
        }
        catch
        {
            // Column already exists or table does not exist
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE ProcessingJobs ADD COLUMN LocalFurnitureContractJson TEXT;",
                cancellationToken);
        }
        catch
        {
            // Column already exists or table does not exist
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE ProcessingJobs ADD COLUMN AnalysisComparisonJson TEXT;",
                cancellationToken);
        }
        catch
        {
            // Column already exists or table does not exist
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN GeminiStructuredAnalysisUsed INTEGER DEFAULT 0;",
                cancellationToken);
        }
        catch { }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN GeminiRestorationUsed INTEGER DEFAULT 0;",
                cancellationToken);
        }
        catch { }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN GeminiComparativeValidationUsed INTEGER DEFAULT 0;",
                cancellationToken);
        }
        catch { }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN GeminiOperationsTotal INTEGER DEFAULT 0;",
                cancellationToken);
        }
        catch { }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN RemoteValidationSkipped INTEGER DEFAULT 0;",
                cancellationToken);
        }
        catch { }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN RemoteValidationSkipReason TEXT;",
                cancellationToken);
        }
        catch { }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN RemoteValidationRequirement TEXT;",
                cancellationToken);
        }
        catch { }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN RemoteValidationReasonsJson TEXT;",
                cancellationToken);
        }
        catch { }

        // Additive product-readiness fields. These preserve operation recovery
        // and human review independently of the automated decision.
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN ProviderOperationId TEXT;",
                cancellationToken);
        }
        catch { }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN ProviderOperationState INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);
        }
        catch { }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN ProviderFailureCategory TEXT;",
                cancellationToken);
        }
        catch { }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE RestorationAttempts ADD COLUMN HumanReviewJson TEXT;",
                cancellationToken);
        }
        catch { }
    }
}
