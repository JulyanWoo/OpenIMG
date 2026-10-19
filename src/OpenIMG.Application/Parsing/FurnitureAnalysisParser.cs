using System.Text.Json;
using System.Text.RegularExpressions;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Parsing;

public static class FurnitureAnalysisParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string CleanJsonContent(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return string.Empty;

        var text = rawResponse.Trim();

        // Strip markdown code fences ```json ... ``` or ``` ... ```
        var match = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            text = match.Groups[1].Value.Trim();
        }
        else
        {
            // If there is leading or trailing text outside { ... }, find first { and last }
            var startIdx = text.IndexOf('{');
            var endIdx = text.LastIndexOf('}');
            if (startIdx >= 0 && endIdx > startIdx)
            {
                text = text.Substring(startIdx, endIdx - startIdx + 1);
            }
        }

        return text;
    }

    public static FurnitureAnalysis Parse(string rawResponse)
    {
        var cleaned = CleanJsonContent(rawResponse);
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new FormatException("Response does not contain valid JSON content for FurnitureAnalysis.");

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var analysis = new FurnitureAnalysis
            {
                FurnitureType = GetStringProp(root, "furnitureType"),
                GeometryDescription = GetStringProp(root, "geometryDescription"),
                Confidence = GetDoubleProp(root, "confidence", 1.0),
                MainMaterials = GetStringList(root, "mainMaterials"),
                MainColors = GetStringList(root, "mainColors"),
                AccentColors = GetStringList(root, "accentColors"),
                ColorLocations = GetDictionary(root, "colorLocations"),
                Doors = GetStringList(root, "doors"),
                Drawers = GetStringList(root, "drawers"),
                Shelves = GetStringList(root, "shelves"),
                Handles = GetStringList(root, "handles"),
                GlassAreas = GetStringList(root, "glassAreas"),
                Panels = GetStringList(root, "panels"),
                DecorativeElements = GetStringList(root, "decorativeElements"),
                StructuralElements = GetStringList(root, "structuralElements"),
                ProtectedFeatures = GetStringList(root, "protectedFeatures"),
                ProtectedRegions = ParseRegions(root, "protectedRegions"),
                Components = ParseComponents(root, "components"),
                UncertainFeatures = GetStringList(root, "uncertainFeatures"),
                PhotoDefects = GetStringList(root, "photoDefects"),
                FramingAnalysis = ParseFramingAnalysis(root, "framingAnalysis"),
                SceneObjects = ParseSceneObjects(root, "sceneObjects"),
                RemovableClutter = GetStringList(root, "removableClutter"),
                RawAnalysisJson = cleaned
            };

            return analysis;
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException($"Failed to deserialize FurnitureAnalysis JSON: {ex.Message}", ex);
        }
    }

    private static string GetStringProp(JsonElement element, string propName)
    {
        if (element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static double GetDoubleProp(JsonElement element, string propName, double defaultVal)
    {
        if (element.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var val))
                return val;
            if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out var parsed))
                return parsed;
        }
        return defaultVal;
    }

    private static List<string> GetStringList(JsonElement element, string propName)
    {
        var list = new List<string>();
        if (element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var str = item.GetString();
                    if (!string.IsNullOrWhiteSpace(str))
                        list.Add(str);
                }
            }
        }
        return list;
    }

    private static Dictionary<string, string> GetDictionary(JsonElement element, string propName)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in prop.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    dict[p.Name] = p.Value.GetString() ?? string.Empty;
            }
        }
        return dict;
    }

    private static List<ProtectedRegion> ParseRegions(JsonElement element, string propName)
    {
        var list = new List<ProtectedRegion>();
        if (element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    list.Add(new ProtectedRegion
                    {
                        X = GetDoubleProp(item, "x", 0.0),
                        Y = GetDoubleProp(item, "y", 0.0),
                        Width = GetDoubleProp(item, "width", 0.0),
                        Height = GetDoubleProp(item, "height", 0.0),
                        RegionType = GetStringProp(item, "regionType"),
                        Description = GetStringProp(item, "description"),
                        Importance = GetDoubleProp(item, "importance", 1.0)
                    });
                }
            }
        }
        return list;
    }

    private static List<FurnitureComponent> ParseComponents(JsonElement element, string propName)
    {
        var list = new List<FurnitureComponent>();
        if (element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    ProtectedRegion? region = null;
                    if (item.TryGetProperty("region", out var regEl) && regEl.ValueKind == JsonValueKind.Object)
                    {
                        region = new ProtectedRegion
                        {
                            X = GetDoubleProp(regEl, "x", 0.0),
                            Y = GetDoubleProp(regEl, "y", 0.0),
                            Width = GetDoubleProp(regEl, "width", 0.0),
                            Height = GetDoubleProp(regEl, "height", 0.0),
                            RegionType = GetStringProp(regEl, "regionType"),
                            Description = GetStringProp(regEl, "description"),
                            Importance = GetDoubleProp(regEl, "importance", 1.0)
                        };
                    }

                    var isStructural = true;
                    if (item.TryGetProperty("isStructural", out var isStructProp) &&
                        (isStructProp.ValueKind == JsonValueKind.True || isStructProp.ValueKind == JsonValueKind.False))
                    {
                        isStructural = isStructProp.GetBoolean();
                    }

                    list.Add(new FurnitureComponent
                    {
                        Id = GetStringProp(item, "id"),
                        ParentComponentId = item.TryGetProperty("parentComponentId", out var parent) && parent.ValueKind == JsonValueKind.String ? parent.GetString() : null,
                        Type = GetStringProp(item, "type"),
                        Description = GetStringProp(item, "description"),
                        Color = GetStringProp(item, "color"),
                        Material = GetStringProp(item, "material"),
                        IsStructural = isStructural,
                        Importance = GetStringProp(item, "importance"),
                        Confidence = GetDoubleProp(item, "confidence", 1.0),
                        Region = region
                    });
                }
            }
        }
        return list;
    }

    private static ImageFramingAnalysis? ParseFramingAnalysis(JsonElement element, string propName)
    {
        if (element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.Object)
        {
            var isCropped = GetBoolProp(prop, "isFurnitureCropped");
            var cropSeverityStr = GetStringProp(prop, "cropSeverity");
            var missingContextStr = GetStringProp(prop, "missingContextType");
            var safeZoomOut = GetBoolProp(prop, "safeZoomOutRecommended");
            var confidence = GetDoubleProp(prop, "framingConfidence", 1.0);
            var cropEdges = GetStringList(prop, "cropEdges");

            Enum.TryParse<CropSeverity>(cropSeverityStr, true, out var cropSeverity);
            Enum.TryParse<MissingContextType>(missingContextStr, true, out var missingContext);

            var exp = new RecommendedExpansion();
            if (prop.TryGetProperty("recommendedExpansion", out var expEl) && expEl.ValueKind == JsonValueKind.Object)
            {
                exp = new RecommendedExpansion
                {
                    TopPercent = GetDoubleProp(expEl, "topPercent", 0.0),
                    BottomPercent = GetDoubleProp(expEl, "bottomPercent", 0.0),
                    LeftPercent = GetDoubleProp(expEl, "leftPercent", 0.0),
                    RightPercent = GetDoubleProp(expEl, "rightPercent", 0.0)
                };
            }

            return new ImageFramingAnalysis
            {
                IsFurnitureCropped = isCropped,
                CropSeverity = cropSeverity,
                CropEdges = cropEdges,
                MissingContextType = missingContext,
                SafeZoomOutRecommended = safeZoomOut,
                RecommendedExpansion = exp,
                FramingConfidence = confidence
            };
        }
        return null;
    }

    private static List<SceneObject> ParseSceneObjects(JsonElement element, string propName)
    {
        var list = new List<SceneObject>();
        if (element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    ProtectedRegion? region = null;
                    if (item.TryGetProperty("region", out var regEl) && regEl.ValueKind == JsonValueKind.Object)
                    {
                        region = new ProtectedRegion
                        {
                            X = GetDoubleProp(regEl, "x", 0.0),
                            Y = GetDoubleProp(regEl, "y", 0.0),
                            Width = GetDoubleProp(regEl, "width", 0.0),
                            Height = GetDoubleProp(regEl, "height", 0.0),
                            RegionType = GetStringProp(regEl, "regionType"),
                            Description = GetStringProp(regEl, "description"),
                            Importance = GetDoubleProp(regEl, "importance", 1.0)
                        };
                    }

                    var classStr = GetStringProp(item, "classification");
                    Enum.TryParse<SceneClassification>(classStr, true, out var classification);

                    var precStr = GetStringProp(item, "precedence");
                    Enum.TryParse<RegionPrecedence>(precStr, true, out var precedence);

                    var occStr = GetStringProp(item, "occlusion");
                    Enum.TryParse<OcclusionType>(occStr, true, out var occlusion);

                    var id = GetStringProp(item, "id");
                    var description = GetStringProp(item, "description");
                    var overlapsFurniture = GetBoolProp(item, "overlapsFurniture");
                    var isRemovable = GetBoolProp(item, "isRemovable");
                    var confidence = GetDoubleProp(item, "confidence", 1.0);

                    // Conservative Ownership Resolution:
                    // Any SceneObject must resolve to Uncertain rather than RemovableScene when:
                    // - ownership is unclear
                    // - confidence is insufficient (< 0.85)
                    // - it overlaps furniture
                    // - it may be physically attached to furniture
                    // - it may be an appliance integrated into cabinetry
                    // - it may be hardware
                    // - it may be furniture decoration
                    // - it may occlude an important furniture feature
                    // Low confidence must never resolve to RemovableScene.
                    var textToCheck = (id + " " + description).ToLowerInvariant();
                    bool isPotentialFurnitureFeature = textToCheck.Contains("appliance") || textToCheck.Contains("microwave") ||
                                                       textToCheck.Contains("oven") || textToCheck.Contains("stove") ||
                                                       textToCheck.Contains("range") || textToCheck.Contains("cooktop") ||
                                                       textToCheck.Contains("dishwasher") || textToCheck.Contains("sink") ||
                                                       textToCheck.Contains("faucet") || textToCheck.Contains("tap") ||
                                                       textToCheck.Contains("fridge") || textToCheck.Contains("refrigerator") ||
                                                       textToCheck.Contains("hood") || textToCheck.Contains("handle") ||
                                                       textToCheck.Contains("knob") || textToCheck.Contains("pull") ||
                                                       textToCheck.Contains("hinge") || textToCheck.Contains("hardware") ||
                                                       textToCheck.Contains("trim") || textToCheck.Contains("moulding") ||
                                                       textToCheck.Contains("molding") || textToCheck.Contains("decoration") ||
                                                       textToCheck.Contains("attached") || textToCheck.Contains("integrated") ||
                                                       textToCheck.Contains("built-in");

                    bool isOccludingFurniture = occlusion == OcclusionType.FurniturePartiallyOccluded ||
                                                occlusion == OcclusionType.FurnitureHeavilyOccluded ||
                                                occlusion == OcclusionType.Uncertain;

                    bool isOwnershipUnclear = classification == SceneClassification.Uncertain ||
                                              precedence == RegionPrecedence.Uncertain ||
                                              (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(description));

                    bool mustBeUncertain = confidence < 0.85 ||
                                           overlapsFurniture ||
                                           isOccludingFurniture ||
                                           isPotentialFurnitureFeature ||
                                           isOwnershipUnclear;

                    if (mustBeUncertain)
                    {
                        classification = SceneClassification.Uncertain;
                        precedence = RegionPrecedence.Uncertain;
                        isRemovable = false;
                    }
                    else if (precedence == 0)
                    {
                        precedence = (classification == SceneClassification.RemovableClutter ||
                                      classification == SceneClassification.Person ||
                                      classification == SceneClassification.Reflection)
                            ? RegionPrecedence.RemovableScene
                            : RegionPrecedence.Uncertain;
                    }

                    list.Add(new SceneObject
                    {
                        Id = id,
                        Description = description,
                        Classification = classification,
                        Precedence = precedence,
                        Occlusion = occlusion,
                        Region = region,
                        OverlapsFurniture = overlapsFurniture,
                        IsRemovable = isRemovable,
                        Confidence = confidence
                    });
                }
            }
        }
        return list;
    }

    private static bool GetBoolProp(JsonElement element, string propName)
    {
        if (element.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
            if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var b)) return b;
        }
        return false;
    }
}
