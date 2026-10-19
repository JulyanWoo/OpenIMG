using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Analysis;

public interface ILocalContractConflictDetector
{
    LocalContractConflictReport DetectAndResolveConflicts(
        LocalFurnitureContract contract,
        FurnitureAnalysis? adaptedAnalysis = null);
}

public record LocalContractConflictReport
{
    public List<ContractConflict> Conflicts { get; init; } = new();
    public List<ContractAuthorityRecord> AuthorityRecords { get; init; } = new();
    /// <summary>
    /// Color references after the same authority resolution represented by <see cref="AuthorityRecords"/>.
    /// Consumers must use these resolved references rather than the raw sampler output.
    /// </summary>
    public List<SceneColorReference> ResolvedSceneColorReferences { get; init; } = new();
    public ConstraintAuthority EnvelopeAuthority { get; init; } = ConstraintAuthority.Strict;
    public bool HasLowCoverageEnvelope { get; init; }
    public bool HasColorConflict { get; init; }
    public bool HasClutterCandidate { get; init; }
}

public class LocalContractConflictDetector : ILocalContractConflictDetector
{
    public const string CurrentAuthorityPolicyVersion = "contract-authority-v2";
    private static readonly HashSet<string> PermanentFixtureClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "switch", "outlet", "plug", "countertop", "counter", "sink", "faucet", "tap",
        "handle", "hinge", "knob", "pull", "cabinet", "drawer", "door", "shelf",
        "wall", "floor", "ceiling", "window", "mirror", "backsplash", "cooktop", "stove"
    };

    private static readonly HashSet<string> RemovableClutterClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "bottle", "cup", "wine glass", "bowl", "plate", "bag", "handbag", "backpack",
        "suitcase", "stool", "sack", "box", "package", "packaging", "container",
        "rag", "cloth", "towel", "tool", "sponge", "debris", "can", "pan", "pot"
    };

    public LocalContractConflictReport DetectAndResolveConflicts(
        LocalFurnitureContract contract,
        FurnitureAnalysis? adaptedAnalysis = null)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var conflicts = new List<ContractConflict>();
        var authorityRecords = new List<ContractAuthorityRecord>();
        var hasLowCoverage = false;
        var hasColorConflict = false;
        var hasClutterCandidate = false;
        var resolvedSceneColors = new List<SceneColorReference>();

        // ---------------------------------------------------------
        // 1. Furniture Envelope Coverage & Authority Validation
        // ---------------------------------------------------------
        var envelopeAuthority = ConstraintAuthority.Strict;
        var bounds = contract.FurnitureBounds;

        if (bounds == null)
        {
            envelopeAuthority = ConstraintAuthority.Uncertain;
            authorityRecords.Add(new ContractAuthorityRecord
            {
                ConstraintId = "furniture_envelope",
                Type = "FurnitureEnvelope",
                Confidence = 0.20,
                Authority = ConstraintAuthority.Uncertain,
                SourceEvidence = "No distinct furniture boundary could be isolated.",
                Reason = "Furniture boundary absent or unresolvable.",
                ConflictDetected = false,
                PromptStatement = "Do not infer furniture outside clearly observed structural regions; use the entire original photograph as visual truth."
            });
        }
        else
        {
            // Evaluate coverage against all observed structural regions
            double totalSpanX = 0;
            if (contract.StructuralRegions.Count > 0)
            {
                var minX = contract.StructuralRegions.Min(r => r.Region.X);
                var maxX = contract.StructuralRegions.Max(r => r.Region.Right);
                totalSpanX = maxX - minX;
            }

            // Low coverage check: if bounds capture only a suspiciously small fraction of width/area
            var isLowCoverage = bounds.Width < 0.35 ||
                                bounds.Area < 0.22 ||
                                (bounds.X > 0.65 && bounds.Width < 0.35) || // pushed to far right
                                (bounds.Right < 0.35 && bounds.Width < 0.35) || // pushed to far left
                                (totalSpanX > 0.60 && bounds.Width < totalSpanX * 0.60) ||
                                contract.FurnitureConfidence < 0.60;

            if (isLowCoverage)
            {
                hasLowCoverage = true;
                envelopeAuthority = ConstraintAuthority.Uncertain;
                var conflict = new ContractConflict
                {
                    ConflictId = "conflict_envelope_low_coverage",
                    ConflictType = "LowCoverageFurnitureEnvelope",
                    Description = $"Proposed furniture envelope [{bounds.X:F2}, {bounds.Y:F2}, {bounds.Width:F2}, {bounds.Height:F2}] captures suspiciously small fraction of scene (Width={bounds.Width:F2}, Area={bounds.Area:F2}, Conf={contract.FurnitureConfidence:F2}).",
                    AffectedConstraintIds = new List<string> { "furniture_envelope" },
                    ResolutionApplied = "Downgraded envelope authority to Uncertain. Forbidden strict envelope assertion. Gemini must use the entire source image as visual truth."
                };
                conflicts.Add(conflict);

                authorityRecords.Add(new ContractAuthorityRecord
                {
                    ConstraintId = "furniture_envelope",
                    Type = "FurnitureEnvelope",
                    Confidence = contract.FurnitureConfidence,
                    Authority = ConstraintAuthority.Uncertain,
                    SourceEvidence = $"Low coverage detection: Bounds=[{bounds.X:F2}, {bounds.Y:F2}, {bounds.Width:F2}, {bounds.Height:F2}], Conf={contract.FurnitureConfidence:F2}",
                    Reason = "Low coverage envelope cannot become Strict.",
                    ConflictDetected = true,
                    ConflictReason = conflict.Description,
                    PromptStatement = "Do not infer furniture outside clearly observed structural regions; use the entire original photograph as visual truth."
                });
            }
            else
            {
                var conf = contract.FurnitureConfidence >= 0.75 ? ConstraintAuthority.Strict : ConstraintAuthority.Advisory;
                authorityRecords.Add(new ContractAuthorityRecord
                {
                    ConstraintId = "furniture_envelope",
                    Type = "FurnitureEnvelope",
                    Confidence = contract.FurnitureConfidence,
                    Authority = conf,
                    SourceEvidence = $"Verified coherent envelope [{bounds.X:F2}, {bounds.Y:F2}, {bounds.Width:F2}, {bounds.Height:F2}] with confidence {contract.FurnitureConfidence:F2}.",
                    Reason = conf == ConstraintAuthority.Strict ? "High-confidence structural bounding." : "Moderate-confidence structural bounding.",
                    ConflictDetected = false,
                    PromptStatement = $"Preserve detected furniture outer boundary at [{bounds.X:F2}, {bounds.Y:F2}, {bounds.Width:F2}, {bounds.Height:F2}]."
                });
            }
        }

        // ---------------------------------------------------------
        // 2. Scene Color vs Measured CIELAB & Hue Validation
        // ---------------------------------------------------------
        foreach (var sc in contract.SceneColorReferences)
        {
            var isGreenMeasured = sc.ReferenceLab.A < -1.8 || (sc.HueDegrees >= 105.0 && sc.HueDegrees <= 175.0 && sc.ReferenceLab.A <= 0);
            var isVeryLowChroma = sc.Chroma < 4.5;
            var isHighVariance = sc.RegionVariance > 18.0;

            // Check if sampling region overlaps furniture bounds
            var overlapsFurniture = bounds != null && bounds.CalculateIoU(new NormalizedRectangle(sc.Region.X, sc.Region.Y, sc.Region.Width, sc.Region.Height)) > 0.10;

            // Semantic label contradiction check: e.g. "warm_beige_taupe" but measured is green or very low chroma
            var isSemanticWarm = sc.HueFamily.Contains("warm", StringComparison.OrdinalIgnoreCase) ||
                                 sc.HueFamily.Contains("beige", StringComparison.OrdinalIgnoreCase) ||
                                 sc.HueFamily.Contains("amber", StringComparison.OrdinalIgnoreCase);

            var localizationIsUncertain = envelopeAuthority == ConstraintAuthority.Uncertain;
            var isColorContradiction = (isSemanticWarm && isGreenMeasured) ||
                                       (isSemanticWarm && isVeryLowChroma) ||
                                       overlapsFurniture ||
                                       isHighVariance;

            if (isColorContradiction || localizationIsUncertain)
            {
                hasColorConflict = true;
                var reason = isSemanticWarm && isGreenMeasured
                    ? $"Semantic label '{sc.HueFamily}' conflicts with measured green spectrum (a*={sc.ReferenceLab.A:F1}, Hue={sc.HueDegrees:F1}°)."
                    : overlapsFurniture
                        ? $"Sample region [{sc.Region.X:F2},{sc.Region.Y:F2},{sc.Region.Width:F2},{sc.Region.Height:F2}] overlaps furniture body."
                        : isVeryLowChroma
                            ? $"Chroma ({sc.Chroma:F1}) is too low to support a strict semantic warm hue label."
                            : isHighVariance
                                ? $"Surface variance ({sc.RegionVariance:F1}) is too high for strict color constraint."
                                : "Furniture envelope localization is uncertain, so a scene-color sample derived relative to that envelope cannot become strict.";

                var conflict = new ContractConflict
                {
                    ConflictId = $"conflict_color_{sc.RegionId}",
                    ConflictType = isColorContradiction ? "SemanticColorConflict" : "UncertainSceneColorLocalization",
                    Description = reason,
                    AffectedConstraintIds = new List<string> { sc.RegionId },
                    ResolutionApplied = "Downgraded color authority to Uncertain. Suppressed semantic color naming. Prompt emits source-preservation instruction."
                };
                conflicts.Add(conflict);

                authorityRecords.Add(new ContractAuthorityRecord
                {
                    ConstraintId = sc.RegionId,
                    Type = "SceneColorReference",
                    Confidence = Math.Min(sc.Confidence, 0.40),
                    Authority = ConstraintAuthority.Uncertain,
                    SourceEvidence = $"Measured Lab=[{sc.ReferenceLab.L:F1}, {sc.ReferenceLab.A:F1}, {sc.ReferenceLab.B:F1}], Chroma={sc.Chroma:F1}, Hue={sc.HueDegrees:F1}°",
                    Reason = reason,
                    ConflictDetected = true,
                    ConflictReason = reason,
                    PromptStatement = "The wall color could not be determined reliably. Preserve the original observed wall color identity; do not recolor it."
                });

                // The raw sampler can name a surface incorrectly or can sample it from an
                // unreliable furniture-relative position. Carry the downgrade into the model
                // consumed by the prompt compiler; an authority record alone is not enough.
                resolvedSceneColors.Add(sc with
                {
                    HueFamily = "observed_surface_tone",
                    WarmCoolBias = "Neutral",
                    Authority = ConstraintAuthority.Uncertain,
                    ColorConflictDetected = true,
                    ProhibitNeutralization = false
                });
            }
            else
            {
                var auth = sc.Confidence >= 0.80 ? ConstraintAuthority.Strict : ConstraintAuthority.Advisory;
                authorityRecords.Add(new ContractAuthorityRecord
                {
                    ConstraintId = sc.RegionId,
                    Type = "SceneColorReference",
                    Confidence = sc.Confidence,
                    Authority = auth,
                    SourceEvidence = $"Measured Lab=[{sc.ReferenceLab.L:F1}, {sc.ReferenceLab.A:F1}, {sc.ReferenceLab.B:F1}], Chroma={sc.Chroma:F1}, Hue={sc.HueDegrees:F1}°",
                    Reason = "Measured CIELAB and hue family are consistent with observed scene surface.",
                    ConflictDetected = false,
                    PromptStatement = $"Preserve permanent {sc.Role} surface color ({sc.HueFamily}, {sc.WarmCoolBias} tone)."
                });
                resolvedSceneColors.Add(sc with { Authority = auth, ColorConflictDetected = false });
            }
        }

        // ---------------------------------------------------------
        // 3. Scene Objects: Clutter Classification & Overlap Conflict
        // ---------------------------------------------------------
        var allObjects = contract.DetectedObjects.Concat(contract.SceneObjects).DistinctBy(o => o.Id).ToList();
        foreach (var obj in allObjects)
        {
            var isFixture = PermanentFixtureClasses.Contains(obj.ClassName);
            var isClutterClass = RemovableClutterClasses.Contains(obj.ClassName);

            // Check if overlaps persistent scene detail
            var overlapsPersistent = contract.PersistentSceneDetails.Any(d =>
                new NormalizedRectangle(d.Region.X, d.Region.Y, d.Region.Width, d.Region.Height).CalculateIoU(obj.Region) > 0.10);

            // Check if overlaps furniture bounds significantly
            var overlapsFurniture = bounds != null && bounds.CalculateIoU(obj.Region) > 0.15;

            if (isFixture || overlapsPersistent)
            {
                // Can NEVER be removable clutter!
                authorityRecords.Add(new ContractAuthorityRecord
                {
                    ConstraintId = obj.Id,
                    Type = "PermanentSceneElement",
                    Confidence = Math.Max(0.85, obj.Confidence),
                    Authority = ConstraintAuthority.Strict,
                    SourceEvidence = $"Class={obj.ClassName}, Region=[{obj.Region.X:F2},{obj.Region.Y:F2},{obj.Region.Width:F2},{obj.Region.Height:F2}]",
                    Reason = isFixture ? "Object is a permanent architectural/furniture fixture." : "Object overlaps verified persistent scene detail.",
                    ConflictDetected = false,
                    PromptStatement = $"Preserve permanent element '{obj.ClassName}' at [{obj.Region.X:F2},{obj.Region.Y:F2},{obj.Region.Width:F2},{obj.Region.Height:F2}]. Do not remove."
                });
            }
            else if (overlapsFurniture && isClutterClass)
            {
                // Conflict: candidate clutter overlaps furniture -> cannot be StrictCleanupAllowed
                var conflict = new ContractConflict
                {
                    ConflictId = $"conflict_clutter_overlap_{obj.Id}",
                    ConflictType = "RemovableClutterFurnitureOverlap",
                    Description = $"Object '{obj.ClassName}' was detected as clutter but overlaps furniture bounds with IoU > 0.15.",
                    AffectedConstraintIds = new List<string> { obj.Id },
                    ResolutionApplied = "Downgraded from StrictCleanup to AdvisoryCleanupAllowed or Uncertain to protect furniture continuity."
                };
                conflicts.Add(conflict);

                var auth = obj.Confidence >= 0.70 ? ConstraintAuthority.AdvisoryCleanupAllowed : ConstraintAuthority.Uncertain;
                authorityRecords.Add(new ContractAuthorityRecord
                {
                    ConstraintId = obj.Id,
                    Type = "SceneObjectClutter",
                    Confidence = obj.Confidence,
                    Authority = auth,
                    SourceEvidence = $"Class={obj.ClassName}, Conf={obj.Confidence:F2}, OverlapIoU={bounds?.CalculateIoU(obj.Region):F2}",
                    Reason = "Object overlaps furniture body; strict removal prohibited to prevent geometric destruction.",
                    ConflictDetected = true,
                    ConflictReason = conflict.Description,
                    PromptStatement = "Remove only if clearly temporary and removal does not require reconstructing furniture."
                });
            }
            else if (isClutterClass)
            {
                hasClutterCandidate = true;
                var auth = obj.Confidence >= 0.65 ? ConstraintAuthority.StrictCleanupAllowed : ConstraintAuthority.AdvisoryCleanupAllowed;
                authorityRecords.Add(new ContractAuthorityRecord
                {
                    ConstraintId = obj.Id,
                    Type = "SceneObjectClutter",
                    Confidence = obj.Confidence,
                    Authority = auth,
                    SourceEvidence = $"Class={obj.ClassName}, Conf={obj.Confidence:F2}, Region=[{obj.Region.X:F2},{obj.Region.Y:F2},{obj.Region.Width:F2},{obj.Region.Height:F2}]",
                    Reason = auth == ConstraintAuthority.StrictCleanupAllowed
                        ? "Generic temporary object unattached to furniture with high confidence."
                        : "Candidate temporary object with moderate confidence.",
                    ConflictDetected = false,
                    PromptStatement = auth == ConstraintAuthority.StrictCleanupAllowed
                        ? $"Safely remove temporary non-structural clutter: {obj.ClassName}."
                        : $"Remove candidate temporary {obj.ClassName} only if clearly non-structural."
                });
            }
        }

        // ---------------------------------------------------------
        // 4. Hardware Cardinality & Protected Absence Authority
        // ---------------------------------------------------------
        foreach (var hw in contract.HardwareCandidates)
        {
            var auth = hw.Confidence >= 0.70 ? ConstraintAuthority.Strict : ConstraintAuthority.Advisory;
            authorityRecords.Add(new ContractAuthorityRecord
            {
                ConstraintId = hw.Id,
                Type = "HardwareCandidate",
                Confidence = hw.Confidence,
                Authority = auth,
                SourceEvidence = $"Hardware detection at [{hw.Region.X:F2},{hw.Region.Y:F2},{hw.Region.Width:F2},{hw.Region.Height:F2}], Conf={hw.Confidence:F2}",
                Reason = auth == ConstraintAuthority.Strict ? "High-confidence localized hardware candidate." : "Medium-confidence hardware candidate.",
                ConflictDetected = false,
                PromptStatement = $"Preserve hardware candidate {hw.Id} at [{hw.Region.X:F2},{hw.Region.Y:F2},{hw.Region.Width:F2},{hw.Region.Height:F2}]."
            });
        }

        foreach (var abs in contract.HardwareAbsenceConstraints)
        {
            var auth = abs.Confidence >= 0.65 ? ConstraintAuthority.Strict : ConstraintAuthority.Advisory;
            authorityRecords.Add(new ContractAuthorityRecord
            {
                ConstraintId = abs.ConstraintId,
                Type = "HardwareAbsence",
                Confidence = abs.Confidence,
                Authority = auth,
                SourceEvidence = abs.SourceEvidence,
                Reason = "Visually confirmed empty hardware flank.",
                ConflictDetected = false,
                PromptStatement = $"Region [{abs.Region.X:F2},{abs.Region.Y:F2},{abs.Region.Width:F2},{abs.Region.Height:F2}] confirmed empty of hardware; do not mirror."
            });
        }

        return new LocalContractConflictReport
        {
            Conflicts = conflicts,
            AuthorityRecords = authorityRecords,
            ResolvedSceneColorReferences = resolvedSceneColors,
            EnvelopeAuthority = envelopeAuthority,
            HasLowCoverageEnvelope = hasLowCoverage,
            HasColorConflict = hasColorConflict,
            HasClutterCandidate = hasClutterCandidate
        };
    }
}
