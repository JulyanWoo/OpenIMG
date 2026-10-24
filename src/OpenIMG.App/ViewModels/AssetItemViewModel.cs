using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Application.Restoration;

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
    public ImageWorkState WorkState { get; private set; } = new(null, false, false, false, false, "");
    public string ResultHint => WorkState.Hint;
    public string StatusLabel => WorkState.MissingResult ? "Sin resultado" : Status switch
    {
        ImageProcessingStatus.NeedsReview => "Por revisar",
        ImageProcessingStatus.Accepted => "Aceptada",
        ImageProcessingStatus.Failed => WorkState.HasCurrentResult ? "Rechazada / fallida" : "Fallida",
        ImageProcessingStatus.Pending => "Pendiente",
        ImageProcessingStatus.Cancelled => "Cancelada",
        ImageProcessingStatus.Paused => "Pausada",
        _ => "En curso"
    };
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
        var latestAvailableCandidate = ImageWorkState.LatestAvailableAttempt(Asset);
        RestoredImagePath = latestAvailableCandidate?.GeneratedImagePath;
        WorkState = ImageWorkState.Read(Asset);
        OnPropertyChanged(nameof(WorkState));
        OnPropertyChanged(nameof(ResultHint));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBadgeColor));
    }
}
