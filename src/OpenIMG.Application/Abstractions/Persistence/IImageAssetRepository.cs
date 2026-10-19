using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;

namespace OpenIMG.Application.Abstractions.Persistence;

public interface IImageAssetRepository
{
    Task<ImageAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ImageAsset?> GetByHashAsync(Guid projectId, string sha256Hash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ImageAsset>> GetByProjectIdAsync(Guid projectId, ImageProcessingStatus? statusFilter = null, CancellationToken cancellationToken = default);
    Task<int> ResetProjectProcessingStateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ImageAsset> AddAsync(ImageAsset asset, CancellationToken cancellationToken = default);
    Task AddRangeAsync(IEnumerable<ImageAsset> assets, CancellationToken cancellationToken = default);
    Task UpdateAsync(ImageAsset asset, CancellationToken cancellationToken = default);
    Task<ProcessingJob> CreateJobAsync(Guid assetId, CancellationToken cancellationToken = default);
    Task<ProcessingJob?> GetLatestJobForAssetAsync(Guid assetId, CancellationToken cancellationToken = default);
    Task UpdateJobAsync(ProcessingJob job, CancellationToken cancellationToken = default);
    Task<RestorationAttempt> AddAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default);
    Task UpdateAttemptAsync(RestorationAttempt attempt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RestorationAttempt>> GetAttemptsForJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task RemoveIncompleteAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default);
}
