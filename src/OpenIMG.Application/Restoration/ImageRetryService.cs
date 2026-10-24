using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Policies;
using OpenIMG.Core.Enums;

namespace OpenIMG.Application.Restoration;

public enum ImageRetryResult { Queued, ManualRecoveryRequired, MissingSource, InProgress, Unavailable }

/// <summary>Requeues terminal photos while preserving remote-operation ownership and history.</summary>
public sealed class ImageRetryService(IImageAssetRepository repository)
{
    public async Task<ImageRetryResult> EnqueueAsync(Guid assetId, bool authorizeReplacement = false, CancellationToken cancellationToken = default)
    {
        var asset = await repository.GetByIdAsync(assetId, cancellationToken);
        if (asset is null) return ImageRetryResult.Unavailable;
        if (ImageWorkState.IsProcessing(asset.Status) || asset.Status == ImageProcessingStatus.Pending)
            return ImageRetryResult.InProgress;
        if (!File.Exists(asset.OriginalFilePath)) return ImageRetryResult.MissingSource;
        var state = ImageWorkState.Read(asset);
        if (state.ManualRecoveryRequired)
        {
            if (!authorizeReplacement || !state.MissingResult) return ImageRetryResult.ManualRecoveryRequired;
            var job = asset.ProcessingJobs.OrderByDescending(item => item.CreatedAt).First();
            foreach (var attempt in job.Attempts.Where(item => ProviderOperationRecoveryPolicy.MayHaveSubmittedGeneration(item.ProviderOperationState)))
            {
                attempt.ProviderOperationState = ProviderOperationState.Failed;
                attempt.ProviderFailureCategory = "UserAuthorizedReplacement";
                attempt.FailureDetails = "The user explicitly authorized a new generation without a recoverable conversation link. A remote image may still exist. Previous evidence and outputs were retained.";
                attempt.CompletedAt ??= DateTimeOffset.UtcNow;
                await repository.UpdateAttemptAsync(attempt, cancellationToken);
            }
        }
        asset.Status = ImageProcessingStatus.Pending;
        await repository.UpdateAsync(asset, cancellationToken);
        return ImageRetryResult.Queued;
    }
}
