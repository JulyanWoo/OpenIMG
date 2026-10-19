using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Entities;

public class ProcessingJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImageAssetId { get; set; }
    public ImageProcessingStatus Status { get; set; } = ImageProcessingStatus.Pending;
    public int AttemptCount { get; set; }
    public string? CurrentStep { get; set; }
    public string? ErrorMessage { get; set; }
    public RestorationFailureReason? FailureReason { get; set; }
    public string? FurnitureAnalysisJson { get; set; }
    public string? LocalFurnitureContractJson { get; set; }
    public string? AnalysisComparisonJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ImageAsset? ImageAsset { get; set; }
    public ICollection<RestorationAttempt> Attempts { get; set; } = new List<RestorationAttempt>();
}
