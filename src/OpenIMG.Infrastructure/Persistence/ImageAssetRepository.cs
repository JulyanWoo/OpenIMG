using Microsoft.EntityFrameworkCore;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Application.Policies;

namespace OpenIMG.Infrastructure.Persistence;

public class ImageAssetRepository : IImageAssetRepository
{
    private readonly IDbContextFactory<OpenImgDbContext> _contextFactory;

    public ImageAssetRepository(IDbContextFactory<OpenImgDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<ImageAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ImageAssets
            .Include(a => a.ProcessingJobs)
                .ThenInclude(j => j.Attempts)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public async Task<ImageAsset?> GetByHashAsync(Guid projectId, string sha256Hash, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ImageAssets
            .FirstOrDefaultAsync(a => a.ProjectId == projectId && a.Sha256Hash == sha256Hash, cancellationToken);
    }

    public async Task<IReadOnlyList<ImageAsset>> GetByProjectIdAsync(
        Guid projectId,
        ImageProcessingStatus? statusFilter = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.ImageAssets
            .Include(a => a.ProcessingJobs)
                .ThenInclude(j => j.Attempts)
            .Where(a => a.ProjectId == projectId);

        if (statusFilter.HasValue)
        {
            query = query.Where(a => a.Status == statusFilter.Value);
        }

        return await query.OrderBy(a => a.OriginalFileName).ToListAsync(cancellationToken);
    }

    public async Task<int> ResetProjectProcessingStateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var assets = await context.ImageAssets
            .Include(asset => asset.ProcessingJobs)
                .ThenInclude(job => job.Attempts)
            .Where(asset => asset.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var asset in assets)
        {
            asset.Status = ImageProcessingStatus.Pending;
            asset.UpdatedAt = now;

            var latestJob = asset.ProcessingJobs
                .OrderByDescending(job => job.CreatedAt)
                .FirstOrDefault();

            var requiresProviderRecovery = latestJob?.Attempts.Any(attempt =>
                ProviderOperationRecoveryPolicy.MayHaveSubmittedGeneration(attempt.ProviderOperationState) ||
                (attempt.CompletedAt == null && attempt.ProviderOperationState is not ProviderOperationState.GenerationPrepared and
                    not ProviderOperationState.AnalysisCompleted)) == true;

            // A fresh job gives a true new attempt sequence while keeping every
            // prior result and attempt attached to its historical job. Do not
            // detach an operation that may already exist remotely: retain that
            // job as the latest one so the orchestrator's recovery guard sees it.
            if (latestJob == null || !requiresProviderRecovery)
            {
                var freshJob = new ProcessingJob
                {
                    ImageAssetId = asset.Id,
                    Status = ImageProcessingStatus.Pending,
                    AttemptCount = 0,
                    CurrentStep = "Queued after user reset",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                context.ProcessingJobs.Add(freshJob);
                continue;
            }

            latestJob.Status = ImageProcessingStatus.Pending;
            latestJob.AttemptCount = 0;
            latestJob.CurrentStep = "Pending after user reset";
            latestJob.ErrorMessage = null;
            latestJob.FailureReason = null;
            latestJob.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
        return assets.Count;
    }

    public async Task<ImageAsset> AddAsync(ImageAsset asset, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.ImageAssets.Add(asset);
        await context.SaveChangesAsync(cancellationToken);
        return asset;
    }

    public async Task AddRangeAsync(IEnumerable<ImageAsset> assets, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.ImageAssets.AddRange(assets);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(ImageAsset asset, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        var persisted = await context.ImageAssets.FindAsync([asset.Id], cancellationToken)
            ?? throw new InvalidOperationException($"Image asset {asset.Id} no longer exists.");
        context.Entry(persisted).CurrentValues.SetValues(asset);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProcessingJob> CreateJobAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var job = new ProcessingJob
        {
            ImageAssetId = assetId,
            Status = ImageProcessingStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        context.ProcessingJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task<ProcessingJob?> GetLatestJobForAssetAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var jobs = await context.ProcessingJobs
            .Include(j => j.Attempts)
            .Where(j => j.ImageAssetId == assetId)
            .ToListAsync(cancellationToken);

        return jobs.OrderByDescending(j => j.CreatedAt).FirstOrDefault();
    }

    public async Task UpdateJobAsync(ProcessingJob job, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        var persisted = await context.ProcessingJobs.FindAsync([job.Id], cancellationToken)
            ?? throw new InvalidOperationException($"Processing job {job.Id} no longer exists.");
        context.Entry(persisted).CurrentValues.SetValues(job);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<RestorationAttempt> AddAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.RestorationAttempts.Add(attempt);
        await context.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    public async Task UpdateAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var persisted = await context.RestorationAttempts.FindAsync([attempt.Id], cancellationToken)
            ?? throw new InvalidOperationException($"Restoration attempt {attempt.Id} no longer exists.");
        context.Entry(persisted).CurrentValues.SetValues(attempt);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RestorationAttempt>> GetAttemptsForJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RestorationAttempts
            .Where(a => a.ProcessingJobId == jobId)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync(cancellationToken);
    }

    public async Task RemoveIncompleteAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var incomplete = await context.RestorationAttempts
            .Where(a => a.ProcessingJobId == jobId && a.CompletedAt == null)
            .ToListAsync(cancellationToken);
        if (incomplete.Count > 0)
        {
            context.RestorationAttempts.RemoveRange(incomplete);
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
