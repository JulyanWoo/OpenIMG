namespace OpenIMG.Core.Models;

public record NormalizedRectangle(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double Area => Math.Max(0.0, Width) * Math.Max(0.0, Height);

    public NormalizedRectangle Clamp()
    {
        var clampedX = Math.Clamp(X, 0.0, 1.0);
        var clampedY = Math.Clamp(Y, 0.0, 1.0);
        var clampedW = Math.Clamp(Width, 0.0, Math.Max(0.0, 1.0 - clampedX));
        var clampedH = Math.Clamp(Height, 0.0, Math.Max(0.0, 1.0 - clampedY));
        return new NormalizedRectangle(clampedX, clampedY, clampedW, clampedH);
    }

    public double CalculateIoU(NormalizedRectangle other)
    {
        var x1 = Math.Max(X, other.X);
        var y1 = Math.Max(Y, other.Y);
        var x2 = Math.Min(Right, other.Right);
        var y2 = Math.Min(Bottom, other.Bottom);

        var intersectionWidth = Math.Max(0.0, x2 - x1);
        var intersectionHeight = Math.Max(0.0, y2 - y1);
        var intersectionArea = intersectionWidth * intersectionHeight;

        var unionArea = Area + other.Area - intersectionArea;
        return unionArea > 0.0 ? intersectionArea / unionArea : 0.0;
    }

    public double IntersectionOverSelf(NormalizedRectangle other)
    {
        var x1 = Math.Max(X, other.X);
        var y1 = Math.Max(Y, other.Y);
        var x2 = Math.Min(Right, other.Right);
        var y2 = Math.Min(Bottom, other.Bottom);

        var intersectionWidth = Math.Max(0.0, x2 - x1);
        var intersectionHeight = Math.Max(0.0, y2 - y1);
        var intersectionArea = intersectionWidth * intersectionHeight;

        return Area > 0.0 ? intersectionArea / Area : 0.0;
    }

    public bool Intersects(NormalizedRectangle other)
    {
        var x1 = Math.Max(X, other.X);
        var y1 = Math.Max(Y, other.Y);
        var x2 = Math.Min(Right, other.Right);
        var y2 = Math.Min(Bottom, other.Bottom);
        return (x2 > x1) && (y2 > y1);
    }

    public static NormalizedRectangle FromProtectedRegion(ProtectedRegion pr) =>
        new(pr.X, pr.Y, pr.Width, pr.Height);

    public ProtectedRegion ToProtectedRegion(string regionType = "", string description = "", double importance = 1.0) =>
        new()
        {
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            RegionType = regionType,
            Description = description,
            Importance = importance
        };
}
