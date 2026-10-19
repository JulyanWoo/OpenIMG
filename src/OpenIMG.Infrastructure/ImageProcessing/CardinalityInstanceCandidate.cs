using OpenCvSharp;

namespace OpenIMG.Infrastructure.ImageProcessing;

public enum CardinalityConfidenceStatus
{
    Reliable,
    Ambiguous,
    InsufficientData
}

public sealed class CardinalityInstanceCandidate
{
    public int Id { get; set; }
    public Rect Bounds { get; set; }
    public Point2d Centroid { get; set; }
    public double Orientation { get; set; } // degrees [-90, 90]
    public double AspectRatio { get; set; }
    public double Area { get; set; }
    public double Compactness { get; set; }
    public double Elongation { get; set; }
    public double Confidence { get; set; }
    public double GeometryCompatibility { get; set; } = 1.0;
    public double MultiScaleStability { get; set; } = 1.0;
    public List<int> SourceContourIds { get; set; } = new();
    public List<Point> Points { get; set; } = new();
}

public sealed class CardinalityValidationResult
{
    public string ConstraintId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int OriginalObservedCount { get; set; }
    public int RestoredDetectedCount { get; set; }
    public CardinalityConfidenceStatus Status { get; set; }
    public double AverageConfidence { get; set; }
    public List<CardinalityInstanceCandidate> RestoredCandidates { get; set; } = new();
    public List<CardinalityInstanceCandidate> OriginalCandidates { get; set; } = new();
    public List<OpenIMG.Core.Models.ValidationEvidence> Evidences { get; set; } = new();
    public Dictionary<string, string> DiagnosticImagePaths { get; set; } = new();
}
