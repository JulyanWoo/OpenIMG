using System.Globalization;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Analysis;

public class LocalToFurnitureAnalysisAdapter : ILocalToFurnitureAnalysisAdapter
{
    private readonly ILocalContractConflictDetector _conflictDetector;

    private static readonly HashSet<string> FurnitureClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chair", "couch", "sofa", "bed", "dining table", "desk", "wardrobe", "cabinet"
    };

    private static readonly HashSet<string> PermanentFixtures = new(StringComparer.OrdinalIgnoreCase)
    {
        "switch", "outlet", "plug", "countertop", "counter", "sink", "faucet", "tap",
        "handle", "hinge", "knob", "pull", "cabinet", "drawer", "door", "shelf",
        "wall", "floor", "ceiling", "window", "mirror", "backsplash", "cooktop", "stove"
    };

    private static readonly HashSet<string> GenericRemovableClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "bottle", "cup", "wine glass", "bowl", "plate", "bag", "handbag", "backpack",
        "suitcase", "stool", "sack", "box", "package", "packaging", "container",
        "rag", "cloth", "towel", "tool", "sponge", "debris", "can", "pan", "pot"
    };

    public LocalToFurnitureAnalysisAdapter()
        : this(new LocalContractConflictDetector())
    {
    }

    public LocalToFurnitureAnalysisAdapter(ILocalContractConflictDetector conflictDetector)
    {
        _conflictDetector = conflictDetector ?? throw new ArgumentNullException(nameof(conflictDetector));
    }

    public FurnitureAnalysis Adapt(LocalFurnitureContract localContract)
    {
        ArgumentNullException.ThrowIfNull(localContract);

        // The normal engine persists an already-resolved contract. Keeping this
        // defensive resolution here makes every caller (including resumed jobs and
        // calibration tools) obey the same authority policy instead of accidentally
        // compiling raw observations into strict instructions.
        if (localContract.AuthorityRecords.Count == 0)
        {
            var authority = _conflictDetector.DetectAndResolveConflicts(localContract);
            localContract = localContract with
            {
                FurnitureEnvelopeAuthority = authority.EnvelopeAuthority,
                SceneColorReferences = authority.ResolvedSceneColorReferences,
                AuthorityRecords = authority.AuthorityRecords,
                Conflicts = authority.Conflicts
            };
        }

        // 1. Conservative Furniture Type
        var bounds = localContract.FurnitureBounds ?? new NormalizedRectangle(0.05, 0.05, 0.9, 0.9);
        var furnitureType = "furniture assembly (locally detected candidate)";
        var primaryObject = localContract.DetectedObjects
            .Where(o => FurnitureClasses.Contains(o.ClassName) && o.Region.Area > 0.08 && bounds.CalculateIoU(o.Region) > 0.15)
            .OrderByDescending(o => o.Confidence)
            .FirstOrDefault();

        if (primaryObject != null && localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict)
        {
            var rawClass = primaryObject.ClassName.ToLowerInvariant();
            if (rawClass == "couch" || rawClass == "sofa")
                furnitureType = "seating/sofa candidate";
            else if (rawClass == "chair")
                furnitureType = "seating/chair candidate";
            else if (rawClass == "bed")
                furnitureType = "bed candidate";
            else if (rawClass == "dining table")
                furnitureType = "table candidate";
            else
                furnitureType = $"{rawClass} candidate";
        }

        // 2. Geometry Description
        var geometryDesc = string.Format(
            CultureInfo.InvariantCulture,
            "Locally observed furniture structural boundary at [{0:F2}, {1:F2}, {2:F2}, {3:F2}] with {4} structural regions and {5} candidate hardware elements.",
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            localContract.StructuralRegions.Count,
            localContract.HardwareCandidates.Count);

        // 3. Colors (Dominant and Accents)
        var mainColors = localContract.ColorRegions
            .Where(c => c.IsDominant)
            .Select(GetColorName)
            .Distinct()
            .ToList();

        if (mainColors.Count == 0)
        {
            mainColors.Add("observed neutral structural tone");
        }

        var accentColors = localContract.ColorRegions
            .Where(c => c.IsAccentCandidate)
            .Select(GetColorName)
            .Distinct()
            .ToList();

        var colorLocations = new Dictionary<string, string>();
        if (localContract.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict)
        {
            foreach (var mc in mainColors)
            {
                colorLocations[mc] = "main observed furniture body and structural regions";
            }
            foreach (var ac in accentColors)
            {
                colorLocations[ac] = "strictly restricted to localized observed coordinates; do not spread or bleed";
            }
        }

        // 4. Material Hints (Conservative)
        var mainMaterials = new List<string>();
        foreach (var hint in localContract.MaterialHints)
        {
            switch (hint.Material)
            {
                case MaterialHintType.GlassLikeReflective:
                    mainMaterials.Add("glass-like reflective surface (low-confidence hint)");
                    break;
                case MaterialHintType.WoodLikeTexture:
                    mainMaterials.Add("wood-textured surface (low-confidence hint)");
                    break;
                case MaterialHintType.MetalLikeHighlight:
                    mainMaterials.Add("metallic surface (low-confidence hint)");
                    break;
            }
        }
        if (mainMaterials.Count == 0)
        {
            mainMaterials.Add("observed structural finish");
        }
        mainMaterials = mainMaterials.Distinct().ToList();

        // 5. Components Mapping (Strictly Conservative)
        var components = new List<FurnitureComponent>();
        var protectedRegions = new List<ProtectedRegion>();

        // Overall furniture envelope protected region
        protectedRegions.Add(new ProtectedRegion
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            RegionType = "furniture_silhouette",
            Description = "Locally detected furniture structural envelope",
            Importance = 1.0
        });

        // Structural regions mapping
        foreach (var sr in localContract.StructuralRegions)
        {
            string mappedType;
            string mappedDesc;

            switch (sr.RegionType)
            {
                case StructuralRegionType.DoorLikeRegion:
                    mappedType = "door_like_candidate";
                    mappedDesc = "Door-like structural candidate with uncertainty";
                    break;
                case StructuralRegionType.DrawerLikeRegion:
                    mappedType = "drawer_like_candidate";
                    mappedDesc = "Drawer-like structural candidate with uncertainty";
                    break;
                case StructuralRegionType.StructuralPanelCandidate:
                    mappedType = "structural_panel";
                    mappedDesc = "Generic structural panel candidate";
                    break;
                case StructuralRegionType.HorizontalDivision:
                    mappedType = "horizontal_division";
                    mappedDesc = "Horizontal structural boundary division";
                    break;
                case StructuralRegionType.VerticalDivision:
                    mappedType = "vertical_division";
                    mappedDesc = "Vertical structural boundary division";
                    break;
                case StructuralRegionType.ShelfCandidate:
                    mappedType = "shelf_candidate";
                    mappedDesc = "Horizontal shelf division candidate";
                    break;
                case StructuralRegionType.RectangularComponentCandidate:
                    mappedType = "rectangular_component";
                    mappedDesc = "Rectangular structural component candidate with uncertainty";
                    break;
                case StructuralRegionType.LargeFurnitureRegion:
                    mappedType = "structural_envelope";
                    mappedDesc = "Overall furniture structural body envelope";
                    break;
                default:
                    mappedType = "structural_candidate";
                    mappedDesc = "Generic structural region candidate with uncertainty";
                    break;
            }

            // Check overlapping material hint
            var overlappingMat = localContract.MaterialHints
                .FirstOrDefault(m => m.Region.CalculateIoU(sr.Region) > 0.05 || m.Region.IntersectionOverSelf(sr.Region) > 0.15);
            var compMaterial = overlappingMat != null
                ? $"{overlappingMat.Material} (low confidence)"
                : "observed structural finish";

            // Check overlapping color
            var overlappingColorRegion = localContract.ColorRegions
                .FirstOrDefault(c => c.Region.CalculateIoU(sr.Region) > 0.08);
            var compColor = overlappingColorRegion != null ? GetColorName(overlappingColorRegion) : mainColors[0];

            var comp = new FurnitureComponent
            {
                Id = sr.Id,
                Type = mappedType,
                Description = mappedDesc,
                Color = compColor,
                Material = compMaterial,
                IsStructural = true,
                Importance = "critical",
                Confidence = sr.Confidence,
                Region = new ProtectedRegion
                {
                    X = sr.Region.X,
                    Y = sr.Region.Y,
                    Width = sr.Region.Width,
                    Height = sr.Region.Height,
                    RegionType = mappedType,
                    Description = mappedDesc,
                    Importance = 1.0
                }
            };

            components.Add(comp);
            protectedRegions.Add(comp.Region);
        }

        // Hardware candidates mapping (Do NOT invent labels like hinge or knob!)
        foreach (var hw in localContract.HardwareCandidates)
        {
            var hwComp = new FurnitureComponent
            {
                Id = hw.Id,
                Type = "hardware_candidate",
                Description = "Unidentified hardware candidate (preserve exact position and form)",
                Color = "observed hardware tone",
                Material = "metallic/hardware candidate",
                IsStructural = false,
                Importance = "high",
                Confidence = hw.Confidence,
                Region = new ProtectedRegion
                {
                    X = hw.Region.X,
                    Y = hw.Region.Y,
                    Width = hw.Region.Width,
                    Height = hw.Region.Height,
                    RegionType = "hardware_candidate",
                    Description = "Hardware candidate",
                    Importance = 0.9
                }
            };

            components.Add(hwComp);
            protectedRegions.Add(hwComp.Region);
        }

        // 6. Semantic Sub-Lists
        var doors = new List<string>();
        var doorCount = localContract.StructuralRegions.Count(r => r.RegionType == StructuralRegionType.DoorLikeRegion);
        if (doorCount > 0)
        {
            doors.Add(string.Format(CultureInfo.InvariantCulture, "{0} candidate door-like structural regions with uncertainty", doorCount));
        }

        var drawers = new List<string>();
        var drawerCount = localContract.StructuralRegions.Count(r => r.RegionType == StructuralRegionType.DrawerLikeRegion);
        if (drawerCount > 0)
        {
            drawers.Add(string.Format(CultureInfo.InvariantCulture, "{0} candidate drawer-like structural regions with uncertainty", drawerCount));
        }

        var handles = new List<string>();
        if (localContract.HardwareCandidates.Count > 0)
        {
            handles.Add(string.Format(CultureInfo.InvariantCulture, "{0} candidate hardware elements", localContract.HardwareCandidates.Count));
        }

        var glassAreas = new List<string>();
        if (localContract.MaterialHints.Any(m => m.Material == MaterialHintType.GlassLikeReflective))
        {
            glassAreas.Add("candidate glass-like reflective surface (preserve original appearance)");
        }
        else
        {
            glassAreas.Add("none detected");
        }

        // 7. Protected and Uncertain Features
        var protectedFeatures = new List<string>
        {
            "Preserve detected furniture outer boundary and aspect ratio exactly.",
            "Preserve observed structural divisions, panels, doors, and drawers without adding or removing components.",
            "Confine localized accent colors strictly to observed regions.",
            "Preserve all candidate hardware elements in their exact localized positions."
        };

        var uncertainFeatures = new List<string>
        {
            "Exact semantic classification of panels and door/drawer divisions is candidate-based and subject to uncertainty.",
            "Hardware sub-type (knob vs pull vs hinge) is unverified candidate; original appearance must be preserved."
        };

        if (localContract.MaterialHints.Any(m => m.Material == MaterialHintType.GlassLikeReflective))
        {
            uncertainFeatures.Add("Glass-like reflective area detected with low confidence; preserve original reflection and surface properties.");
        }

        // 8. Scene Objects Mapping
        var sceneObjects = new List<SceneObject>();
        var clutter = new List<string>();

        var candidateSceneObjects = localContract.SceneObjects
            .Concat(localContract.DetectedObjects.Where(o => !FurnitureClasses.Contains(o.ClassName) || o.Region.Area <= 0.08 || bounds.CalculateIoU(o.Region) <= 0.15))
            .DistinctBy(o => o.Id)
            .ToList();

        foreach (var obj in candidateSceneObjects)
        {
            var isFixture = PermanentFixtures.Contains(obj.ClassName);
            var isPersistent = localContract.PersistentSceneDetails.Any(d =>
                new NormalizedRectangle(d.Region.X, d.Region.Y, d.Region.Width, d.Region.Height).CalculateIoU(obj.Region) > 0.10);
            var authority = localContract.AuthorityRecords.FirstOrDefault(r => r.ConstraintId == obj.Id)?.Authority;
            var cleanupExplicitlyAllowed = authority is ConstraintAuthority.StrictCleanupAllowed or ConstraintAuthority.AdvisoryCleanupAllowed;
            var isExplicitRemovable = GenericRemovableClasses.Contains(obj.ClassName) || obj.IsRemovableClutter;

            // Phase 4F.4 Generic Temporary Clutter Detection (Part 6 & 7):
            // Visual signals: outside protected furniture structure, not an architectural fixture,
            // not a persistent scene detail, compact, and distinct from permanent architecture.
            var overlapsFurniture = bounds.CalculateIoU(obj.Region) > 0.15;
            bool isUnverified = obj.Id.StartsWith("weak-", StringComparison.OrdinalIgnoreCase) || obj.Confidence < 0.32;
            var isRemovable = !isFixture &&
                              !isPersistent &&
                              !overlapsFurniture &&
                              !isUnverified &&
                              obj.Region.Area <= 0.20 &&
                              authority != ConstraintAuthority.Uncertain &&
                              (isExplicitRemovable || cleanupExplicitlyAllowed || authority == null || authority == ConstraintAuthority.Advisory);

            var classification = isRemovable ? SceneClassification.RemovableClutter : SceneClassification.PermanentEnvironment;
            var precedence = isRemovable ? RegionPrecedence.RemovableScene : RegionPrecedence.PermanentEnvironment;

            if (isRemovable && (isExplicitRemovable || cleanupExplicitlyAllowed))
            {
                clutter.Add(obj.ClassName);
            }

            sceneObjects.Add(new SceneObject
            {
                Id = obj.Id,
                Description = $"Observed scene object: {obj.ClassName}",
                Classification = classification,
                Precedence = precedence,
                Occlusion = OcclusionType.SceneOnly,
                IsRemovable = isRemovable,
                Confidence = obj.Confidence,
                Region = new ProtectedRegion
                {
                    X = obj.Region.X,
                    Y = obj.Region.Y,
                    Width = obj.Region.Width,
                    Height = obj.Region.Height,
                    RegionType = "scene_object",
                    Description = obj.ClassName,
                    Importance = isRemovable ? 0.2 : 0.7
                }
            });
        }

        // Cardinality is geometric: do not turn an uncertain candidate into a semantic label.
        // Exact count is only asserted after spatial de-duplication of substantially overlapping detections.
        var deduplicatedHardware = localContract.HardwareCandidates
            .OrderByDescending(h => h.Confidence)
            .Where(h => h.Confidence >= 0.45)
            .Aggregate(new List<HardwareCandidate>(), (kept, candidate) =>
            {
                if (!kept.Any(existing => existing.Region.CalculateIoU(candidate.Region) >= 0.60)) kept.Add(candidate);
                return kept;
            });

        var cardinality = new List<ComponentCardinalityConstraint>();
        var hardwareAbsences = new List<HardwareAbsenceConstraint>(localContract.HardwareAbsenceConstraints);

        var furnitureBounds = localContract.FurnitureBounds ?? new NormalizedRectangle(0.1, 0.15, 0.8, 0.7);
        var midX = furnitureBounds.X + furnitureBounds.Width / 2.0;
        var singletonMechanisms = new List<HardwareCandidate>();

        // Identify high-confidence singleton flank mechanisms (e.g. single vertical hydraulic strut on right flank)
        foreach (var hw in deduplicatedHardware.Where(h =>
            h.Confidence >= 0.40 &&
            string.IsNullOrEmpty(h.RepeatGroupId) &&
            h.Region.X > 0.02 && h.Region.Right < 0.98 &&
            h.Region.Height >= 0.12 &&
            (h.Orientation == "vertical" || h.Region.Height > h.Region.Width * 1.3)))
        {
            var isRight = hw.Region.X > midX && hw.Region.Right > furnitureBounds.Right - 0.22;
            var isLeft = hw.Region.Right < midX && hw.Region.X < furnitureBounds.X + 0.22;
            var isFlank = isRight || isLeft;

            var oppositeCandidate = deduplicatedHardware.FirstOrDefault(other =>
                other.Id != hw.Id &&
                Math.Abs(other.Region.Y - hw.Region.Y) < 0.15 &&
                ((isRight && other.Region.Right < midX && other.Region.X < furnitureBounds.X + 0.22) ||
                 (isLeft && other.Region.X > midX && other.Region.Right > furnitureBounds.Right - 0.22)) &&
                other.Orientation == "vertical" &&
                other.Region.Height >= hw.Region.Height * 0.55 &&
                other.Region.Width >= hw.Region.Width * 0.40);

            if (isFlank && oppositeCandidate == null)
            {
                singletonMechanisms.Add(hw);
                var side = isRight ? "Right" : "Left";
                var oppositeSide = isRight ? "Left" : "Right";
                var constraintId = $"hardware_singleton_{hw.Id}_{side.ToLowerInvariant()}";
                var absenceId = $"hardware_absence_{oppositeSide.ToLowerInvariant()}_for_{hw.Id}";

                cardinality.Add(new ComponentCardinalityConstraint
                {
                    ConstraintId = constraintId,
                    Category = "elongated hardware mechanism candidate",
                    RawCandidateCount = 1,
                    DeduplicatedCount = 1,
                    MinimumAllowedCount = 1,
                    MaximumAllowedCount = 1,
                    Region = new ProtectedRegion
                    {
                        X = hw.Region.X,
                        Y = hw.Region.Y,
                        Width = hw.Region.Width,
                        Height = hw.Region.Height,
                        RegionType = "hardware_singleton",
                        Description = $"Singleton elongated hardware mechanism on {side} flank",
                        Importance = 1.0
                    },
                    Confidence = hw.Confidence,
                    SourceRegionIds = new List<string> { hw.Id },
                    Strict = true,
                    Side = side,
                    RelativePosition = isRight ? "RightFlank" : "LeftFlank",
                    IsSingletonMechanism = true,
                    OppositeAbsenceConstraintId = absenceId
                });

                // Derive HardwareAbsenceConstraint for the opposite flank centered symmetrically
                var hwCenterX = hw.Region.X + hw.Region.Width / 2.0;
                var distFromEdge = isRight
                    ? furnitureBounds.Right - hwCenterX
                    : hwCenterX - furnitureBounds.X;

                var oppCenterX = isRight
                    ? furnitureBounds.X + distFromEdge
                    : furnitureBounds.Right - distFromEdge;

                var oppW = Math.Clamp(Math.Max(0.10, hw.Region.Width * 1.8), 0.05, 0.25);
                var oppH = Math.Clamp(Math.Max(0.18, hw.Region.Height * 1.4), 0.10, 0.40);
                var oppX = Math.Clamp(oppCenterX - oppW / 2.0, 0.0, 1.0 - oppW);
                var oppY = Math.Clamp(hw.Region.Y - 0.02, 0.0, 1.0 - oppH);

                var oppRegion = new ProtectedRegion
                {
                    X = Math.Round(oppX, 3),
                    Y = Math.Round(oppY, 3),
                    Width = Math.Round(oppW, 3),
                    Height = Math.Round(oppH, 3),
                    RegionType = "hardware_absence",
                    Description = $"Protected hardware absence on {oppositeSide} flank (no matching {side} mechanism observed)",
                    Importance = 1.0
                };

                if (!hardwareAbsences.Any(a => a.ConstraintId == absenceId))
                {
                    hardwareAbsences.Add(new HardwareAbsenceConstraint
                    {
                        ConstraintId = absenceId,
                        Region = oppRegion,
                        HardwareFamily = "elongated_hardware_mechanism",
                        ObservedCount = 0,
                        Confidence = Math.Clamp(hw.Confidence + 0.30, 0.40, 0.85),
                        AdjacentConstraintId = constraintId,
                        Strict = true,
                        Side = oppositeSide,
                        SourceEvidence = $"Visual confirmation: exactly 1 elongated hardware mechanism candidate observed on {side} flank; opposite {oppositeSide} flank confirmed empty."
                    });
                }
            }
        }

        // Remaining non-singleton hardware candidates
        var remainingHw = deduplicatedHardware.Except(singletonMechanisms).ToList();
        if (remainingHw.Count > 0)
        {
            var minX = remainingHw.Min(h => h.Region.X); var minY = remainingHw.Min(h => h.Region.Y);
            var maxX = remainingHw.Max(h => h.Region.Right); var maxY = remainingHw.Max(h => h.Region.Bottom);
            cardinality.Add(new ComponentCardinalityConstraint
            {
                ConstraintId = "hardware_candidates_remaining_count",
                Category = "observed hardware candidate",
                RawCandidateCount = remainingHw.Count,
                DeduplicatedCount = remainingHw.Count,
                MinimumAllowedCount = remainingHw.Count,
                MaximumAllowedCount = remainingHw.Count,
                Region = new ProtectedRegion { X = minX, Y = minY, Width = maxX - minX, Height = maxY - minY, RegionType = "hardware_cardinality", Description = "Remaining observed hardware count", Importance = 0.8 },
                Confidence = remainingHw.Average(h => h.Confidence),
                SourceRegionIds = remainingHw.Select(h => h.Id).ToList(),
                Strict = false,
                Side = "General",
                RelativePosition = "General",
                IsSingletonMechanism = false
            });
        }

        return new FurnitureAnalysis
        {
            FurnitureType = furnitureType,
            GeometryDescription = geometryDesc,
            Confidence = localContract.LocalConfidence,
            Components = components,
            MainMaterials = mainMaterials,
            MainColors = mainColors,
            AccentColors = accentColors,
            ColorLocations = colorLocations,
            Doors = doors,
            Drawers = drawers,
            Shelves = new List<string>(),
            Handles = handles,
            GlassAreas = glassAreas,
            Panels = new List<string> { "structural carcase panels" },
            DecorativeElements = new List<string>(),
            StructuralElements = new List<string> { "furniture carcase", "structural panel candidates" },
            ProtectedFeatures = protectedFeatures,
            ProtectedRegions = protectedRegions,
            ComponentCardinalityConstraints = cardinality,
            HardwareAbsenceConstraints = hardwareAbsences,
            SceneColorReferences = localContract.SceneColorReferences,
            ProtectedAbsenceRegions = localContract.ProtectedAbsenceRegions,
            PersistentSceneDetails = localContract.PersistentSceneDetails,
            NegativeSpaceRegions = localContract.NegativeSpaceRegions.ToList(),
            UncertainFeatures = uncertainFeatures,
            PhotoDefects = new List<string>(),
            SceneObjects = sceneObjects,
            RemovableClutter = clutter.Distinct().ToList(),
            FurnitureEnvelopeAuthority = localContract.FurnitureEnvelopeAuthority,
            AuthorityRecords = localContract.AuthorityRecords,
            Conflicts = localContract.Conflicts,
            RawAnalysisJson = "{\"source\": \"LocalToFurnitureAnalysisAdapter\"}"
        };
    }

    private static string GetColorName(LocalColorRegion region)
    {
        var lab = region.DominantLab;
        if (lab.L > 80 && Math.Sqrt(lab.A * lab.A + lab.B * lab.B) < 18) return "white";
        if (lab.L < 25) return "black";
        if (Math.Sqrt(lab.A * lab.A + lab.B * lab.B) < 12) return "grey";
        if (lab.B > 15 && lab.A > -5) return "wood/brown tone";
        if (lab.B < -10) return "blue tone";
        if (lab.A < -10) return "green tone";
        if (lab.A > 15) return "red/warm tone";
        return region.RepresentativeRgb;
    }
}
