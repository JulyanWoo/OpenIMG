using OpenCvSharp;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.ImageProcessing;

// Geometry-only checks: no semantic model and no colour-only failure.
public static class OpenCvStructuralSafetyValidator
{
    public static List<ValidationEvidence> Validate(Mat originalGray, Mat restoredGray, FurnitureAnalysis analysis)
    {
        var evidence = new List<ValidationEvidence>();
        using var diff = new Mat(); Cv2.Absdiff(originalGray, restoredGray, diff);
        using var changed = new Mat(); Cv2.Threshold(diff, changed, 28, 255, ThresholdTypes.Binary);
        foreach (var absence in analysis.ProtectedAbsenceRegions.Where(x => x.Strict && x.Confidence >= .80))
        {
            using var o = Crop(originalGray, absence.Region); using var r = Crop(restoredGray, absence.Region); using var c = Crop(changed, absence.Region);
            if (o.Empty() || r.Empty()) continue;
            using var oe = Edges(o); using var re = Edges(r); using var unexpected = new Mat();
            Cv2.Subtract(re, oe, unexpected); var changedRatio = Cv2.CountNonZero(c) / (double)(c.Rows * c.Cols);
            var edgeRatio = Cv2.CountNonZero(unexpected) / (double)Math.Max(1, Cv2.CountNonZero(re));
            using var labels = new Mat(); using var stats = new Mat(); using var centroids = new Mat();
            var count = Cv2.ConnectedComponentsWithStats(unexpected, labels, stats, centroids);
            var largest = Enumerable.Range(1, Math.Max(0, count - 1)).Select(i => stats.Get<int>(i, (int)ConnectedComponentsTypes.Area)).DefaultIfEmpty(0).Max();
            var rectangular = largest > Math.Max(80, c.Rows * c.Cols / 100);
            if (changedRatio > .12 && edgeRatio > .22 && rectangular)
                evidence.Add(new ValidationEvidence { Category = ValidationEvidenceCategory.Structural, Type = "UnexpectedStructureInProtectedAbsence", Region = absence.ConstraintId, Score = changedRatio, Threshold = .12, Severity = ValidationSeverity.Critical, Passed = false, Message = $"Coherent structure intrudes into protected absence '{absence.ConstraintId}' (changed={changedRatio:P1}, unexpected edges={edgeRatio:P1}, largest cluster={largest}px)." });
        }
        foreach (var detail in analysis.PersistentSceneDetails.Where(x => x.Strict && x.Confidence >= .80))
        {
            using var o = Crop(originalGray, detail.Region); using var r = Crop(restoredGray, detail.Region); if (o.Empty() || r.Empty()) continue;
            using var oe = Edges(o); using var re = Edges(r); var originalEdges = Cv2.CountNonZero(oe); using var missing = new Mat(); Cv2.Subtract(oe, re, missing);
            if (originalEdges > 20 && Cv2.CountNonZero(missing) / (double)originalEdges > .70)
                evidence.Add(new ValidationEvidence { Category = ValidationEvidenceCategory.Diagnostic, Type = "PersistentSceneDetailRemoved", Region = detail.CandidateId, Score = Cv2.CountNonZero(missing) / (double)originalEdges, Threshold = .70, Severity = ValidationSeverity.Critical, Passed = false, Message = $"Persistent scene detail '{detail.CandidateId}' lost its defining geometry." });
        }
        return evidence;
    }
    private static Mat Edges(Mat m) { var e = new Mat(); Cv2.Canny(m, e, 60, 140); return e; }
    private static Mat Crop(Mat source, ProtectedRegion region) { var x=Math.Clamp((int)(region.X*source.Width),0,source.Width-1); var y=Math.Clamp((int)(region.Y*source.Height),0,source.Height-1); var w=Math.Clamp((int)(region.Width*source.Width),1,source.Width-x); var h=Math.Clamp((int)(region.Height*source.Height),1,source.Height-y); return new Mat(source,new Rect(x,y,w,h)); }
}
