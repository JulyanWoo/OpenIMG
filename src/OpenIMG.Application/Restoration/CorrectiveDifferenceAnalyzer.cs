using System.Globalization;
using System.Text.RegularExpressions;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Restoration;

/// <summary>
/// Converts the existing local Original-vs-draft validation evidence into a
/// bounded corrective contract. It does not measure images or change validation policy.
/// </summary>
public sealed class CorrectiveDifferenceAnalyzer
{
    private static readonly Regex CardinalityMessage = new(
        @"observed (?<source>\d+), restored physical candidates (?<draft>\d+) \(Status: (?<status>[^)]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly HashSet<string> StructuralEvidenceTypes = new(StringComparer.Ordinal)
    {
        "ComponentStructureAdded",
        "ComponentStructureRemoved",
        "ComponentCardinalityMismatch",
        "CriticalHardwareRemoved",
        "HardwareAbsenceViolated",
        "UnexpectedStructureInProtectedAbsence",
        "ProtectedNegativeSpaceViolation",
        "PersistentSceneDetailRemoved",
        "UnsupportedPhysicalAddition",
        "PermanentScenePhysicalAddition",
        "SevereStructuralDeviation",
        "UnexpectedEdgeCluster",
        "FurnitureBoundaryIntrusion",
        "ComponentColorIdentityChanged",
        "PermanentSceneColorIdentityChanged",
        "ImageRegistration",
        "RegistrationModel"
    };

    public CorrectiveRestorationContract Analyze(FurnitureAnalysisContract source, LocalValidationReport comparison)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(comparison);

        var preserve = new List<CorrectiveInstruction>
        {
            CorrectiveInstruction.Policy("PhotographicImprovement"),
            CorrectiveInstruction.Policy("GeminiCleanup")
        };
        var restore = new List<CorrectiveInstruction>();
        var remove = new List<CorrectiveInstruction>();
        var uncertain = new List<CorrectiveInstruction>();
        var requiresReview = false;

        foreach (var evidence in comparison.Evidences.Where(e => !e.Passed && StructuralEvidenceTypes.Contains(e.Type)))
        {
            var instruction = CorrectiveInstruction.Evidence(
                evidence.Type,
                SafeRegion(evidence.Region),
                evidence.Severity,
                feature: ResolveFeature(source, evidence.Region),
                location: ResolveLocation(source, evidence.Region));
            switch (evidence.Type)
            {
                case "ComponentStructureAdded":
                case "HardwareAbsenceViolated":
                case "UnexpectedStructureInProtectedAbsence":
                case "ProtectedNegativeSpaceViolation":
                    ClassifyLocalizedConflict(evidence.Severity, instruction, remove, uncertain, ref requiresReview);
                    break;
                case "ComponentStructureRemoved":
                case "CriticalHardwareRemoved":
                case "PersistentSceneDetailRemoved":
                case "ComponentColorIdentityChanged":
                case "PermanentSceneColorIdentityChanged":
                    ClassifyLocalizedConflict(evidence.Severity, instruction, restore, uncertain, ref requiresReview);
                    break;
                case "UnsupportedPhysicalAddition":
                case "PermanentScenePhysicalAddition":
                    // These summary evidence types do not prove a localizable region on their own.
                    uncertain.Add(instruction);
                    requiresReview = true;
                    break;
                case "ComponentCardinalityMismatch":
                    ClassifyCardinalityMismatch(source, evidence, restore, remove, uncertain, ref requiresReview);
                    break;
                case "SevereStructuralDeviation":
                case "UnexpectedEdgeCluster":
                case "FurnitureBoundaryIntrusion":
                case "ImageRegistration":
                case "RegistrationModel":
                    uncertain.Add(instruction);
                    requiresReview = true;
                    break;
            }
        }

        foreach (var addition in comparison.UnsupportedAdditions.Where(a => a.IsViolation && a.Category != PhysicalAdditionCategory.MinorSceneDetailAddition))
        {
            var instruction = CorrectiveInstruction.Evidence(
                "UnsupportedPhysicalAddition",
                SafeRegion(addition.AdditionId),
                addition.Severity,
                addition.Confidence,
                ResolveFeature(source, addition.AdditionId),
                SafeLocation(addition.Region));

            if (addition.SourceSupportStatus != SourceSupportStatus.Unsupported || addition.Severity != ValidationSeverity.Critical)
            {
                uncertain.Add(instruction);
                requiresReview = true;
                continue;
            }

            remove.Add(instruction);
            if (addition.Scale == PhysicalAdditionScale.Large)
            {
                uncertain.Add(instruction);
                requiresReview = true;
            }
        }

        foreach (var violation in comparison.NegativeSpaceViolations.Where(v => v.IsViolation))
        {
            var instruction = CorrectiveInstruction.Evidence(
                "ProtectedNegativeSpaceViolation",
                SafeRegion(violation.NegativeSpaceRegionId),
                violation.Severity,
                violation.Confidence,
                ResolveFeature(source, violation.NegativeSpaceRegionId),
                SafeLocation(violation.Region));

            if (violation.ProtectionLevel == ConstraintAuthority.Strict && violation.Severity == ValidationSeverity.Critical)
                remove.Add(instruction);
            else
            {
                uncertain.Add(instruction);
                requiresReview = true;
            }
        }

        var hasDefinitiveCorrection = restore.Count + remove.Count > 0;
        var route = requiresReview
            ? CorrectiveRestorationRoute.NeedsReview
            : hasDefinitiveCorrection
                ? CorrectiveRestorationRoute.TargetedStructuralCorrection
                : CorrectiveRestorationRoute.DeltaCorrection;

        return new CorrectiveRestorationContract
        {
            SourceSha256 = source.SourceIdentity.Sha256,
            Route = route,
            RouteReason = route switch
            {
                CorrectiveRestorationRoute.NeedsReview => "Local evidence contains a broad, ambiguous, or non-critical structural conflict; do not ask GPT to guess.",
                CorrectiveRestorationRoute.TargetedStructuralCorrection => "Local evidence identifies one or more critical, localized physical conflicts.",
                _ => "No structural conflict was reported by local comparison."
            },
            PreserveFromDraft = preserve,
            RestoreFromOriginal = Distinct(restore),
            RemoveUnsupportedFromDraft = Distinct(remove),
            UncertainDoNotInfer = Distinct(uncertain)
        };
    }

    private static void ClassifyLocalizedConflict(
        ValidationSeverity severity,
        CorrectiveInstruction instruction,
        ICollection<CorrectiveInstruction> definitive,
        ICollection<CorrectiveInstruction> uncertain,
        ref bool requiresReview)
    {
        if (severity == ValidationSeverity.Critical && !string.IsNullOrWhiteSpace(instruction.Region))
            definitive.Add(instruction);
        else
        {
            uncertain.Add(instruction);
            requiresReview = true;
        }
    }

    private static void ClassifyCardinalityMismatch(
        FurnitureAnalysisContract source,
        ValidationEvidence evidence,
        ICollection<CorrectiveInstruction> restore,
        ICollection<CorrectiveInstruction> remove,
        ICollection<CorrectiveInstruction> uncertain,
        ref bool requiresReview)
    {
        var match = CardinalityMessage.Match(evidence.Message);
        if (evidence.Severity != ValidationSeverity.Critical || !match.Success ||
            !string.Equals(match.Groups["status"].Value.Trim(), "Reliable", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(match.Groups["source"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var sourceCount) ||
            !int.TryParse(match.Groups["draft"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var draftCount) ||
            string.IsNullOrWhiteSpace(evidence.Region) || sourceCount == draftCount)
        {
            uncertain.Add(CorrectiveInstruction.Evidence(evidence.Type, SafeRegion(evidence.Region), evidence.Severity, feature: ResolveFeature(source, evidence.Region), location: ResolveLocation(source, evidence.Region)));
            requiresReview = true;
            return;
        }

        var instruction = CorrectiveInstruction.Evidence(evidence.Type, SafeRegion(evidence.Region), evidence.Severity, feature: ResolveFeature(source, evidence.Region), location: ResolveLocation(source, evidence.Region));
        if (draftCount > sourceCount)
            remove.Add(instruction);
        else
            restore.Add(instruction);
    }

    private static List<CorrectiveInstruction> Distinct(IEnumerable<CorrectiveInstruction> instructions)
        => instructions
            .GroupBy(i => (i.EvidenceType, i.Region), StringTupleComparer.Instance)
            .Select(group => group.First())
            .ToList();

    private static string? SafeRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region) || region.Length > 80 || !char.IsAsciiLetter(region[0])) return null;
        return region.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? region : null;
    }

    private static string ResolveFeature(FurnitureAnalysisContract source, string? regionId)
    {
        if (string.IsNullOrWhiteSpace(regionId)) return "permanent component";

        var terms = source.Components.Where(c => string.Equals(c.Id, regionId, StringComparison.OrdinalIgnoreCase)).Select(c => c.Type)
            .Concat(source.StructuralRegions.Where(r => string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase)).Select(r => r.RegionType.ToString()))
            .Concat(source.Hardware.Where(h => string.Equals(h.Id, regionId, StringComparison.OrdinalIgnoreCase)).Select(h => h.GeometryType))
            .Concat(source.ComponentCardinalityConstraints.Where(c => string.Equals(c.ConstraintId, regionId, StringComparison.OrdinalIgnoreCase)).Select(c => c.Category));
        var value = string.Join(' ', terms).ToLowerInvariant();
        return value switch
        {
            var term when term.Contains("hydraulic", StringComparison.Ordinal) => "hydraulic mechanism",
            var term when term.Contains("handle", StringComparison.Ordinal) => "handle",
            var term when term.Contains("hinge", StringComparison.Ordinal) => "hinge",
            var term when term.Contains("drawer", StringComparison.Ordinal) => "drawer",
            var term when term.Contains("shelf", StringComparison.Ordinal) => "shelf",
            var term when term.Contains("door", StringComparison.Ordinal) => "door",
            var term when term.Contains("panel", StringComparison.Ordinal) => "panel",
            var term when term.Contains("opening", StringComparison.Ordinal) => "opening",
            var term when term.Contains("support", StringComparison.Ordinal) => "support",
            var term when term.Contains("countertop", StringComparison.Ordinal) => "countertop",
            var term when term.Contains("sink", StringComparison.Ordinal) => "sink",
            var term when term.Contains("switch", StringComparison.Ordinal) => "wall switch",
            var term when term.Contains("outlet", StringComparison.Ordinal) => "electrical outlet",
            var term when term.Contains("wall", StringComparison.Ordinal) => "wall structure",
            var term when term.Contains("floor", StringComparison.Ordinal) => "floor structure",
            _ => "permanent component"
        };
    }

    private static NormalizedRectangle? ResolveLocation(FurnitureAnalysisContract source, string? regionId)
    {
        if (string.IsNullOrWhiteSpace(regionId)) return null;
        var component = source.Components.FirstOrDefault(c => string.Equals(c.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (component?.Region is { } componentRegion) return SafeLocation(NormalizedRectangle.FromProtectedRegion(componentRegion));

        var structural = source.StructuralRegions.FirstOrDefault(r => string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (structural != null) return SafeLocation(structural.Region);

        var hardware = source.Hardware.FirstOrDefault(h => string.Equals(h.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (hardware != null) return SafeLocation(hardware.Region);

        var constraint = source.ComponentCardinalityConstraints.FirstOrDefault(c => string.Equals(c.ConstraintId, regionId, StringComparison.OrdinalIgnoreCase));
        if (constraint != null) return SafeLocation(NormalizedRectangle.FromProtectedRegion(constraint.Region));

        var detail = source.PersistentSceneDetails.FirstOrDefault(d => string.Equals(d.CandidateId, regionId, StringComparison.OrdinalIgnoreCase));
        return detail == null ? null : SafeLocation(NormalizedRectangle.FromProtectedRegion(detail.Region));
    }

    private static NormalizedRectangle? SafeLocation(NormalizedRectangle? bounds)
    {
        if (bounds is not { } value ||
            !double.IsFinite(value.X) || !double.IsFinite(value.Y) ||
            !double.IsFinite(value.Width) || !double.IsFinite(value.Height))
            return null;
        return value.Clamp();
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string EvidenceType, string? Region)>
    {
        public static StringTupleComparer Instance { get; } = new();
        public bool Equals((string EvidenceType, string? Region) x, (string EvidenceType, string? Region) y)
            => string.Equals(x.EvidenceType, y.EvidenceType, StringComparison.Ordinal) && string.Equals(x.Region, y.Region, StringComparison.Ordinal);
        public int GetHashCode((string EvidenceType, string? Region) value)
            => HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.EvidenceType), value.Region == null ? 0 : StringComparer.Ordinal.GetHashCode(value.Region));
    }
}
