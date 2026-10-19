using OpenCvSharp;
using System.IO;
using OpenIMG.Core.Models;
using OpenIMG.Infrastructure.ImageProcessing;
namespace OpenIMG.Tests;
public class ComponentCardinalityValidatorTests
{
 [Theory][Trait("Category", TestCategories.Unit)][InlineData(1,1,false)][InlineData(1,2,true)][InlineData(4,4,false)][InlineData(4,5,true)]
 public void Counts_distinct_elongated_components(int expected,int actual,bool mismatch){var a=Temp();var b=Temp();try{Write(a,expected);Write(b,actual);var e=new OpenCvComponentCardinalityValidator().Validate(a,b,[C(expected,.9)]);Assert.Equal(mismatch,e.Any(x=>x.Type=="ComponentCardinalityMismatch"));if(mismatch)Assert.Equal(OpenIMG.Core.Enums.ValidationSeverity.Critical,e.Single().Severity);}finally{File.Delete(a);File.Delete(b);}}
 [Fact][Trait("Category", TestCategories.Unit)] public void Low_confidence_mismatch_is_warning(){var a=Temp();var b=Temp();try{Write(a,1);Write(b,2);Assert.Equal(OpenIMG.Core.Enums.ValidationSeverity.Warning,new OpenCvComponentCardinalityValidator().Validate(a,b,[C(1,.5)]).Single().Severity);}finally{File.Delete(a);File.Delete(b);}}
#if OPENIMG_ENGINEERING
 [Fact]
 [Trait("Category", TestCategories.Calibration)]
 public void AuditCurrentRestoredCandidateExtraction()
 {
  var repoRoot = TestArtifactWorkspace.FindRepoRoot();
  var manifestPath = Path.Combine(repoRoot, "calibration", "phase3", "calibration_manifest.json");
  var manifestItems = File.Exists(manifestPath)
      ? System.Text.Json.JsonSerializer.Deserialize<List<CalibrationManifestItem>>(File.ReadAllText(manifestPath))
      : null;
  var origPath = manifestItems?.FirstOrDefault(item => item.CalibrationId == "CAL01")?.SourcePath;
  var candBPath = Path.Combine(repoRoot, "calibration", "phase4c-live", "CAL01", "candidate_B_gemini.png");
  var candAPath = Path.Combine(repoRoot, "calibration", "phase4c-live", "CAL01", "candidate_A_local.png");
  if (string.IsNullOrWhiteSpace(origPath) || !File.Exists(origPath) || !File.Exists(candBPath)) return;
  var analysisJsonPath = Path.Combine(repoRoot, "calibration", "phase4c", "CAL01", "adapted_furniture_analysis.json");
  var analysis = System.Text.Json.JsonSerializer.Deserialize<FurnitureAnalysis>(File.ReadAllText(analysisJsonPath));
  if (analysis?.ComponentCardinalityConstraints == null || !analysis.ComponentCardinalityConstraints.Any(c => c.Strict))
  {
      var contractJsonPath = Path.Combine(repoRoot, "calibration", "phase4c", "CAL01", "local_furniture_contract.json");
      if (File.Exists(contractJsonPath))
      {
          var contract = System.Text.Json.JsonSerializer.Deserialize<LocalFurnitureContract>(File.ReadAllText(contractJsonPath));
          if (contract != null)
          {
              analysis = new OpenIMG.Application.Analysis.LocalToFurnitureAnalysisAdapter().Adapt(contract);
          }
      }
  }
  var constraint = analysis?.ComponentCardinalityConstraints?.FirstOrDefault(c => c.Strict)
      ?? new ComponentCardinalityConstraint { ConstraintId = "hw_cardinality_cal01", MinimumAllowedCount = 1, MaximumAllowedCount = 1, Strict = true, Confidence = 0.85 };

  var validator = new OpenCvComponentCardinalityValidator();
  var outputRoot = TestArtifactWorkspace.Create("cardinality-historical-audit");
  var diagA = Path.Combine(outputRoot, "diagnostics_A");
  var diagB = Path.Combine(outputRoot, "diagnostics_B");

  var resB = validator.ValidateDetailed(origPath, candBPath, new[] { constraint }, diagB, "CAL01_historical_B");
  var resA = validator.ValidateDetailed(origPath, candAPath, new[] { constraint }, diagA, "CAL01_historical_A");

  var cal01Results = new Dictionary<string, object>
  {
   ["Candidate_B"] = new
   {
    resB[0].OriginalObservedCount,
    resB[0].RestoredDetectedCount,
    Status = resB[0].Status.ToString(),
    resB[0].AverageConfidence,
    CandidateCount = resB[0].RestoredCandidates.Count,
    StrongCandidates = resB[0].RestoredCandidates.Where(c => c.Confidence >= 0.58).Select(c => new { c.Id, c.Bounds.X, c.Bounds.Y, c.Bounds.Width, c.Bounds.Height, c.AspectRatio, c.Confidence, c.GeometryCompatibility, c.MultiScaleStability }).ToList(),
    Evidences = resB[0].Evidences.Select(e => new { e.Type, e.Severity, e.Passed, e.Message }).ToList()
   },
   ["Candidate_A"] = new
   {
    resA[0].OriginalObservedCount,
    resA[0].RestoredDetectedCount,
    Status = resA[0].Status.ToString(),
    resA[0].AverageConfidence,
    CandidateCount = resA[0].RestoredCandidates.Count,
    Evidences = resA[0].Evidences.Select(e => new { e.Type, e.Severity, e.Passed, e.Message }).ToList()
   }
  };

  var valPath = Path.Combine(outputRoot, "cardinality_cal01_results.json");
  File.WriteAllText(valPath, System.Text.Json.JsonSerializer.Serialize(cal01Results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
  Directory.Delete(outputRoot, recursive: true);
 }
#endif

 [Fact]
 [Trait("Category", TestCategories.Unit)]
 public void Realistic_synthetic_fixture_with_seams_woodgrain_and_nested_contours_counts_accurately()
 {
     var origPath = Temp();
     var restSamePath = Temp();
     var restExtraPath = Temp();
     try
     {
         // 1 physical component in original
         WriteRealisticCabinetScene(origPath, 1);
         // Same 1 physical component in restored (should count as 1, not dozens despite woodgrain, seams, halos)
         WriteRealisticCabinetScene(restSamePath, 1);
         // 2 physical components in restored (one true extra component added among noisy seams)
         WriteRealisticCabinetScene(restExtraPath, 2);

         var validator = new OpenCvComponentCardinalityValidator();
         var constraint = new ComponentCardinalityConstraint
         {
             ConstraintId = "realistic_hardware_test",
             Category = "hardware",
             DeduplicatedCount = 1,
             Confidence = 0.90,
             Strict = true,
             Region = new ProtectedRegion { X = 0, Y = 0, Width = 1, Height = 1 }
         };

         // Test 1: 1 original vs 1 restored with noisy seams -> should match (0 mismatch evidence)
         var detailed1 = validator.ValidateDetailed(origPath, restSamePath, new[] { constraint });
         Assert.Single(detailed1);
         Assert.Equal(1, detailed1[0].RestoredDetectedCount);
         Assert.DoesNotContain(detailed1[0].Evidences, e => !e.Passed);

         // Test 2: 1 original vs 2 restored (one true extra component among noisy seams) -> should detect 2, mismatch!
         var detailed2 = validator.ValidateDetailed(origPath, restExtraPath, new[] { constraint });
         Assert.Single(detailed2);
         Assert.Equal(2, detailed2[0].RestoredDetectedCount);
         var mismatchEv = detailed2[0].Evidences.FirstOrDefault(e => e.Type == "ComponentCardinalityMismatch");
         Assert.NotNull(mismatchEv);
         Assert.False(mismatchEv.Passed);
     }
     finally
     {
         if (File.Exists(origPath)) File.Delete(origPath);
         if (File.Exists(restSamePath)) File.Delete(restSamePath);
         if (File.Exists(restExtraPath)) File.Delete(restExtraPath);
     }
 }

 private static void WriteRealisticCabinetScene(string path, int hardwareCount)
 {
     using var mat = new Mat(250, 250, MatType.CV_8UC1, new Scalar(75));

     // 1. Panel borders (outer border around cabinet door/drawer)
     Cv2.Rectangle(mat, new Rect(5, 5, 240, 240), new Scalar(40), 2);
     Cv2.Rectangle(mat, new Rect(15, 15, 220, 220), new Scalar(90), 1);

     // 2. Woodgrain-like line texture (wavy thin lines with subtle contrast)
     for (int y = 20; y < 240; y += 14)
     {
         var pts = new List<Point>();
         for (int x = 16; x < 234; x += 10)
         {
             int waveY = (int)(y + 2.5 * Math.Sin(x * 0.15));
             pts.Add(new Point(x, waveY));
         }
         for (int i = 0; i < pts.Count - 1; i++)
         {
             Cv2.Line(mat, pts[i], pts[i + 1], new Scalar(88), 1);
         }
     }

     // 3. Repeated drawer seams (horizontal seams across cabinet)
     int[] drawerSeamsY = { 45, 105, 165, 220 };
     foreach (var sy in drawerSeamsY)
     {
         // Dark seam line
         Cv2.Line(mat, new Point(15, sy), new Point(235, sy), new Scalar(30), 2);
         // Sharpening halo (light line immediately adjacent to dark seam)
         Cv2.Line(mat, new Point(15, sy + 2), new Point(235, sy + 2), new Scalar(115), 1);
     }

     // 4. Physical hardware component(s)
     DrawHardwareInstance(mat, 50, 70);

     if (hardwareCount >= 2)
     {
         // Second true extra component among noisy seams
         DrawHardwareInstance(mat, 140, 70);
     }

     // 5. Subpixel resampling / slight smoothing to simulate photographic capture
     using var smoothed = new Mat();
     Cv2.GaussianBlur(mat, smoothed, new Size(3, 3), 0.5);
     Cv2.ImWrite(path, smoothed);
 }

 private static void DrawHardwareInstance(Mat mat, int x, int y)
 {
     // Outer hardware body (strong contrast, 36x10 rect)
     Cv2.Rectangle(mat, new Rect(x, y, 36, 10), new Scalar(230), -1);

     // Reflective highlight inside hardware (inner contour)
     Cv2.Line(mat, new Point(x + 4, y + 3), new Point(x + 32, y + 3), new Scalar(255), 1);

     // Shadow edge beside hardware (dark contour below)
     Cv2.Line(mat, new Point(x, y + 11), new Point(x + 36, y + 11), new Scalar(25), 1);
     // Caps
     Cv2.Rectangle(mat, new Rect(x - 1, y, 2, 10), new Scalar(160), -1);
     Cv2.Rectangle(mat, new Rect(x + 35, y, 2, 10), new Scalar(160), -1);
 }

 static string Temp()=>Path.Combine(Path.GetTempPath(),$"openimg-{Guid.NewGuid():N}.png");
 static ComponentCardinalityConstraint C(int n,double c)=>new(){ConstraintId="x",Category="hardware",DeduplicatedCount=n,Confidence=c,Strict=true,Region=new ProtectedRegion{X=0,Y=0,Width=1,Height=1}};
 static void Write(string p,int n){using var m=new Mat(200,200,MatType.CV_8UC1,Scalar.Black);for(var i=0;i<n;i++)Cv2.Rectangle(m,new Rect(15+i*35,80,24,6),Scalar.White,-1);Cv2.ImWrite(p,m);}
}
