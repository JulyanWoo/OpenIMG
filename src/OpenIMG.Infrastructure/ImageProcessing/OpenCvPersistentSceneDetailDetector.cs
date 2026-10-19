using OpenCvSharp;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Core.Models;
namespace OpenIMG.Infrastructure.ImageProcessing;
public sealed class OpenCvPersistentSceneDetailDetector : IPersistentSceneDetailDetector
{
 public IReadOnlyList<SceneDetailCandidate> Detect(string path, LocalFurnitureContract c, string diagnostics)
 {
  using var im=Cv2.ImRead(path,ImreadModes.Grayscale); if(im.Empty()||c.FurnitureBounds is null)return [];
  using var edges=new Mat(); Cv2.Canny(im,edges,70,160); Cv2.FindContours(edges,out Point[][] cs,out _,RetrievalModes.External,ContourApproximationModes.ApproxSimple);
  var outp=new List<SceneDetailCandidate>(); foreach(var q in cs){var r=Cv2.BoundingRect(q); var a=r.Width*r.Height; if(a<80||a>im.Width*im.Height*.01)continue; var nx=r.X/(double)im.Width;var ny=r.Y/(double)im.Height;var nw=r.Width/(double)im.Width;var nh=r.Height/(double)im.Height; var b=c.FurnitureBounds; var near=Math.Abs(nx-b.Right)<.15||Math.Abs(ny-b.Bottom)<.15; if(!near||nw/nh>3||nh/nw>3)continue; outp.Add(new SceneDetailCandidate{CandidateId=$"persistent_scene_detail_{outp.Count+1}",Region=new ProtectedRegion{X=nx,Y=ny,Width=nw,Height=nh,RegionType="persistent_scene_detail",Description="Compact high-contrast scene detail",Importance=.9},Confidence=.82,ContrastScore=.8,BoundaryStrength=.8,ShapeDescriptor="compact_contour",AttachmentContext="adjacent_to_furniture",SourceEvidence="Compact contour adjacent to high-confidence furniture envelope.",Strict=true});}
  return outp.Take(8).ToList();
 }
}
