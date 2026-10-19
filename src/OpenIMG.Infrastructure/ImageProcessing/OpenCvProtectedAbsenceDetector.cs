using OpenCvSharp;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Core.Models;
namespace OpenIMG.Infrastructure.ImageProcessing;
public sealed class OpenCvProtectedAbsenceDetector : IProtectedAbsenceDetector
{
 public IReadOnlyList<ProtectedAbsenceRegion> Detect(string path, LocalFurnitureContract c, string diagnostics)
 {
  using var im=Cv2.ImRead(path,ImreadModes.Grayscale); if(im.Empty()||c.FurnitureBounds is null||c.FurnitureConfidence<.70)return [];
  var b=c.FurnitureBounds; var bottom=b.Bottom; if(bottom>=.90)return [];
  var r=new ProtectedRegion{X=b.X,Y=bottom,Width=b.Width,Height=Math.Min(.30,1-bottom),RegionType="open_scene",Description="Contiguous low-structure region below furniture envelope",Importance=1};
  using var roi=new Mat(im,new Rect((int)(r.X*im.Width),(int)(r.Y*im.Height),Math.Max(1,(int)(r.Width*im.Width)),Math.Max(1,(int)(r.Height*im.Height)))); using var e=new Mat(); Cv2.Canny(roi,e,60,140);
  var density=Cv2.CountNonZero(e)/(double)(roi.Rows*roi.Cols); if(density>.08)return [];
  return [new ProtectedAbsenceRegion{ConstraintId="absence_below_furniture_envelope",Region=r,AbsenceType="NoFurnitureVolume",Confidence=.85,AdjacentFurnitureIds=c.StructuralRegions.Select(x=>x.Id).ToList(),SourceEvidence="High-confidence furniture envelope terminates above a contiguous low-structure scene region.",Strict=true}];
 }
}
