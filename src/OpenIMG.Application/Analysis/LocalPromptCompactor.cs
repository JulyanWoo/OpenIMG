using System.Globalization;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Analysis;

public class LocalPromptCompactor : ILocalPromptCompactor
{
    // These are semantic authority levels, not image-validation thresholds. The same
    // values are used by LocalContractConflictDetector when classifying strict evidence.
    private const double VerifiedCandidateConfidence = 0.70;

    public PromptCompactionReport Compact(LocalFurnitureContract localContract, FurnitureAnalysis adaptedAnalysis)
    {
        ArgumentNullException.ThrowIfNull(localContract);
        ArgumentNullException.ThrowIfNull(adaptedAnalysis);

        var assemblies = new List<LocalPromptAssembly>();
        var individualCriticalIds = new List<string>();
        var diagnosticNotes = new List<string>();

        var rawRegionCount = localContract.StructuralRegions.Count + localContract.HardwareCandidates.Count;

        // 1. Overall Furniture Envelope Assembly
        var bounds = localContract.FurnitureBounds ?? new NormalizedRectangle(0.05, 0.05, 0.9, 0.9);
        var envelopeRegions = localContract.StructuralRegions
            .Where(r => r.RegionType == StructuralRegionType.LargeFurnitureRegion)
            .ToList();

        var envelopeAuthority = localContract.FurnitureEnvelopeAuthority;
        var envelopeSummary = envelopeAuthority == ConstraintAuthority.Uncertain
            ? string.Format(
                CultureInfo.InvariantCulture,
                "Furniture envelope coverage from local analysis is incomplete/uncertain [{0:F2}, {1:F2}, {2:F2}, {3:F2}]. Do NOT restrict furniture to this partial region. Use the entire original photograph as visual truth for full furniture extent.",
                bounds.X, bounds.Y, bounds.Width, bounds.Height)
            : string.Format(
                CultureInfo.InvariantCulture,
                "Preserve overall furniture structural envelope and outer geometry at [{0:F2}, {1:F2}, {2:F2}, {3:F2}]. Maintain strict aspect ratio and boundary continuity.",
                bounds.X, bounds.Y, bounds.Width, bounds.Height);

        assemblies.Add(new LocalPromptAssembly
        {
            AssemblyId = "assembly_envelope",
            AssemblyType = "structural_assembly",
            Bounds = bounds,
            MemberCount = Math.Max(1, envelopeRegions.Count),
            MemberRegionIds = envelopeRegions.Select(r => r.Id).ToList(),
            StructuralPattern = "outer_envelope",
            Orientation = "none",
            PrimaryColors = adaptedAnalysis.MainColors,
            Protected = envelopeAuthority == ConstraintAuthority.Strict,
            Confidence = localContract.FurnitureConfidence > 0 ? localContract.FurnitureConfidence : 0.85,
            Authority = envelopeAuthority,
            AuthorityReason = envelopeAuthority == ConstraintAuthority.Uncertain ? "Low coverage envelope" : "Verified structural envelope",
            Summary = envelopeSummary
        });

        // 2. Horizontal Divisions & Shelf Candidates
        var hDivs = localContract.StructuralRegions
            .Where(r => (r.RegionType == StructuralRegionType.HorizontalDivision ||
                         r.RegionType == StructuralRegionType.ShelfCandidate) &&
                        r.RegionType != StructuralRegionType.LargeFurnitureRegion)
            .OrderBy(r => r.Region.Y)
            .ThenBy(r => r.Region.X)
            .ToList();

        if (hDivs.Count >= 2)
        {
            var hAvg = hDivs.Average(r => r.Confidence);
            var hBounds = CalculateEnclosingBounds(hDivs.Select(r => r.Region));
            assemblies.Add(new LocalPromptAssembly
            {
                AssemblyId = "assembly_h_divisions",
                AssemblyType = "horizontal_division_group",
                Bounds = hBounds,
                MemberCount = hDivs.Count,
                MemberRegionIds = hDivs.Select(r => r.Id).ToList(),
                StructuralPattern = "parallel_horizontal_divisions",
                Orientation = "horizontal",
                PrimaryColors = adaptedAnalysis.MainColors,
                Protected = localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict && hAvg >= VerifiedCandidateConfidence,
                Confidence = hAvg,
                Authority = localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict && hAvg >= VerifiedCandidateConfidence ? ConstraintAuthority.Strict : ConstraintAuthority.Advisory,
                AuthorityReason = localContract.FurnitureEnvelopeAuthority != ConstraintAuthority.Strict ? "Envelope localization is not verified; division pattern remains advisory" : hAvg >= VerifiedCandidateConfidence ? "High-confidence structural divisions" : "Advisory structural pattern",
                Summary = string.Format(
                    CultureInfo.InvariantCulture,
                    "Preserve the pattern of {0} parallel horizontal structural divisions and shelf boundaries across [{1:F2}, {2:F2}, {3:F2}, {4:F2}]. Preserve their exact alignment, vertical positions, spacing, and component boundaries without adding or removing divisions.",
                    hDivs.Count, hBounds.X, hBounds.Y, hBounds.Width, hBounds.Height)
            });
            diagnosticNotes.Add($"Compacted {hDivs.Count} horizontal divisions into assembly_h_divisions.");
        }
        else if (hDivs.Count == 1)
        {
            individualCriticalIds.Add(hDivs[0].Id);
        }

        // 3. Vertical Divisions
        var vDivs = localContract.StructuralRegions
            .Where(r => r.RegionType == StructuralRegionType.VerticalDivision &&
                        r.RegionType != StructuralRegionType.LargeFurnitureRegion)
            .OrderBy(r => r.Region.X)
            .ThenBy(r => r.Region.Y)
            .ToList();

        if (vDivs.Count >= 2)
        {
            var vAvg = vDivs.Average(r => r.Confidence);
            var vBounds = CalculateEnclosingBounds(vDivs.Select(r => r.Region));
            assemblies.Add(new LocalPromptAssembly
            {
                AssemblyId = "assembly_v_divisions",
                AssemblyType = "vertical_division_group",
                Bounds = vBounds,
                MemberCount = vDivs.Count,
                MemberRegionIds = vDivs.Select(r => r.Id).ToList(),
                StructuralPattern = "parallel_vertical_divisions",
                Orientation = "vertical",
                PrimaryColors = adaptedAnalysis.MainColors,
                Protected = localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict && vAvg >= VerifiedCandidateConfidence,
                Confidence = vAvg,
                Authority = localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict && vAvg >= VerifiedCandidateConfidence ? ConstraintAuthority.Strict : ConstraintAuthority.Advisory,
                AuthorityReason = localContract.FurnitureEnvelopeAuthority != ConstraintAuthority.Strict ? "Envelope localization is not verified; division pattern remains advisory" : vAvg >= VerifiedCandidateConfidence ? "High-confidence structural divisions" : "Advisory structural pattern",
                Summary = string.Format(
                    CultureInfo.InvariantCulture,
                    "Preserve the pattern of {0} parallel vertical structural divisions and panel seams across [{1:F2}, {2:F2}, {3:F2}, {4:F2}]. Maintain their relative horizontal positions, vertical alignment, and spacing with zero structural redesign.",
                    vDivs.Count, vBounds.X, vBounds.Y, vBounds.Width, vBounds.Height)
            });
            diagnosticNotes.Add($"Compacted {vDivs.Count} vertical divisions into assembly_v_divisions.");
        }
        else if (vDivs.Count == 1)
        {
            individualCriticalIds.Add(vDivs[0].Id);
        }

        // 4. Drawer-like Regions
        var drawers = localContract.StructuralRegions
            .Where(r => r.RegionType == StructuralRegionType.DrawerLikeRegion)
            .OrderBy(r => r.Region.Y)
            .ToList();

        if (drawers.Count >= 2)
        {
            var dAvg = drawers.Average(r => r.Confidence);
            var dBounds = CalculateEnclosingBounds(drawers.Select(r => r.Region));
            assemblies.Add(new LocalPromptAssembly
            {
                AssemblyId = "assembly_drawer_stack",
                AssemblyType = "drawer_stack_group",
                Bounds = dBounds,
                MemberCount = drawers.Count,
                MemberRegionIds = drawers.Select(r => r.Id).ToList(),
                StructuralPattern = "vertical_stack",
                Orientation = "horizontal",
                PrimaryColors = adaptedAnalysis.MainColors,
                Protected = localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict && dAvg >= VerifiedCandidateConfidence,
                Confidence = dAvg,
                Authority = localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict && dAvg >= VerifiedCandidateConfidence ? ConstraintAuthority.Strict : ConstraintAuthority.Advisory,
                AuthorityReason = localContract.FurnitureEnvelopeAuthority != ConstraintAuthority.Strict ? "Envelope localization is not verified; drawer pattern remains advisory" : dAvg >= VerifiedCandidateConfidence ? "High-confidence drawer stack" : "Advisory drawer stack",
                Summary = string.Format(
                    CultureInfo.InvariantCulture,
                    "Preserve the stack of {0} drawer-like structural regions across [{1:F2}, {2:F2}, {3:F2}, {4:F2}]. Keep their count, relative heights, horizontal alignment, and reveal seams exactly as observed.",
                    drawers.Count, dBounds.X, dBounds.Y, dBounds.Width, dBounds.Height)
            });
            diagnosticNotes.Add($"Compacted {drawers.Count} drawer candidates into assembly_drawer_stack.");
        }
        else if (drawers.Count == 1)
        {
            individualCriticalIds.Add(drawers[0].Id);
        }

        // 5. Door-like Regions
        var doors = localContract.StructuralRegions
            .Where(r => r.RegionType == StructuralRegionType.DoorLikeRegion)
            .OrderBy(r => r.Region.X)
            .ToList();

        if (doors.Count >= 2)
        {
            var doorAvg = doors.Average(r => r.Confidence);
            var doorBounds = CalculateEnclosingBounds(doors.Select(r => r.Region));
            assemblies.Add(new LocalPromptAssembly
            {
                AssemblyId = "assembly_repeated_panels",
                AssemblyType = "repeated_panel_group",
                Bounds = doorBounds,
                MemberCount = doors.Count,
                MemberRegionIds = doors.Select(r => r.Id).ToList(),
                StructuralPattern = "aligned_vertical_array",
                Orientation = "vertical",
                PrimaryColors = adaptedAnalysis.MainColors,
                Protected = localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict && doorAvg >= VerifiedCandidateConfidence,
                Confidence = doorAvg,
                Authority = localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict && doorAvg >= VerifiedCandidateConfidence ? ConstraintAuthority.Strict : ConstraintAuthority.Advisory,
                AuthorityReason = localContract.FurnitureEnvelopeAuthority != ConstraintAuthority.Strict ? "Envelope localization is not verified; panel pattern remains advisory" : doorAvg >= VerifiedCandidateConfidence ? "High-confidence panel group" : "Advisory panel group",
                Summary = string.Format(
                    CultureInfo.InvariantCulture,
                    "Preserve the set of {0} door-like panel regions across [{1:F2}, {2:F2}, {3:F2}, {4:F2}]. Maintain their widths, vertical orientation, alignment, and separation without modification.",
                    doors.Count, doorBounds.X, doorBounds.Y, doorBounds.Width, doorBounds.Height)
            });
            diagnosticNotes.Add($"Compacted {doors.Count} door candidates into assembly_repeated_panels.");
        }
        else if (doors.Count == 1)
        {
            individualCriticalIds.Add(doors[0].Id);
        }

        // 6. Generic Panels & Rectangular Component Candidates
        var panels = localContract.StructuralRegions
            .Where(r => (r.RegionType == StructuralRegionType.StructuralPanelCandidate ||
                         r.RegionType == StructuralRegionType.RectangularComponentCandidate) &&
                        r.RegionType != StructuralRegionType.LargeFurnitureRegion)
            .OrderBy(r => r.Region.Y)
            .ThenBy(r => r.Region.X)
            .ToList();

        if (panels.Count >= 2)
        {
            var pAvg = panels.Average(r => r.Confidence);
            var pBounds = CalculateEnclosingBounds(panels.Select(r => r.Region));
            assemblies.Add(new LocalPromptAssembly
            {
                AssemblyId = "assembly_structural_panels",
                AssemblyType = "structural_assembly",
                Bounds = pBounds,
                MemberCount = panels.Count,
                MemberRegionIds = panels.Select(r => r.Id).ToList(),
                StructuralPattern = "panel_assembly",
                Orientation = "none",
                PrimaryColors = adaptedAnalysis.MainColors,
                Protected = localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict && pAvg >= VerifiedCandidateConfidence,
                Confidence = pAvg,
                Authority = localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict && pAvg >= VerifiedCandidateConfidence ? ConstraintAuthority.Strict : ConstraintAuthority.Advisory,
                AuthorityReason = localContract.FurnitureEnvelopeAuthority != ConstraintAuthority.Strict ? "Envelope localization is not verified; panel pattern remains advisory" : pAvg >= VerifiedCandidateConfidence ? "High-confidence structural panels" : "Advisory structural panels",
                Summary = string.Format(
                    CultureInfo.InvariantCulture,
                    "Preserve the assembly of {0} structural panel divisions across [{1:F2}, {2:F2}, {3:F2}, {4:F2}]. Zero component addition or removal.",
                    panels.Count, pBounds.X, pBounds.Y, pBounds.Width, pBounds.Height)
            });
            diagnosticNotes.Add($"Compacted {panels.Count} structural panels into assembly_structural_panels.");
        }
        else if (panels.Count == 1)
        {
            individualCriticalIds.Add(panels[0].Id);
        }

        // 7. Hardware Candidates Compaction
        var furnitureBounds = localContract.FurnitureBounds ?? new NormalizedRectangle(0.1, 0.15, 0.8, 0.7);
        var midX = furnitureBounds.X + furnitureBounds.Width / 2.0;

        // Preserve every source candidate for traceability. Only the independently
        // verified subset below can become a strict count or singleton constraint.
        var allHw = localContract.HardwareCandidates
            .OrderBy(h => h.Region.Y)
            .ThenBy(h => h.Region.X)
            .ToList();

        // Identify high-confidence singleton flank mechanisms that must never be compacted
        var singletonHw = new List<HardwareCandidate>();
        var nonSingletonHw = new List<HardwareCandidate>();

        foreach (var h in allHw)
        {
            var isVertical = h.Orientation == "vertical" && h.Region.Height > h.Region.Width * 1.3;
            var isRight = h.Region.X > midX && h.Region.Right > furnitureBounds.Right - 0.22;
            var isLeft = h.Region.Right < midX && h.Region.X < furnitureBounds.X + 0.22;
            var isFlank = isRight || isLeft;

            var oppositeCandidate = allHw.FirstOrDefault(other =>
                other.Id != h.Id &&
                Math.Abs(other.Region.Y - h.Region.Y) < 0.15 &&
                ((isRight && other.Region.Right < midX && other.Region.X < furnitureBounds.X + 0.22) ||
                 (isLeft && other.Region.X > midX && other.Region.Right > furnitureBounds.Right - 0.22)) &&
                other.Orientation == "vertical" &&
                other.Region.Height >= h.Region.Height * 0.55 &&
                other.Region.Width >= h.Region.Width * 0.40);

            if (isFlank && isVertical && h.Confidence >= VerifiedCandidateConfidence && oppositeCandidate == null)
            {
                singletonHw.Add(h);
                // Do NOT compact singleton hardware mechanisms; preserve individually!
                individualCriticalIds.Add(h.Id);
                diagnosticNotes.Add($"Preserved isolated singleton hardware candidate {h.Id} on {(isRight ? "right" : "left")} flank individually.");
            }
            else
            {
                nonSingletonHw.Add(h);
            }
        }

        var hwTraces = new List<HardwareCompactionTrace>();
        foreach (var sh in singletonHw)
        {
            var isR = sh.Region.X > midX;
            var side = isR ? "right" : "left";
            hwTraces.Add(new HardwareCompactionTrace
            {
                ConstraintId = $"hardware_singleton_{sh.Id}_{side}",
                SourceRegionIds = new List<string> { sh.Id },
                ObservedCount = 1,
                LocalizedBounds = sh.Region,
                CompactedPromptStatement = string.Format(CultureInfo.InvariantCulture,
                    "Preserve isolated singleton hardware candidate {0} on {1} flank individually at [{2:F2}, {3:F2}, {4:F2}, {5:F2}]. Count = 1.",
                    sh.Id, side, sh.Region.X, sh.Region.Y, sh.Region.Width, sh.Region.Height)
            });
        }

        var verifiedHw = nonSingletonHw.Where(h => h.Confidence >= VerifiedCandidateConfidence).ToList();
        if (verifiedHw.Count >= 2)
        {
            var hwBounds = CalculateEnclosingBounds(verifiedHw.Select(h => h.Region));
            assemblies.Add(new LocalPromptAssembly
            {
                AssemblyId = "assembly_hardware_candidates",
                AssemblyType = "hardware_candidate_group",
                Bounds = hwBounds,
                MemberCount = verifiedHw.Count,
                MemberRegionIds = verifiedHw.Select(h => h.Id).ToList(),
                StructuralPattern = "aligned_array",
                Orientation = "aligned",
                PrimaryColors = new List<string> { "observed hardware tone" },
                Material = "metallic/hardware candidate",
                Protected = true,
                Confidence = verifiedHw.Average(h => h.Confidence),
                Authority = ConstraintAuthority.Strict,
                AuthorityReason = "Strict verified hardware count and asymmetry",
                Summary = string.Format(
                    CultureInfo.InvariantCulture,
                    "Preserve the set of {0} observed elongated hardware candidates across the drawer/door assembly. Keep their count, approximate positions, orientations, spacing, and metallic appearance unchanged. Do not add, remove, merge, or redesign any of them.",
                    verifiedHw.Count)
            });
            diagnosticNotes.Add($"Compacted {verifiedHw.Count} verified hardware candidates into assembly_hardware_candidates.");

            hwTraces.Add(new HardwareCompactionTrace
            {
                ConstraintId = "assembly_hardware_candidates",
                SourceRegionIds = verifiedHw.Select(h => h.Id).ToList(),
                ObservedCount = verifiedHw.Count,
                LocalizedBounds = hwBounds,
                CompactedPromptStatement = string.Format(CultureInfo.InvariantCulture,
                    "Preserve the set of {0} observed elongated hardware candidates across [{1:F2}, {2:F2}, {3:F2}, {4:F2}].",
                    verifiedHw.Count, hwBounds.X, hwBounds.Y, hwBounds.Width, hwBounds.Height)
            });
        }
        else if (verifiedHw.Count == 1)
        {
            var single = verifiedHw[0];
            individualCriticalIds.Add(single.Id);
            hwTraces.Add(new HardwareCompactionTrace
            {
                ConstraintId = $"hardware_candidate_{single.Id}",
                SourceRegionIds = new List<string> { single.Id },
                ObservedCount = 1,
                LocalizedBounds = single.Region,
                CompactedPromptStatement = string.Format(CultureInfo.InvariantCulture,
                    "Preserve hardware candidate {0} at [{1:F2}, {2:F2}, {3:F2}, {4:F2}]. Count = 1.",
                    single.Id, single.Region.X, single.Region.Y, single.Region.Width, single.Region.Height)
            });
        }
        else if (nonSingletonHw.Count == 1)
        {
            // A low-confidence singleton is still evidence from the source and must
            // remain traceable. It deliberately does not enter a strict assembly or
            // an exact-count constraint.
            var single = nonSingletonHw[0];
            individualCriticalIds.Add(single.Id);
            hwTraces.Add(new HardwareCompactionTrace
            {
                ConstraintId = $"hardware_candidate_advisory_{single.Id}",
                SourceRegionIds = new List<string> { single.Id },
                ObservedCount = 1,
                LocalizedBounds = single.Region,
                CompactedPromptStatement = string.Format(CultureInfo.InvariantCulture,
                    "Retain advisory hardware observation {0} at [{1:F2}, {2:F2}, {3:F2}, {4:F2}]; do not infer its type or duplicate it.",
                    single.Id, single.Region.X, single.Region.Y, single.Region.Width, single.Region.Height)
            });
            diagnosticNotes.Add($"Retained low-confidence singleton hardware candidate {single.Id} as advisory trace evidence.");
        }

        // Sort assemblies deterministically
        assemblies = assemblies.OrderBy(a => a.AssemblyId).ToList();

        var compactedCount = assemblies.Count;
        var individualCount = individualCriticalIds.Count;
        var compactionRatio = rawRegionCount > 0
            ? Math.Max(0.0, 1.0 - (double)(compactedCount + individualCount) / rawRegionCount)
            : 0.0;

        return new PromptCompactionReport
        {
            RawRegionCount = rawRegionCount,
            CompactedAssemblyCount = compactedCount,
            IndividualCriticalRegionCount = individualCount,
            CompactionRatio = compactionRatio,
            Assemblies = assemblies,
            IndividualCriticalRegionIds = individualCriticalIds,
            HardwareTraces = hwTraces,
            DiagnosticNotes = diagnosticNotes
        };
    }

    private static NormalizedRectangle CalculateEnclosingBounds(IEnumerable<NormalizedRectangle> rects)
    {
        var list = rects.ToList();
        if (list.Count == 0) return new NormalizedRectangle(0, 0, 0, 0);

        var minX = list.Min(r => r.X);
        var minY = list.Min(r => r.Y);
        var maxX = list.Max(r => r.Right);
        var maxY = list.Max(r => r.Bottom);

        return new NormalizedRectangle(minX, minY, maxX - minX, maxY - minY);
    }
}
