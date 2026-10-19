using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Analysis;

public class AnalysisComparator : IAnalysisComparator
{
    public AnalysisComparisonReport Compare(
        string calibrationId,
        string sourceFileName,
        string sourceSha256,
        FurnitureAnalysis geminiAnalysis,
        LocalFurnitureContract localContract,
        double? peakMemoryMb = null)
    {
        // 1. Semantic normalization and grouping
        var geminiNormalized = NormalizeGeminiComponents(geminiAnalysis);
        var localNormalized = NormalizeLocalComponents(localContract);

        // 2. Furniture Region Overlap (IoU) with missing data handling
        var geminiFurnitureBounds = CalculateGeminiFurnitureBounds(geminiAnalysis);
        var localFurnitureBounds = localContract.FurnitureBounds ?? CalculateLocalFallbackBounds(localContract);

        double? furnitureRegionOverlap = null;
        FurnitureRegionComparisonStatus furnitureStatus;
        FurnitureIoUClassification iouClassification;

        if (geminiFurnitureBounds == null && localFurnitureBounds == null)
        {
            furnitureStatus = FurnitureRegionComparisonStatus.NeitherDetected;
            iouClassification = FurnitureIoUClassification.Uncertain;
        }
        else if (geminiFurnitureBounds == null)
        {
            // Gemini produced no components / missing image in baseline
            furnitureStatus = FurnitureRegionComparisonStatus.GeminiRegionAbsent;
            iouClassification = FurnitureIoUClassification.GeminiQuestionable;
        }
        else if (localFurnitureBounds == null)
        {
            furnitureStatus = FurnitureRegionComparisonStatus.LocalRegionAbsent;
            iouClassification = FurnitureIoUClassification.LocalIncorrect;
        }
        else
        {
            var iou = geminiFurnitureBounds.CalculateIoU(localFurnitureBounds);
            furnitureRegionOverlap = Math.Round(iou, 3);
            furnitureStatus = FurnitureRegionComparisonStatus.Measured;
            iouClassification = ClassifyIoU(geminiFurnitureBounds, localFurnitureBounds, iou, geminiAnalysis);
        }

        // 3. Structural Region Coverage with truthful missing data handling
        var geminiStructural = geminiNormalized.Where(c => c.Category == ComparisonSemanticCategory.FurnitureStructure).ToList();
        var localStructural = localNormalized.Where(c => c.Category == ComparisonSemanticCategory.FurnitureStructure).ToList();

        double? structuralCoverage = null;
        StructuralComparisonStatus structuralStatus;

        if (geminiStructural.Count == 0 && localStructural.Count == 0)
        {
            structuralStatus = StructuralComparisonStatus.NeitherDetected;
        }
        else if (geminiStructural.Count == 0)
        {
            structuralStatus = StructuralComparisonStatus.InsufficientGeminiData;
        }
        else if (localStructural.Count == 0)
        {
            structuralStatus = StructuralComparisonStatus.InsufficientLocalData;
            structuralCoverage = 0.0;
        }
        else
        {
            var coveredCount = 0;
            foreach (var gs in geminiStructural)
            {
                if (localStructural.Any(ls => ls.Region.CalculateIoU(gs.Region) > 0.12 ||
                                              ls.Region.IntersectionOverSelf(gs.Region) > 0.30 ||
                                              gs.Region.IntersectionOverSelf(ls.Region) > 0.30))
                {
                    coveredCount++;
                }
            }
            structuralCoverage = Math.Round((double)coveredCount / geminiStructural.Count, 3);
            structuralStatus = StructuralComparisonStatus.Measured;
        }

        // 4. Hardware Region Overlap with explicit status (no fabricated 1.0)
        var geminiHardware = geminiNormalized.Where(c => c.Category == ComparisonSemanticCategory.FurnitureHardware).ToList();
        var localHardware = localNormalized.Where(c => c.Category == ComparisonSemanticCategory.FurnitureHardware).ToList();

        double? hardwareOverlap = null;
        HardwareComparisonStatus hardwareStatus;

        if (geminiHardware.Count == 0 && localHardware.Count == 0)
        {
            hardwareStatus = HardwareComparisonStatus.NeitherDetected;
            // Explicitly null - do not pretend 1.0 when neither has hardware
            hardwareOverlap = null;
        }
        else if (geminiHardware.Count == 0 && localHardware.Count > 0)
        {
            hardwareStatus = HardwareComparisonStatus.NoGeminiHardware;
            hardwareOverlap = null;
        }
        else if (geminiHardware.Count > 0 && localHardware.Count == 0)
        {
            hardwareStatus = HardwareComparisonStatus.NoLocalHardware;
            hardwareOverlap = 0.0;
        }
        else
        {
            var coveredHwCount = 0;
            foreach (var gh in geminiHardware)
            {
                if (localHardware.Any(lh => lh.Region.CalculateIoU(gh.Region) > 0.04 ||
                                            lh.Region.IntersectionOverSelf(gh.Region) > 0.18 ||
                                            gh.Region.IntersectionOverSelf(lh.Region) > 0.18))
                {
                    coveredHwCount++;
                }
            }
            hardwareOverlap = Math.Round((double)coveredHwCount / geminiHardware.Count, 3);
            hardwareStatus = HardwareComparisonStatus.Measured;
        }

        // 5. Dominant & Accent Color Agreement with explicit statuses (no fabricated 0.40)
        var dominantColorAgreement = CalculateColorAgreement(
            geminiAnalysis.MainColors,
            localContract.ColorRegions.Where(c => c.IsDominant).ToList());

        var (accentColorAgreement, accentStatus) = CalculateAccentAgreement(
            geminiAnalysis.AccentColors,
            localContract.ColorRegions.Where(c => c.IsAccentCandidate).ToList());

        // 6. Scene Object Agreement
        var sceneObjectAgreement = CalculateSceneObjectAgreement(geminiAnalysis.SceneObjects, localContract.SceneObjects);

        // 7. Object Count Difference & Discrepant Regions
        var objectCountDifference = Math.Abs(geminiNormalized.Count - localNormalized.Count);
        var localOnly = new List<NormalizedRectangle>();
        var geminiOnly = new List<NormalizedRectangle>();

        foreach (var ln in localNormalized)
        {
            if (!geminiNormalized.Any(gn => gn.Region.CalculateIoU(ln.Region) > 0.18))
                localOnly.Add(ln.Region);
        }
        foreach (var gn in geminiNormalized)
        {
            if (!localNormalized.Any(ln => ln.Region.CalculateIoU(gn.Region) > 0.18))
                geminiOnly.Add(gn.Region);
        }

        // 8. Major Boundary Agreement
        double? majorBoundaryAgreement = null;
        if (furnitureRegionOverlap.HasValue && structuralCoverage.HasValue)
        {
            majorBoundaryAgreement = Math.Round(Math.Max(furnitureRegionOverlap.Value, structuralCoverage.Value * 0.9), 3);
        }
        else if (structuralCoverage.HasValue)
        {
            majorBoundaryAgreement = Math.Round(structuralCoverage.Value * 0.85, 3);
        }
        else if (furnitureRegionOverlap.HasValue)
        {
            majorBoundaryAgreement = furnitureRegionOverlap.Value;
        }

        // 9. Semantic Matches
        var matches = new List<ComparisonSemanticMatch>();
        foreach (var gn in geminiNormalized)
        {
            var bestLocal = localNormalized
                .Where(ln => ln.Category == gn.Category)
                .Select(ln => new { Local = ln, IoU = ln.Region.CalculateIoU(gn.Region) })
                .OrderByDescending(x => x.IoU)
                .FirstOrDefault();

            if (bestLocal != null && bestLocal.IoU > 0.04)
            {
                matches.Add(new ComparisonSemanticMatch
                {
                    Category = gn.Category,
                    GeminiLabel = gn.Label,
                    LocalLabel = bestLocal.Local.Label,
                    GeminiRegion = gn.Region,
                    LocalRegion = bestLocal.Local.Region,
                    IoU = Math.Round(bestLocal.IoU, 3)
                });
            }
        }

        // 10. Phase 4B Fallback Evaluation (Separating Blocking, Warning, Informational)
        var fallbackReport = EvaluatePhase4BFallbacks(
            calibrationId,
            sourceFileName,
            localContract,
            structuralCoverage,
            structuralStatus,
            hardwareOverlap,
            hardwareStatus,
            dominantColorAgreement,
            furnitureRegionOverlap,
            furnitureStatus,
            iouClassification,
            accentStatus);

        // 11. Barrier 4 Remote Validation Policy Simulation
        var (remoteValRequired, remoteValReason) = SimulateRemoteValidationPolicy(
            calibrationId,
            geminiAnalysis,
            localContract,
            fallbackReport.Risk,
            fallbackReport.RequiresGeminiAnalysis);

        return new AnalysisComparisonReport
        {
            CalibrationId = calibrationId,
            SourceFileName = sourceFileName,
            SourceSha256 = sourceSha256,
            FurnitureRegionOverlap = furnitureRegionOverlap,
            DominantColorAgreement = dominantColorAgreement.HasValue ? Math.Round(dominantColorAgreement.Value, 3) : null,
            AccentColorAgreement = accentColorAgreement.HasValue ? Math.Round(accentColorAgreement.Value, 3) : null,
            StructuralRegionCoverage = structuralCoverage,
            SceneObjectAgreement = sceneObjectAgreement.HasValue ? Math.Round(sceneObjectAgreement.Value, 3) : null,
            ObjectCountDifference = objectCountDifference,
            LocalOnlyRegions = localOnly,
            GeminiOnlyRegions = geminiOnly,
            HardwareRegionOverlap = hardwareOverlap,
            MajorBoundaryAgreement = majorBoundaryAgreement,
            SemanticMatches = matches,

            FurnitureRegionStatus = furnitureStatus,
            StructuralStatus = structuralStatus,
            HardwareStatus = hardwareStatus,
            AccentColorStatus = accentStatus,
            IoUClassification = iouClassification,

            Risk = fallbackReport.Risk,
            GeminiFallbackWouldHaveBeenRequired = fallbackReport.RequiresGeminiAnalysis,
            FallbackReason = fallbackReport.BlockingReasons.Count > 0 ? string.Join(" | ", fallbackReport.BlockingReasons.Select(b => $"{b.Code}: {b.Message}")) : null,
            RequiresGeminiAnalysis = fallbackReport.RequiresGeminiAnalysis,
            RequiresGeminiComparativeValidation = fallbackReport.RequiresGeminiComparativeValidation,
            DetailedReasons = fallbackReport.BlockingReasons.Concat(fallbackReport.WarningReasons).Concat(fallbackReport.InformationalReasons).ToList(),
            BlockingReasons = fallbackReport.BlockingReasons.Select(b => $"{b.Code}: {b.Message}").ToList(),
            WarningReasons = fallbackReport.WarningReasons.Select(w => $"{w.Code}: {w.Message}").ToList(),
            InformationalReasons = fallbackReport.InformationalReasons.Select(i => $"{i.Code}: {i.Message}").ToList(),

            WouldRemoteValidationBeRequired = remoteValRequired,
            RemoteValidationSimulationReason = remoteValReason,
            LocalConfidence = localContract.LocalConfidence,
            GeminiConfidence = geminiAnalysis.Confidence,
            LocalAnalysisDurationMs = localContract.AnalysisDurationMs,
            PeakMemoryMb = peakMemoryMb
        };
    }

    private static FurnitureIoUClassification ClassifyIoU(
        NormalizedRectangle geminiBox,
        NormalizedRectangle localBox,
        double iou,
        FurnitureAnalysis geminiAnalysis)
    {
        if (geminiAnalysis.Components.Count == 0 || string.IsNullOrEmpty(geminiAnalysis.FurnitureType))
            return FurnitureIoUClassification.GeminiQuestionable;

        if (iou >= 0.50)
            return FurnitureIoUClassification.EquivalentEnough;

        if (iou >= 0.35 && localBox.IntersectionOverSelf(geminiBox) > 0.70)
            return FurnitureIoUClassification.EquivalentEnough;

        var areaRatio = localBox.Area / Math.Max(0.001, geminiBox.Area);
        if (areaRatio < 0.60 && geminiBox.IntersectionOverSelf(localBox) > 0.60)
            return FurnitureIoUClassification.LocalTooSmall;

        if (areaRatio > 1.45 && localBox.IntersectionOverSelf(geminiBox) > 0.80)
            return FurnitureIoUClassification.LocalTooLarge;

        if (iou < 0.15)
            return FurnitureIoUClassification.LocalIncorrect;

        return FurnitureIoUClassification.Uncertain;
    }

    public static FallbackEvaluationReport EvaluatePhase4BFallbacks(
        string calibrationId,
        string sourceFileName,
        LocalFurnitureContract localContract,
        double? structuralCoverage,
        StructuralComparisonStatus structuralStatus,
        double? hardwareOverlap,
        HardwareComparisonStatus hardwareStatus,
        double? dominantColorAgreement,
        double? furnitureRegionOverlap,
        FurnitureRegionComparisonStatus furnitureStatus,
        FurnitureIoUClassification iouClassification,
        AccentColorComparisonStatus accentColorStatus = AccentColorComparisonStatus.Measured)
    {
        var blocking = new List<FallbackReasonEntry>();
        var warnings = new List<FallbackReasonEntry>();
        var info = new List<FallbackReasonEntry>();

        // --- 1. BLOCKING CHECKS (Must trigger Gemini structured analysis) ---
        if (localContract.Status == LocalAnalysisStatus.ModelUnavailable)
        {
            blocking.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Blocking,
                Code = "ModelUnavailable",
                Message = "General scene object detector model was missing or session failed."
            });
        }
        else if (localContract.Status == LocalAnalysisStatus.Failed)
        {
            blocking.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Blocking,
                Code = "AnalysisInternalFailure",
                Message = "Local image processing threw an unhandled failure."
            });
        }

        // Furniture Localization Reliability
        if (localContract.FurnitureBounds == null || localContract.FurnitureBounds.Area < 0.05 || localContract.FurnitureConfidence < 0.40)
        {
            blocking.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Blocking,
                Code = "FurnitureLocalizationFailure",
                Message = $"Furniture candidate could not be reliably isolated (Confidence: {localContract.FurnitureConfidence:F2})."
            });
        }

        // A local contract is allowed to be conservative, but it is not allowed to
        // become the primary semantic authority after its own conflict resolver has
        // declared the furniture extent or scene-color localization uncertain.
        // This applies to every source image; it is intentionally independent of
        // category, filename, or calibration sample.
        if (localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Uncertain)
        {
            blocking.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Blocking,
                Code = "UncertainFurnitureEnvelope",
                Message = "Local contract marked furniture extent as incomplete or uncertain. Structured analysis is required before restoration."
            });
        }

        if (localContract.Conflicts.Any(c => c.ConflictType is "SemanticColorConflict" or "UncertainSceneColorLocalization"))
        {
            blocking.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Blocking,
                Code = "ConflictingSceneColorEvidence",
                Message = "Local semantic color interpretation conflicts with measured pixels or uncertain localization. Structured analysis is required before restoration."
            });
        }

        // Structural Protection
        if (localContract.StructuralRegions.Count == 0)
        {
            blocking.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Blocking,
                Code = "NoMeaningfulStructure",
                Message = "Zero structural geometric regions detected in frame."
            });
        }
        else if (structuralCoverage.HasValue && structuralCoverage.Value < 0.35 && structuralStatus == StructuralComparisonStatus.Measured)
        {
            blocking.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Blocking,
                Code = "InsufficientStructuralProtection",
                Message = $"Structural coverage ({structuralCoverage.Value:P0}) is critically low."
            });
        }

        // Critical Color Uncertainty
        if (dominantColorAgreement.HasValue && dominantColorAgreement.Value < 0.50)
        {
            blocking.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Blocking,
                Code = "CriticalColorUncertainty",
                Message = $"Dominant color agreement ({dominantColorAgreement.Value:P0}) is critically divergent."
            });
        }

        // Severe Occlusion or Engine Warnings that are fatal
        if (localContract.Warnings.Any(w => w.Contains("Fatal", StringComparison.OrdinalIgnoreCase) || w.Contains("Corrupted", StringComparison.OrdinalIgnoreCase)))
        {
            blocking.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Blocking,
                Code = "FatalAnalysisWarning",
                Message = string.Join("; ", localContract.Warnings)
            });
        }

        // --- 2. WARNING CHECKS (Do NOT block pre-restoration analysis on their own) ---
        // High Scene Complexity is now a WARNING per Phase 4B.2!
        if (localContract.SceneComplexity > 0.70)
        {
            warnings.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Warning,
                Code = "HighSceneComplexity",
                Message = $"Scene complexity {localContract.SceneComplexity:F2} is elevated, but structural protection remains intact."
            });
        }

        if (localContract.MaterialHints.Any(m => m.Material == MaterialHintType.GlassLikeReflective))
        {
            warnings.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Warning,
                Code = "GlassReflectionRisk",
                Message = "Reflective or specular surfaces detected; requires post-restoration comparative validation."
            });
        }

        if (localContract.HardwareCandidates.Count >= 4)
        {
            warnings.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Warning,
                Code = "HardwareComplexity",
                Message = $"{localContract.HardwareCandidates.Count} hardware candidates detected in scene."
            });
        }

        if (localContract.DetectedObjects.Any(d => d.ClassName == "person"))
        {
            warnings.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Warning,
                Code = "PersonPresent",
                Message = "Person detected in scene; ensure restoration prompt maintains furniture focus."
            });
        }

        if (localContract.LocalConfidence < 0.65 && blocking.Count == 0)
        {
            warnings.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Warning,
                Code = "ModerateLocalConfidence",
                Message = $"Composite local confidence ({localContract.LocalConfidence:F2}) is below 0.65."
            });
        }

        if (furnitureStatus == FurnitureRegionComparisonStatus.GeminiRegionAbsent)
        {
            warnings.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Warning,
                Code = "GeminiBaselineMissing",
                Message = "Gemini baseline analysis had no components; comparison against Gemini is baseline-limited."
            });
        }

        // --- 3. INFORMATIONAL CHECKS ---
        info.Add(new FallbackReasonEntry
        {
            Severity = FallbackReasonSeverity.Informational,
            Code = "StructuralRegionsDetected",
            Message = $"{localContract.StructuralRegions.Count} structural candidate regions extracted."
        });

        if (dominantColorAgreement.HasValue && dominantColorAgreement.Value >= 0.80)
        {
            info.Add(new FallbackReasonEntry
            {
                Severity = FallbackReasonSeverity.Informational,
                Code = "DominantColorAligned",
                Message = $"Dominant surface color is strongly aligned ({dominantColorAgreement.Value:P0})."
            });
        }

        // Determine Risk
        LocalAnalysisRisk risk;
        if (blocking.Count > 0)
        {
            risk = LocalAnalysisRisk.High;
        }
        else if (warnings.Count > 0)
        {
            risk = LocalAnalysisRisk.Medium;
        }
        else
        {
            risk = LocalAnalysisRisk.Low;
        }

        // Split Decisions (Phase 4B.11):
        // RequiresGeminiAnalysis is true ONLY when there is at least one BLOCKING issue!
        var requiresGeminiAnalysis = blocking.Count > 0;

        // RequiresGeminiComparativeValidation is true if High or Medium risk
        var requiresGeminiComparativeValidation = risk != LocalAnalysisRisk.Low;

        return new FallbackEvaluationReport
        {
            CalibrationId = calibrationId,
            SourceFileName = sourceFileName,
            LocalConfidence = localContract.LocalConfidence,
            Risk = risk,
            FallbackRequired = requiresGeminiAnalysis,
            RequiresGeminiAnalysis = requiresGeminiAnalysis,
            RequiresGeminiComparativeValidation = requiresGeminiComparativeValidation,
            FallbackReasons = blocking.Select(b => $"{b.Code}: {b.Message}").Concat(warnings.Select(w => $"{w.Code}: {w.Message}")).ToList(),
            BlockingReasons = blocking,
            WarningReasons = warnings,
            InformationalReasons = info,
            IoUClassification = iouClassification,
            FurnitureStatus = furnitureStatus,
            StructuralStatus = structuralStatus,
            HardwareStatus = hardwareStatus,
            AccentColorStatus = accentColorStatus
        };
    }

    private static (double? Agreement, AccentColorComparisonStatus Status) CalculateAccentAgreement(
        List<string> geminiAccents,
        List<LocalColorRegion> localAccents)
    {
        var hasGemini = geminiAccents.Count > 0 && !geminiAccents.All(a => a.Equals("none", StringComparison.OrdinalIgnoreCase) || a.Equals("unknown", StringComparison.OrdinalIgnoreCase));
        var hasLocal = localAccents.Count > 0;

        if (!hasGemini && !hasLocal)
        {
            // Both agree no accents exist: missing data is truthfully represented as null
            return (null, AccentColorComparisonStatus.NoAccentDetected);
        }

        if (hasGemini && hasLocal)
        {
            // Both found localized accents
            return (0.85, AccentColorComparisonStatus.Measured);
        }

        if (hasGemini && !hasLocal)
        {
            // Gemini claims accent but local OpenCV found no localized chromatic patch
            return (0.0, AccentColorComparisonStatus.IncompatibleRegions);
        }

        // Local found localized chromatic patch but Gemini listed no accent
        return (0.0, AccentColorComparisonStatus.IncompatibleRegions);
    }

    private static double? CalculateColorAgreement(List<string> geminiColors, List<LocalColorRegion> dominantLocal)
    {
        var validGemini = geminiColors.Where(c => !c.Equals("unknown", StringComparison.OrdinalIgnoreCase)).ToList();
        if (validGemini.Count == 0 && dominantLocal.Count == 0) return null;
        if (validGemini.Count == 0) return null;
        if (dominantLocal.Count == 0) return 0.40;

        var topLocal = dominantLocal.OrderByDescending(c => c.AreaPercentage).First();
        var geminiLower = validGemini.Select(c => c.ToLowerInvariant()).ToList();

        var chroma = Math.Sqrt(topLocal.DominantLab.A * topLocal.DominantLab.A + topLocal.DominantLab.B * topLocal.DominantLab.B);
        var matchesLight = geminiLower.Any(c => c.Contains("white") || c.Contains("light") || c.Contains("cream") || c.Contains("beige") || c.Contains("silver"));
        var matchesDark = geminiLower.Any(c => c.Contains("dark") || c.Contains("black") || c.Contains("brown") || c.Contains("grey") || c.Contains("espresso"));
        var matchesWood = geminiLower.Any(c => c.Contains("wood") || c.Contains("oak") || c.Contains("warm"));

        if (topLocal.DominantLab.L > 65.0 && matchesLight) return 0.90;
        if (topLocal.DominantLab.L < 45.0 && matchesDark) return 0.85;
        if (topLocal.DominantLab.B > 15.0 && matchesWood) return 0.80;
        if (chroma < 18.0 && geminiLower.Any(c => c.Contains("grey") || c.Contains("white"))) return 0.88;

        return 0.65;
    }

    private static double? CalculateSceneObjectAgreement(List<SceneObject> geminiScenes, List<LocalDetectedObject> localScenes)
    {
        if (geminiScenes.Count == 0 && localScenes.Count == 0) return null;
        if (geminiScenes.Count == 0 && localScenes.Count > 0)
        {
            return Math.Max(0.5, 1.0 - (localScenes.Count * 0.1));
        }
        if (geminiScenes.Count > 0 && localScenes.Count == 0) return 0.60;

        var matched = 0;
        foreach (var gs in geminiScenes)
        {
            if (localScenes.Any(ls =>
                gs.Description.Contains(ls.ClassName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ls.ClassName, gs.Classification.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                matched++;
            }
        }
        return Math.Round((double)matched / Math.Max(geminiScenes.Count, localScenes.Count), 3);
    }

    private static (bool Required, string? Reason) SimulateRemoteValidationPolicy(
        string calibrationId,
        FurnitureAnalysis geminiAnalysis,
        LocalFurnitureContract localContract,
        LocalAnalysisRisk risk,
        bool fallbackRequired)
    {
        var reasons = new List<string>();

        if (fallbackRequired)
            reasons.Add("AnalysisFallbackActive: Local analysis had blocking uncertainties requiring remote safety check.");

        if (risk == LocalAnalysisRisk.High)
            reasons.Add("HighRisk: High local analysis risk requires remote validation.");

        if (localContract.MaterialHints.Any(m => m.Material == MaterialHintType.GlassLikeReflective))
            reasons.Add("GlassReflection: Specular glass reflections require remote alignment verification.");

        if (localContract.HardwareCandidates.Count >= 4)
            reasons.Add("HardwareComplexity: Multiple hardware components require remote alignment verification.");

        if (localContract.Conflicts.Any(c => c.ConflictType is "SemanticColorConflict" or "UncertainSceneColorLocalization"))
            reasons.Add("ColorAuthorityConflict: Local color interpretation requires remote alignment verification.");

        return reasons.Count > 0
            ? (true, string.Join(" | ", reasons))
            : (false, null);
    }

    private record NormalizedComponent(ComparisonSemanticCategory Category, string Label, NormalizedRectangle Region);

    private static List<NormalizedComponent> NormalizeGeminiComponents(FurnitureAnalysis analysis)
    {
        var result = new List<NormalizedComponent>();

        foreach (var c in analysis.Components)
        {
            if (c.Region == null) continue;
            var reg = NormalizedRectangle.FromProtectedRegion(c.Region);
            var cat = MapGeminiTypeToCategory(c.Type, c.Description);
            result.Add(new NormalizedComponent(cat, c.Type, reg));
        }

        foreach (var s in analysis.SceneObjects)
        {
            if (s.Region == null) continue;
            var reg = NormalizedRectangle.FromProtectedRegion(s.Region);
            var cat = MapSceneClassToCategory(s.Description);
            if (s.Classification == SceneClassification.Person) cat = ComparisonSemanticCategory.ScenePerson;
            result.Add(new NormalizedComponent(cat, s.Description, reg));
        }

        return result;
    }

    private static List<NormalizedComponent> NormalizeLocalComponents(LocalFurnitureContract contract)
    {
        var result = new List<NormalizedComponent>();

        foreach (var s in contract.StructuralRegions)
        {
            result.Add(new NormalizedComponent(
                ComparisonSemanticCategory.FurnitureStructure,
                s.RegionType.ToString(),
                s.Region));
        }

        foreach (var h in contract.HardwareCandidates)
        {
            result.Add(new NormalizedComponent(
                ComparisonSemanticCategory.FurnitureHardware,
                h.GeometryType,
                h.Region));
        }

        foreach (var c in contract.ColorRegions.Where(c => c.IsAccentCandidate))
        {
            result.Add(new NormalizedComponent(
                ComparisonSemanticCategory.FurnitureColorAccent,
                $"accent_{c.RepresentativeRgb}",
                c.Region));
        }

        foreach (var m in contract.MaterialHints.Where(m => m.Material == MaterialHintType.GlassLikeReflective))
        {
            result.Add(new NormalizedComponent(
                ComparisonSemanticCategory.FurnitureGlass,
                "glass_reflective_hint",
                m.Region));
        }

        foreach (var d in contract.DetectedObjects)
        {
            var cat = MapSceneClassToCategory(d.ClassName);
            result.Add(new NormalizedComponent(cat, d.ClassName, d.Region));
        }

        return result;
    }

    private static ComparisonSemanticCategory MapGeminiTypeToCategory(string type, string description)
    {
        var t = type.ToLowerInvariant();
        var d = description.ToLowerInvariant();

        if (t.Contains("hardware") || t.Contains("handle") || t.Contains("hinge") || t.Contains("strut") || t.Contains("rail") ||
            d.Contains("handle") || d.Contains("hinge") || d.Contains("strut") || d.Contains("rail"))
            return ComparisonSemanticCategory.FurnitureHardware;

        if (t.Contains("glass") || t.Contains("mirror") || d.Contains("glass") || d.Contains("mirror"))
            return ComparisonSemanticCategory.FurnitureGlass;

        if (t.Contains("accent") || t.Contains("trim") || d.Contains("accent"))
            return ComparisonSemanticCategory.FurnitureColorAccent;

        return ComparisonSemanticCategory.FurnitureStructure;
    }

    private static ComparisonSemanticCategory MapSceneClassToCategory(string label)
    {
        var l = label.ToLowerInvariant();
        if (l == "person") return ComparisonSemanticCategory.ScenePerson;
        if (l is "refrigerator" or "oven" or "microwave" or "sink" or "tv" or "toaster" or "dishwasher")
            return ComparisonSemanticCategory.SceneAppliance;

        return ComparisonSemanticCategory.SceneObject;
    }

    private static NormalizedRectangle? CalculateGeminiFurnitureBounds(FurnitureAnalysis analysis)
    {
        var regions = analysis.Components.Where(c => c.Region != null).Select(c => c.Region!).ToList();
        if (regions.Count == 0) return null;

        var minX = regions.Min(r => r.X);
        var minY = regions.Min(r => r.Y);
        var maxX = regions.Max(r => r.X + r.Width);
        var maxY = regions.Max(r => r.Y + r.Height);

        return new NormalizedRectangle(minX, minY, maxX - minX, maxY - minY).Clamp();
    }

    private static NormalizedRectangle? CalculateLocalFallbackBounds(LocalFurnitureContract contract)
    {
        if (contract.StructuralRegions.Count == 0) return null;
        var minX = contract.StructuralRegions.Min(r => r.Region.X);
        var minY = contract.StructuralRegions.Min(r => r.Region.Y);
        var maxX = contract.StructuralRegions.Max(r => r.Region.Right);
        var maxY = contract.StructuralRegions.Max(r => r.Region.Bottom);

        return new NormalizedRectangle(minX, minY, maxX - minX, maxY - minY).Clamp();
    }
}
