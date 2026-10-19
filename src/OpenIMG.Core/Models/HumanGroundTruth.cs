namespace OpenIMG.Core.Models;

public enum HumanGroundTruthJudgment
{
    Pending,
    Good,
    Bad,
    Uncertain
}

public enum DefectTag
{
    BadColor,
    BadGeometry,
    BadComponent,
    BadMaterial,
    BadAddedDetail,
    BadRemovedDetail,
    BadComposition,
    BadSceneCleanup,
    FurnitureDamagedByCleanup,
    ExcessiveBackgroundChange,
    IncorrectObjectRemoval,
    BadReframing,
    DistortedProportions,
    InventedOccludedFurniture,
    BadColorCastCorrection
}

public enum AnalysisGroundTruthQuality
{
    Correct,
    MostlyCorrect,
    Incorrect,
    Uncertain
}

public enum ProtectedRegionQuality
{
    Good,
    Approximate,
    Poor,
    Missing
}

public class HumanGroundTruth
{
    public string CalibrationId { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public HumanGroundTruthJudgment OverallJudgment { get; set; } = HumanGroundTruthJudgment.Pending;
    public string? FurnitureJudgment { get; set; }
    public string? SceneCleanupJudgment { get; set; }
    public string? ReframingJudgment { get; set; }
    public List<DefectTag> DefectTags { get; set; } = new();
    public string? HumanNotes { get; set; }
    public AnalysisGroundTruthQuality AnalysisQuality { get; set; } = AnalysisGroundTruthQuality.Uncertain;
    public ProtectedRegionQuality RegionQuality { get; set; } = ProtectedRegionQuality.Approximate;
    public DateTimeOffset? ReviewedAt { get; set; }
}
