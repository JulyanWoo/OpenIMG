using OpenCvSharp;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.ImageProcessing;

/// <summary>
/// Builds the explicit furniture, scene, removable, uncertainty and boundary masks
/// consumed by the validation pipeline.
/// </summary>
internal static class OpenCvValidationMaskBuilder
{
    internal static (Mat FurnitureMask, Mat SceneMask, Mat RemovableMask, Mat UncertainMask, Mat BoundaryMask)
    Build(int width, int height, Mat origGray, FurnitureAnalysis analysis)
    {
        var rawFurniture = new Mat(height, width, MatType.CV_8UC1, new Scalar(0));
        var uncertain = new Mat(height, width, MatType.CV_8UC1, new Scalar(0));
        var removable = new Mat(height, width, MatType.CV_8UC1, new Scalar(0));

        // 1. Components
        foreach (var comp in analysis.Components)
        {
            if (comp.Region != null)
            {
                var rx = Math.Clamp((int)(comp.Region.X * width), 0, width - 1);
                var ry = Math.Clamp((int)(comp.Region.Y * height), 0, height - 1);
                var rw = Math.Clamp((int)(comp.Region.Width * width), 1, width - rx);
                var rh = Math.Clamp((int)(comp.Region.Height * height), 1, height - ry);
                Cv2.Rectangle(rawFurniture, new Rect(rx, ry, rw, rh), new Scalar(255), -1);
            }
        }

        // 2. Protected Regions
        foreach (var pr in analysis.ProtectedRegions)
        {
            var rx = Math.Clamp((int)(pr.X * width), 0, width - 1);
            var ry = Math.Clamp((int)(pr.Y * height), 0, height - 1);
            var rw = Math.Clamp((int)(pr.Width * width), 1, width - rx);
            var rh = Math.Clamp((int)(pr.Height * height), 1, height - ry);
            Cv2.Rectangle(rawFurniture, new Rect(rx, ry, rw, rh), new Scalar(255), -1);
        }

        // 3. Scene Objects with Region Precedence, Occlusion Model, & Conservative Ownership Safety
        using var tempDilated = new Mat();
        using var tempEroded = new Mat();
        using var tempDilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9));
        using var tempErodeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.Dilate(rawFurniture, tempDilated, tempDilateKernel);
        Cv2.Erode(rawFurniture, tempEroded, tempErodeKernel);
        using var initialBoundaryMask = new Mat();
        Cv2.Subtract(tempDilated, tempEroded, initialBoundaryMask);

        foreach (var so in analysis.SceneObjects)
        {
            if (so.Region != null)
            {
                var rx = Math.Clamp((int)(so.Region.X * width), 0, width - 1);
                var ry = Math.Clamp((int)(so.Region.Y * height), 0, height - 1);
                var rw = Math.Clamp((int)(so.Region.Width * width), 1, width - rx);
                var rh = Math.Clamp((int)(so.Region.Height * height), 1, height - ry);
                var rect = new Rect(rx, ry, rw, rh);

                // Check overlap with existing furniture mask and furniture boundary mask
                using var subMask = new Mat(rawFurniture, rect);
                using var boundarySub = new Mat(initialBoundaryMask, rect);

                bool isReflectionOnGlass = so.Classification == SceneClassification.Reflection &&
                                           so.Occlusion == OcclusionType.SceneOnly;

                bool hasFurnitureOverlap = (so.OverlapsFurniture || Cv2.CountNonZero(subMask) > 0) && !isReflectionOnGlass;
                bool touchesBoundary = Cv2.CountNonZero(boundarySub) > 0;

                var desc = (so.Id + " " + so.Description).ToLowerInvariant();
                bool isPotentialFurnitureFeature = desc.Contains("appliance") || desc.Contains("microwave") ||
                                                   desc.Contains("oven") || desc.Contains("stove") ||
                                                   desc.Contains("range") || desc.Contains("cooktop") ||
                                                   desc.Contains("dishwasher") || desc.Contains("sink") ||
                                                   desc.Contains("faucet") || desc.Contains("tap") ||
                                                   desc.Contains("fridge") || desc.Contains("refrigerator") ||
                                                   desc.Contains("hood") || desc.Contains("handle") ||
                                                   desc.Contains("knob") || desc.Contains("pull") ||
                                                   desc.Contains("hinge") || desc.Contains("hardware") ||
                                                   desc.Contains("trim") || desc.Contains("moulding") ||
                                                   desc.Contains("molding") || desc.Contains("decoration") ||
                                                   desc.Contains("attached") || desc.Contains("integrated") ||
                                                   desc.Contains("built-in");

                bool isLowConfidence = so.Confidence < 0.85;

                bool isOccludingFurniture = so.Occlusion == OcclusionType.FurniturePartiallyOccluded ||
                                            so.Occlusion == OcclusionType.FurnitureHeavilyOccluded ||
                                            so.Occlusion == OcclusionType.Uncertain;

                bool isOwnershipUnclear = so.Classification == SceneClassification.Uncertain ||
                                          so.Precedence == RegionPrecedence.Uncertain ||
                                          (string.IsNullOrWhiteSpace(so.Id) && string.IsNullOrWhiteSpace(so.Description));

                bool forceUncertain = isLowConfidence ||
                                      hasFurnitureOverlap ||
                                      touchesBoundary ||
                                      isPotentialFurnitureFeature ||
                                      isOccludingFurniture ||
                                      isOwnershipUnclear;

                bool isRemovableClass = so.IsRemovable ||
                                        so.Classification == SceneClassification.RemovableClutter ||
                                        so.Classification == SceneClassification.Person ||
                                        so.Classification == SceneClassification.Reflection;

                // Precedence: Furniture protection always wins over removal permission
                if (forceUncertain)
                {
                    // Overlap, boundary touch, low confidence, or potential furniture element resolves to Uncertain
                    Cv2.Rectangle(uncertain, rect, new Scalar(255), -1);
                    Cv2.Rectangle(rawFurniture, rect, new Scalar(255), -1);
                }
                else if (isRemovableClass && so.Precedence == RegionPrecedence.RemovableScene)
                {
                    Cv2.Rectangle(removable, rect, new Scalar(255), -1);
                }
                else if (so.Classification == SceneClassification.PermanentEnvironment || so.Precedence == RegionPrecedence.PermanentEnvironment)
                {
                    // Permanent environment (e.g. wall/floor), not furniture
                }
                else
                {
                    Cv2.Rectangle(rawFurniture, rect, new Scalar(255), -1);
                }
            }
        }

        // Fallback if no components were given
        if (Cv2.CountNonZero(rawFurniture) == 0)
        {
            using var silMat = new Mat();
            Cv2.Threshold(origGray, silMat, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
            silMat.CopyTo(rawFurniture);
        }

        // Dilate to form FurnitureValidationMask with conservative expansion around boundaries (Specification 1)
        var furnitureValidationMask = new Mat();
        using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9));
        Cv2.Dilate(rawFurniture, furnitureValidationMask, dilateKernel);

        // Boundary mask: dilated minus eroded
        var boundaryMask = new Mat();
        using var erodeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var eroded = new Mat();
        Cv2.Erode(rawFurniture, eroded, erodeKernel);
        Cv2.Subtract(furnitureValidationMask, eroded, boundaryMask);
        rawFurniture.Dispose();

        // Scene mask: NOT of FurnitureValidationMask
        var sceneValidationMask = new Mat();
        Cv2.BitwiseNot(furnitureValidationMask, sceneValidationMask);

        // Removable must be outside furnitureValidationMask (unless glass reflection)
        Cv2.BitwiseAnd(removable, sceneValidationMask, removable);

        return (furnitureValidationMask, sceneValidationMask, removable, uncertain, boundaryMask);
    }
}
