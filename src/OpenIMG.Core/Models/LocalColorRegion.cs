namespace OpenIMG.Core.Models;

public record LabColor(double L, double A, double B)
{
    public double DeltaE(LabColor other)
    {
        var dL = L - other.L;
        var da = A - other.A;
        var db = B - other.B;
        return Math.Sqrt(dL * dL + da * da + db * db);
    }
}

public record LocalColorRegion
{
    public NormalizedRectangle Region { get; init; } = new(0, 0, 0, 0);
    public LabColor DominantLab { get; init; } = new(0, 0, 0);
    public string RepresentativeRgb { get; init; } = "#FFFFFF";
    public double AreaPercentage { get; init; }
    public bool IsDominant { get; init; }
    public bool IsAccentCandidate { get; init; }
    public double Confidence { get; init; }
}
