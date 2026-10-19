using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Analysis;

/// <summary>
/// Stage-A canonicalization adapter. It deliberately reuses the existing
/// local-to-provider projection instead of reimplementing its authority and
/// cardinality rules.
/// </summary>
public sealed class FurnitureAnalysisContractAdapter : IFurnitureAnalysisContractAdapter
{
    private readonly ILocalToFurnitureAnalysisAdapter _localAdapter;

    public FurnitureAnalysisContractAdapter(ILocalToFurnitureAnalysisAdapter localAdapter)
    {
        _localAdapter = localAdapter ?? throw new ArgumentNullException(nameof(localAdapter));
    }

    /// <summary>
    /// Compatibility constructor for focused tests and legacy tools only.
    /// Production composition injects the existing local adapter.
    /// </summary>
    public FurnitureAnalysisContractAdapter()
        : this(new LocalToFurnitureAnalysisAdapter())
    {
    }

    public FurnitureAnalysisContract FromLocal(LocalFurnitureContract localContract)
    {
        ArgumentNullException.ThrowIfNull(localContract);

        var projected = _localAdapter.Adapt(localContract);
        var contract = FromProvider(projected, localContract.SourceFileName, localContract.SourceSha256, AnalysisSource.Local);

        // Carry only explicitly authorized, source-side cleanup targets into
        // the canonical contract. A generic IsRemovable interpretation is not
        // enough to override source authority during GPT correction.
        var strictCleanupAuthority = localContract.AuthorityRecords
            .Where(record => record.Authority == ConstraintAuthority.StrictCleanupAllowed)
            .Select(record => record.ConstraintId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cleanupTargets = projected.SceneObjects
            .Where(sceneObject => sceneObject.IsRemovable &&
                                  sceneObject.Region != null &&
                                  strictCleanupAuthority.Contains(sceneObject.Id))
            .Select(sceneObject => new CleanupTargetRecord
            {
                CleanupTargetId = sceneObject.Id,
                Region = NormalizedRectangle.FromProtectedRegion(sceneObject.Region!),
                Authority = ConstraintAuthority.StrictCleanupAllowed,
                ExpectedRemoval = true,
                Details = sceneObject.Description
            })
            .ToList();

        return contract with
        {
            FurnitureRegions = localContract.FurnitureRegionCandidates.ToList(),
            FurnitureBounds = localContract.FurnitureBounds,
            StructuralRegions = localContract.StructuralRegions.ToList(),
            Hardware = localContract.HardwareCandidates.ToList(),
            MaterialHints = localContract.MaterialHints.ToList(),
            ColorRegions = localContract.ColorRegions.ToList(),
            SceneObjects = projected.SceneObjects.ToList(),
            CleanupTargets = cleanupTargets,
            ProtectedAbsenceRegions = localContract.ProtectedAbsenceRegions.ToList(),
            NegativeSpaceRegions = localContract.NegativeSpaceRegions.ToList(),
            PersistentSceneDetails = localContract.PersistentSceneDetails.ToList(),
            SceneColorReferences = localContract.SceneColorReferences.ToList(),
            ComponentCardinalityConstraints = projected.ComponentCardinalityConstraints.ToList(),
            HardwareAbsenceConstraints = projected.HardwareAbsenceConstraints.ToList(),
            FurnitureEnvelopeAuthority = localContract.FurnitureEnvelopeAuthority,
            AuthorityRecords = localContract.AuthorityRecords.ToList(),
            Conflicts = localContract.Conflicts.ToList(),
            ConfidenceMetadata = new ContractConfidenceMetadata
            {
                Overall = localContract.LocalConfidence,
                Furniture = localContract.FurnitureConfidence,
                Structure = AverageOrZero(localContract.StructuralRegions.Select(r => r.Confidence)),
                Components = AverageOrZero(localContract.HardwareCandidates.Select(h => h.Confidence)),
                Scene = AverageOrZero(localContract.SceneObjects.Select(o => o.Confidence)),
                Materials = AverageOrZero(localContract.MaterialHints.Select(m => m.Confidence)),
                Colors = AverageOrZero(localContract.ColorRegions.Select(c => c.Confidence)),
                EvidenceSummary = $"Local analysis status: {localContract.Status}; authority policy: {localContract.AuthorityPolicyVersion}."
            }
        };
    }

    public FurnitureAnalysisContract FromProvider(
        FurnitureAnalysis analysis,
        string sourceFileName = "",
        string sourceSha256 = "")
    {
        return FromProvider(analysis, sourceFileName, sourceSha256, AnalysisSource.GeminiFallback);
    }

    public FurnitureAnalysis ToLegacy(FurnitureAnalysisContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        return new FurnitureAnalysis
        {
            FurnitureType = contract.FurnitureType,
            GeometryDescription = contract.GeometryDescription,
            Confidence = contract.Confidence,
            Components = contract.Components.ToList(),
            ProtectedRegions = contract.ProtectedRegions.ToList(),
            MainMaterials = contract.MainMaterials.ToList(),
            MainColors = contract.MainColors.ToList(),
            AccentColors = contract.AccentColors.ToList(),
            ColorLocations = new Dictionary<string, string>(contract.ColorLocations),
            Doors = contract.Doors.ToList(),
            Drawers = contract.Drawers.ToList(),
            Shelves = contract.Shelves.ToList(),
            Handles = contract.Handles.ToList(),
            GlassAreas = contract.GlassAreas.ToList(),
            Panels = contract.Panels.ToList(),
            DecorativeElements = contract.DecorativeElements.ToList(),
            StructuralElements = contract.StructuralElements.ToList(),
            ProtectedFeatures = contract.ProtectedFeatures.ToList(),
            ComponentCardinalityConstraints = contract.ComponentCardinalityConstraints.ToList(),
            HardwareAbsenceConstraints = contract.HardwareAbsenceConstraints.ToList(),
            SceneColorReferences = contract.SceneColorReferences.ToList(),
            ProtectedAbsenceRegions = contract.ProtectedAbsenceRegions.ToList(),
            PersistentSceneDetails = contract.PersistentSceneDetails.ToList(),
            NegativeSpaceRegions = contract.NegativeSpaceRegions.ToList(),
            UncertainFeatures = contract.UncertainFeatures.ToList(),
            PhotoDefects = contract.PhotoDefects.ToList(),
            FramingAnalysis = contract.Framing,
            SceneObjects = contract.SceneObjects.ToList(),
            RemovableClutter = contract.RemovableClutter.ToList(),
            FurnitureEnvelopeAuthority = contract.FurnitureEnvelopeAuthority,
            AuthorityRecords = contract.AuthorityRecords.ToList(),
            Conflicts = contract.Conflicts.ToList()
        };
    }

    private FurnitureAnalysisContract FromProvider(
        FurnitureAnalysis analysis,
        string sourceFileName,
        string sourceSha256,
        AnalysisSource source)
    {
        var cleanupTargets = analysis.SceneObjects
            .Where(o => o.IsRemovable && o.Region != null)
            .Select(o => new CleanupTargetRecord
            {
                CleanupTargetId = o.Id,
                Region = NormalizedRectangle.FromProtectedRegion(o.Region!),
                Authority = ConstraintAuthority.AdvisoryCleanupAllowed,
                ExpectedRemoval = true,
                Details = o.Description
            })
            .ToList();

        return new FurnitureAnalysisContract
        {
            SourceIdentity = new CanonicalSourceIdentity
            {
                FileName = sourceFileName,
                Sha256 = sourceSha256,
                Authority = ConstraintAuthority.Strict
            },
            AnalysisSource = source,
            FurnitureType = analysis.FurnitureType,
            GeometryDescription = analysis.GeometryDescription,
            Confidence = analysis.Confidence,
            Components = analysis.Components.ToList(),
            ProtectedRegions = analysis.ProtectedRegions.ToList(),
            MainMaterials = analysis.MainMaterials.ToList(),
            MainColors = analysis.MainColors.ToList(),
            AccentColors = analysis.AccentColors.ToList(),
            ColorLocations = new Dictionary<string, string>(analysis.ColorLocations),
            Doors = analysis.Doors.ToList(),
            Drawers = analysis.Drawers.ToList(),
            Shelves = analysis.Shelves.ToList(),
            Handles = analysis.Handles.ToList(),
            GlassAreas = analysis.GlassAreas.ToList(),
            Panels = analysis.Panels.ToList(),
            DecorativeElements = analysis.DecorativeElements.ToList(),
            StructuralElements = analysis.StructuralElements.ToList(),
            ProtectedFeatures = analysis.ProtectedFeatures.ToList(),
            ComponentCardinalityConstraints = analysis.ComponentCardinalityConstraints.ToList(),
            HardwareAbsenceConstraints = analysis.HardwareAbsenceConstraints.ToList(),
            SceneColorReferences = analysis.SceneColorReferences.ToList(),
            ProtectedAbsenceRegions = analysis.ProtectedAbsenceRegions.ToList(),
            PersistentSceneDetails = analysis.PersistentSceneDetails.ToList(),
            NegativeSpaceRegions = analysis.NegativeSpaceRegions.ToList(),
            UncertainFeatures = analysis.UncertainFeatures.ToList(),
            PhotoDefects = analysis.PhotoDefects.ToList(),
            Framing = analysis.FramingAnalysis,
            SceneObjects = analysis.SceneObjects.ToList(),
            CleanupTargets = cleanupTargets,
            RemovableClutter = analysis.RemovableClutter.ToList(),
            FurnitureEnvelopeAuthority = analysis.FurnitureEnvelopeAuthority,
            AuthorityRecords = analysis.AuthorityRecords.ToList(),
            Conflicts = analysis.Conflicts.ToList(),
            ConfidenceMetadata = new ContractConfidenceMetadata
            {
                Overall = analysis.Confidence,
                EvidenceSummary = $"Analysis source: {source}."
            }
        };
    }

    private static double AverageOrZero(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0.0 : list.Average();
    }
}
