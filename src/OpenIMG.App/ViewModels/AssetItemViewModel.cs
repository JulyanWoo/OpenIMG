using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;

namespace OpenIMG.App.ViewModels;

public partial class AssetItemViewModel : ObservableObject
{
    public ImageAsset Asset { get; }

    [ObservableProperty]
    private ImageProcessingStatus _status;

    [ObservableProperty]
    private string? _thumbnailPath;

    [ObservableProperty]
    private string? _restoredImagePath;

    [ObservableProperty]
    private int _attemptCount;

    public Guid Id => Asset.Id;
    public string FileName => Asset.OriginalFileName;
    public string OriginalFilePath => Asset.OriginalFilePath;
    public string Sha256Hash => Asset.Sha256Hash;
    public string DimensionsText => Asset.Width.HasValue && Asset.Height.HasValue
        ? $"{Asset.Width} × {Asset.Height}"
        : "Unknown";

    public string StatusBadgeColor => Status switch
    {
        ImageProcessingStatus.Accepted => "#10B981",
        ImageProcessingStatus.NeedsReview => "#F59E0B",
        ImageProcessingStatus.Failed => "#EF4444",
        ImageProcessingStatus.Analyzing or ImageProcessingStatus.Restoring or
        ImageProcessingStatus.LocalValidation or ImageProcessingStatus.RemoteValidation => "#3B82F6",
        ImageProcessingStatus.Paused => "#9CA3AF",
        _ => "#6B7280"
    };

    public AssetItemViewModel(ImageAsset asset)
    {
        Asset = asset;
        RefreshState();
    }

    public void RefreshState(ImageAsset? persistedState = null)
    {
        if (persistedState != null)
        {
            Asset.Status = persistedState.Status;
            Asset.ThumbnailPath = persistedState.ThumbnailPath;
            Asset.ProcessingJobs = persistedState.ProcessingJobs;
        }

        Status = Asset.Status;
        ThumbnailPath = Asset.ThumbnailPath;
        var latestJob = Asset.ProcessingJobs.OrderByDescending(j => j.CreatedAt).FirstOrDefault();
        AttemptCount = latestJob?.AttemptCount ?? 0;
        var latestAvailableCandidate = Asset.ProcessingJobs
            .SelectMany(job => job.Attempts)
            .Where(attempt => !string.IsNullOrWhiteSpace(attempt.GeneratedImagePath) && File.Exists(attempt.GeneratedImagePath))
            .OrderByDescending(attempt => attempt.CompletedAt ?? attempt.CreatedAt)
            .FirstOrDefault();
        RestoredImagePath = latestAvailableCandidate?.GeneratedImagePath;
        OnPropertyChanged(nameof(StatusBadgeColor));
    }
}
