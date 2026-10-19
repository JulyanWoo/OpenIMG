using System.Text.Json;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Parsing;

public static class Phase4C1ComparativeValidationParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Phase4C1ComparativeValidationReport Parse(string rawResponse, string calibrationId = "")
    {
        var cleaned = FurnitureAnalysisParser.CleanJsonContent(rawResponse);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return new Phase4C1ComparativeValidationReport
            {
                CalibrationId = calibrationId,
                RawGeminiResponse = rawResponse,
                CandidateA = new CandidateEvaluation
                {
                    Decision = "UNCERTAIN",
                    Notes = "Empty response received from Gemini comparative validator."
                },
                CandidateB = new CandidateEvaluation
                {
                    Decision = "UNCERTAIN",
                    Notes = "Empty response received from Gemini comparative validator."
                },
                Comparison = new ComparisonSummary
                {
                    DifferenceDescription = "Unparseable response."
                }
            };
        }

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var candA = root.TryGetProperty("CandidateA", out var aElem)
                ? ParseCandidate(aElem)
                : (root.TryGetProperty("candidateA", out var aElemLower) ? ParseCandidate(aElemLower) : new CandidateEvaluation());

            var candB = root.TryGetProperty("CandidateB", out var bElem)
                ? ParseCandidate(bElem)
                : (root.TryGetProperty("candidateB", out var bElemLower) ? ParseCandidate(bElemLower) : new CandidateEvaluation());

            var comparison = root.TryGetProperty("Comparison", out var cElem)
                ? ParseComparison(cElem)
                : (root.TryGetProperty("comparison", out var cElemLower) ? ParseComparison(cElemLower) : new ComparisonSummary());

            return new Phase4C1ComparativeValidationReport
            {
                CalibrationId = calibrationId,
                CandidateA = candA,
                CandidateB = candB,
                Comparison = comparison,
                RawGeminiResponse = rawResponse
            };
        }
        catch (Exception ex)
        {
            return new Phase4C1ComparativeValidationReport
            {
                CalibrationId = calibrationId,
                RawGeminiResponse = rawResponse,
                CandidateA = new CandidateEvaluation
                {
                    Decision = "UNCERTAIN",
                    Notes = $"Parsing exception: {ex.Message}"
                },
                CandidateB = new CandidateEvaluation
                {
                    Decision = "UNCERTAIN",
                    Notes = $"Parsing exception: {ex.Message}"
                },
                Comparison = new ComparisonSummary
                {
                    DifferenceDescription = $"Parsing error: {ex.Message}"
                }
            };
        }
    }

    private static CandidateEvaluation ParseCandidate(JsonElement elem)
    {
        return new CandidateEvaluation
        {
            FurnitureDecision = GetString(elem, "FurnitureDecision", "furnitureDecision", "ACCEPTED"),
            GeometryPreservation = GetString(elem, "GeometryPreservation", "geometryPreservation", "EXCELLENT"),
            ComponentPreservation = GetString(elem, "ComponentPreservation", "componentPreservation", "EXCELLENT"),
            ColorIdentityPreservation = GetString(elem, "ColorIdentityPreservation", "colorIdentityPreservation", "EXCELLENT"),
            AccentColorLocationPreservation = GetString(elem, "AccentColorLocationPreservation", "accentColorLocationPreservation", "EXCELLENT"),
            MaterialPreservation = GetString(elem, "MaterialPreservation", "materialPreservation", "EXCELLENT"),
            HardwarePreservation = GetString(elem, "HardwarePreservation", "hardwarePreservation", "EXCELLENT"),
            HardwareCountPreservation = GetString(elem, "HardwareCountPreservation", "hardwareCountPreservation", "EXCELLENT"),
            HardwareLocationPreservation = GetString(elem, "HardwareLocationPreservation", "hardwareLocationPreservation", "EXCELLENT"),
            AsymmetryPreservation = GetString(elem, "AsymmetryPreservation", "asymmetryPreservation", "EXCELLENT"),
            FurnitureColorIdentity = GetString(elem, "FurnitureColorIdentity", "furnitureColorIdentity", "EXCELLENT"),
            SceneColorIdentity = GetString(elem, "SceneColorIdentity", "sceneColorIdentity", "EXCELLENT"),
            ScenePreservation = GetString(elem, "ScenePreservation", "scenePreservation", "EXCELLENT"),
            FramingPreservation = GetString(elem, "FramingPreservation", "framingPreservation", "EXCELLENT"),
            PersistentSceneDetailPreservation = GetString(elem, "PersistentSceneDetailPreservation", "persistentSceneDetailPreservation", "EXCELLENT"),
            NegativeSpacePreservation = GetString(elem, "NegativeSpacePreservation", "negativeSpacePreservation", "EXCELLENT"),

            PhotographicQualityImprovement = GetString(elem, "PhotographicQualityImprovement", "photographicQualityImprovement", "STRONG"),
            SharpnessImprovement = GetString(elem, "SharpnessImprovement", "sharpnessImprovement", "GOOD"),
            ExposureImprovement = GetString(elem, "ExposureImprovement", "exposureImprovement", "GOOD"),
            WhiteBalanceImprovement = GetString(elem, "WhiteBalanceImprovement", "whiteBalanceImprovement", "GOOD"),
            NoiseReduction = GetString(elem, "NoiseReduction", "noiseReduction", "GOOD"),
            ColorImprovement = GetString(elem, "ColorImprovement", "colorImprovement", "GOOD"),

            PerspectiveCorrectionApplied = GetBool(elem, "PerspectiveCorrectionApplied", "perspectiveCorrectionApplied", false),
            PerspectiveCorrectionSafe = GetBool(elem, "PerspectiveCorrectionSafe", "perspectiveCorrectionSafe", true),
            UnexpectedAddedFurniture = GetStringList(elem, "UnexpectedAddedFurniture", "unexpectedAddedFurniture"),
            UnexpectedAddedHardware = GetStringList(elem, "UnexpectedAddedHardware", "unexpectedAddedHardware"),
            UnexpectedAddedDetails = GetStringList(elem, "UnexpectedAddedDetails", "unexpectedAddedDetails"),
            UnexpectedRemovedDetails = GetStringList(elem, "UnexpectedRemovedDetails", "unexpectedRemovedDetails"),
            OverprocessingDetected = GetBool(elem, "OverprocessingDetected", "overprocessingDetected", false),
            Decision = GetString(elem, "Decision", "decision", "ACCEPTED"),
            Notes = GetString(elem, "Notes", "notes", string.Empty)
        };
    }

    private static ComparisonSummary ParseComparison(JsonElement elem)
    {
        return new ComparisonSummary
        {
            MeaningfulFidelityDifferenceDetected = GetBool(elem, "MeaningfulFidelityDifferenceDetected", "meaningfulFidelityDifferenceDetected", false),
            DifferenceDescription = GetString(elem, "DifferenceDescription", "differenceDescription", string.Empty)
        };
    }

    private static string GetString(JsonElement elem, string prop1, string prop2, string fallback)
    {
        if (elem.TryGetProperty(prop1, out var p1) && p1.ValueKind == JsonValueKind.String)
            return p1.GetString() ?? fallback;
        if (elem.TryGetProperty(prop2, out var p2) && p2.ValueKind == JsonValueKind.String)
            return p2.GetString() ?? fallback;
        return fallback;
    }

    private static bool GetBool(JsonElement elem, string prop1, string prop2, bool fallback)
    {
        if (elem.TryGetProperty(prop1, out var p1) && (p1.ValueKind == JsonValueKind.True || p1.ValueKind == JsonValueKind.False))
            return p1.GetBoolean();
        if (elem.TryGetProperty(prop2, out var p2) && (p2.ValueKind == JsonValueKind.True || p2.ValueKind == JsonValueKind.False))
            return p2.GetBoolean();
        return fallback;
    }

    private static List<string> GetStringList(JsonElement elem, string prop1, string prop2)
    {
        var list = new List<string>();
        JsonElement target;
        if (!elem.TryGetProperty(prop1, out target) && !elem.TryGetProperty(prop2, out target))
            return list;

        if (target.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in target.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
        }
        return list;
    }
}
