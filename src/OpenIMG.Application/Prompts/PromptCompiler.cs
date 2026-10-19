using System.Globalization;
using System.Text;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Application.Configuration;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Prompts;

public class PromptCompiler
{
    public const string LegacyVersion = "v1.0";
    public const string CurrentAnalysisVersion = "analysis-v4";
    public const string CurrentRestorationVersion = "restoration-v4";
    public const string CurrentValidationVersion = "validation-v4";

    public static string BuildAnalysisPrompt(string version = CurrentAnalysisVersion)
    {
        if (version == "v1.0")
        {
            return BuildLegacyAnalysisPrompt(version);
        }

        return $@"Analyze this photograph of a real existing piece of furniture.
Prompt Version: {version}

CRITICAL RULES:
1. This is an ANALYSIS ONLY task.
2. DO NOT edit the image.
3. DO NOT generate an image.
4. DO NOT redesign or reimagine anything.
5. The original photograph is the ONLY source of truth.
6. Do not infer details that are not visible.
7. If something cannot be determined confidently, mark it under 'uncertainFeatures' or 'uncertain' instead of guessing.
8. Pay particular attention to distinguishing structural colors from small accent colors.
9. Region Precedence:
   FurnitureHardware > FurnitureStructural > FurnitureMaterialSensitive > FurnitureColorSensitive > FurnitureGlass > FurnitureDecorative > Uncertain > PermanentEnvironment > RemovableScene.
   Furniture protection ALWAYS wins over removal permission. If a removable object overlaps protected furniture, the overlap must become protected or uncertain.
10. Return STRICT JSON ONLY. Do not enclose in markdown code fences. Do not output conversational text.

The JSON must follow this exact schema:
{{
  ""furnitureType"": ""string (e.g. kitchen cabinet, closet, island, crib, desk)"",
  ""geometryDescription"": ""detailed description of shapes, proportions, and layout"",
  ""confidence"": 0.95,
  ""mainMaterials"": [""melamine"", ""solid wood"", ""glass"", ""aluminum"", ""quartz"", ""marble""],
  ""mainColors"": [""white"", ""dark walnut""],
  ""accentColors"": [""brushed aluminum"", ""black""],
  ""colorLocations"": {{
    ""dark walnut"": ""lower drawer fronts and island carcase"",
    ""white"": ""upper cabinet doors and quartz countertop""
  }},
  ""doors"": [""4 upper glossy white cabinet doors""],
  ""drawers"": [""4 lower drawers with integrated channel pulls""],
  ""shelves"": [""open corner shelf in dark walnut""],
  ""handles"": [""integrated gola profile channel pulls""],
  ""glassAreas"": [""none""],
  ""panels"": [""waterfall countertop end panels"", ""vertical divider panels""],
  ""decorativeElements"": [""ceiling cove moulding""],
  ""structuralElements"": [""island base"", ""cabinet carcase""],
  ""components"": [
    {{
      ""id"": ""island_base"",
      ""parentComponentId"": null,
      ""type"": ""structural_panel"",
      ""description"": ""Waterfall island base structure"",
      ""color"": ""dark walnut"",
      ""material"": ""melamine"",
      ""isStructural"": true,
      ""importance"": ""critical"",
      ""confidence"": 0.98,
      ""region"": {{
        ""x"": 0.1,
        ""y"": 0.5,
        ""width"": 0.45,
        ""height"": 0.4,
        ""regionType"": ""structural_panel"",
        ""description"": ""Island front panel"",
        ""importance"": 1.0
      }}
    }}
  ],
  ""protectedFeatures"": [
    ""waterfall marble edge on both sides of island"",
    ""integrated horizontal channel pulls on drawers""
  ],
  ""protectedRegions"": [
    {{
      ""x"": 0.05,
      ""y"": 0.15,
      ""width"": 0.9,
      ""height"": 0.8,
      ""regionType"": ""furniture_silhouette"",
      ""description"": ""Entire furniture structural envelope"",
      ""importance"": 1.0
    }}
  ],
  ""sceneObjects"": [
    {{
      ""id"": ""bottle_soda"",
      ""description"": ""2L plastic soda bottle on countertop"",
      ""classification"": ""RemovableClutter"",
      ""precedence"": ""RemovableScene"",
      ""occlusion"": ""SceneOnly"",
      ""region"": {{
        ""x"": 0.8,
        ""y"": 0.48,
        ""width"": 0.05,
        ""height"": 0.12,
        ""regionType"": ""clutter"",
        ""description"": ""Soda bottle"",
        ""importance"": 0.1
      }},
      ""overlapsFurniture"": false,
      ""isRemovable"": true,
      ""confidence"": 0.99
    }}
  ],
  ""removableClutter"": [""bottles on counter"", ""tools on right counter edge"", ""debris on floor""],
  ""framingAnalysis"": {{
    ""isFurnitureCropped"": false,
    ""cropSeverity"": ""None"",
    ""cropEdges"": [],
    ""missingContextType"": ""SceneOnly"",
    ""safeZoomOutRecommended"": false,
    ""recommendedExpansion"": {{
      ""topPercent"": 0.0,
      ""bottomPercent"": 0.0,
      ""leftPercent"": 0.0,
      ""rightPercent"": 0.0
    }},
    ""framingConfidence"": 0.95
  }},
  ""uncertainFeatures"": [],
  ""photoDefects"": [""yellow color cast"", ""digital sensor noise""]
}}";
    }

    private static string BuildLegacyAnalysisPrompt(string version)
    {
        return $@"Analyze this photograph of a real existing piece of furniture.
Prompt Version: {version}

CRITICAL RULES:
1. This is an ANALYSIS ONLY task.
2. DO NOT edit the image.
3. DO NOT generate an image.
4. DO NOT redesign or reimagine anything.
5. The original photograph is the ONLY source of truth.
6. Do not infer details that are not visible.
7. If something cannot be determined confidently, mark it under 'uncertainFeatures' instead of guessing.
8. Pay particular attention to distinguishing structural colors from small accent colors.
9. Return STRICT JSON ONLY. Do not enclose in markdown code fences. Do not output conversational text.

The JSON must follow this exact schema:
{{
  ""furnitureType"": ""string (e.g. kitchen cabinet, closet, crib, desk)"",
  ""geometryDescription"": ""detailed description of shapes, proportions, and layout"",
  ""confidence"": 0.95,
  ""mainMaterials"": [""melamine"", ""solid wood"", ""glass"", ""aluminum""],
  ""mainColors"": [""white"", ""light grey""],
  ""accentColors"": [""blue"", ""chrome""],
  ""colorLocations"": {{
    ""white"": ""main structural panels, doors, and drawer fronts"",
    ""blue"": ""small decorative accent strips on top trim only""
  }},
  ""doors"": [""2 upper cabinet doors, melamine, white""],
  ""drawers"": [""3 lower drawers, white faces, aluminum pulls""],
  ""shelves"": [""open center shelf, white melamine""],
  ""handles"": [""horizontal bar aluminum pulls on all doors and drawers""],
  ""glassAreas"": [""none""],
  ""panels"": [""left side structural panel, right side structural panel, top crown""],
  ""decorativeElements"": [""blue accent trim stripe on upper moulding""],
  ""structuralElements"": [""vertical dividing carcase panels, kickplate""],
  ""components"": [
    {{
      ""id"": ""left_panel"",
      ""parentComponentId"": null,
      ""type"": ""structural_panel"",
      ""description"": ""Left vertical outer cabinet panel"",
      ""color"": ""white"",
      ""material"": ""melamine"",
      ""isStructural"": true,
      ""importance"": ""critical"",
      ""confidence"": 0.98,
      ""region"": {{
        ""x"": 0.05,
        ""y"": 0.15,
        ""width"": 0.08,
        ""height"": 0.75,
        ""regionType"": ""structural_panel"",
        ""description"": ""Left outer wall"",
        ""importance"": 1.0
      }}
    }}
  ],
  ""protectedFeatures"": [
    ""number of doors: exactly 2"",
    ""number of drawers: exactly 3"",
    ""blue color must be strictly confined to upper trim""
  ],
  ""protectedRegions"": [
    {{
      ""x"": 0.0,
      ""y"": 0.0,
      ""width"": 1.0,
      ""height"": 1.0,
      ""regionType"": ""overall"",
      ""description"": ""entire furniture silhouette"",
      ""importance"": 1.0
    }}
  ],
  ""uncertainFeatures"": [""exact drawer slide mechanism not visible""],
  ""photoDefects"": [""yellow color cast"", ""motion blur on lower corner"", ""digital noise""]
}}";
    }

    /// <summary>
    /// Canonical product entry point. The explicit adapter keeps the staged
    /// migration visible and preserves the existing prompt output contract.
    /// </summary>
    public static string BuildRestorationPrompt(
        FurnitureAnalysisContract contract,
        IFurnitureAnalysisContractAdapter projection,
        int attemptNumber = 1,
        string? failureFeedback = null,
        string version = CurrentRestorationVersion,
        OpenImgOptions? options = null,
        AnalysisSource analysisSource = AnalysisSource.GeminiFallback,
        List<LocalPromptAssembly>? assemblies = null,
        List<string>? individualCriticalRegionIds = null,
        PhotographicRestorationIntent? intent = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(projection);

        return BuildRestorationPrompt(
            projection.ToLegacy(contract),
            attemptNumber,
            failureFeedback,
            version,
            options,
            analysisSource,
            assemblies,
            individualCriticalRegionIds,
            intent);
    }

    public static string BuildRestorationPrompt(
        FurnitureAnalysis analysis,
        int attemptNumber = 1,
        string? failureFeedback = null,
        string version = CurrentRestorationVersion,
        OpenImgOptions? options = null,
        AnalysisSource analysisSource = AnalysisSource.GeminiFallback,
        List<LocalPromptAssembly>? assemblies = null,
        List<string>? individualCriticalRegionIds = null,
        PhotographicRestorationIntent? intent = null)
    {
        if (version == "v1.0")
        {
            return BuildLegacyRestorationPrompt(analysis, attemptNumber, failureFeedback, version);
        }

        // RestorationProfile is a legacy setting and must not override the
        // independent photographic, cleanup, or person-removal policies.
        var isHighQualityConservative = options?.PhotographicEnhancementProfile == PhotographicEnhancementProfile.Strong;
        var isConservative = options == null || options.PhotographicEnhancementProfile == PhotographicEnhancementProfile.Light;

        var sb = new StringBuilder();

        if (isHighQualityConservative)
        {
            sb.AppendLine("TASK: STRONG PHOTOGRAPHIC RESTORATION WITH STRICT STRUCTURAL FIDELITY.");
        }
        else
        {
            sb.AppendLine("TASK: PHOTOGRAPHIC RESTORATION ONLY - NOT FURNITURE REDESIGN.");
        }
        sb.AppendLine("Generate and output a restored photographic image based on the supplied original photograph and the strict physical constraints below.");
        sb.AppendLine("Do not reply with textual editing instructions or advice. The expected output is the restored image.");
        sb.AppendLine();
        sb.AppendLine($"Prompt Version: {version}");
        sb.AppendLine($"Attempt: {attemptNumber}");
        sb.AppendLine();
        sb.AppendLine("CRITICAL MANDATE — ORIGINAL AUTHORITY FOR PERMANENT PHYSICAL TRUTH:");
        sb.AppendLine("The source photograph is the highest-authority reference for permanent furniture, hardware, fixtures, and architecture. Structured local analysis is supplemental.");
        sb.AppendLine("Apply the separate scene-cleanup and person-removal policies below; do not restore temporary clutter merely because it appears in the source.");
        sb.AppendLine("If a structured constraint conflicts with clearly visible permanent physical evidence, preserve the source-supported structure.");
        sb.AppendLine("Semantic naming, color tags, or bounding coordinates must NEVER override visible source appearance.");
        sb.AppendLine();

        if (isHighQualityConservative)
        {
            sb.AppendLine("RESTORATION PROFILE: HighQualityConservative (StructuralFidelity=VeryStrict, PhotographicEnhancement=Strong)");
            sb.AppendLine("OPERATING PRINCIPLE: Aggressive photographic restoration. Conservative physical interpretation. Zero furniture redesign.");
            sb.AppendLine();
            sb.AppendLine("OBJECTIVE:");
            sb.AppendLine("- Perform a strong photographic restoration, not a minimal edit.");
            sb.AppendLine("- Substantially improve visible image quality while preserving the exact physical furniture and scene structure.");
            sb.AppendLine("- The final image should look like a much better, professional modern photograph of the exact same real furniture installation.");
            sb.AppendLine("- The improvement should be immediately noticeable side-by-side with the original.");
            sb.AppendLine("- Do not return a nearly unchanged copy when the source has blur, haze, low contrast, poor white balance, or weak color.");
            sb.AppendLine();
            sb.AppendLine("STRONG PHOTOGRAPHIC QUALITY IMPROVEMENTS REQUIRED:");
            sb.AppendLine("- Sharpening & Clarity: Strongly improve perceived sharpness, optical clarity, tonal separation, and edge definition. Recover blur/haze without halos. For blurry/hazy scenes, recover macro clarity and contrast—do NOT invent unsupported micro-detail (screws, seams, textures, hardware, or panel lines).");
            sb.AppendLine("- Exposure & Dynamic Range: Normalize exposure, expand dynamic range, recover highlights, open shadows, and enhance local and global contrast without clipping.");
            sb.AppendLine("- Cleanliness & Artifact Removal: Eliminate camera sensor noise, compression artifacts, chromatic aberration, and atmospheric haze.");
            sb.AppendLine("- White Balance & Color Restoration: Restore and revive authentic colors—do NOT recolor. White melamine with yellow cast -> clean natural white; dark brown -> richer, clearer dark brown; warm beige wall -> brighter, cleaner warm beige; woodgrain -> clearer woodgrain in same hue family. Do not flatten warm surfaces into neutral grey.");
            sb.AppendLine();
            sb.AppendLine("COLOR RESTORATION & ILLUMINATION POLICY:");
            sb.AppendLine("- Surface Color Correction: Restore surfaces toward clean, neutral color. Correct aged or yellow casts.");
            sb.AppendLine("- Designed Color Preservation: Preserve original color identity and location. Brown wood must remain brown wood; black trim must remain black trim; white panels must remain white/off-white.");
            sb.AppendLine("- Illumination Color Separation: Emitted light color redesign is forbidden. Preserve intentional warm illumination (e.g. warm LED strips, cove lighting, amber decorative illumination).");
            sb.AppendLine();
            sb.AppendLine("SAFE GLOBAL PERSPECTIVE & STRAIGHTENING POLICY:");
            sb.AppendLine("- Permitted: Mild horizon leveling, mild perspective straightening, and minor keystone correction as a single global photographic transformation. Keep furniture proportions consistent. No substantial cropping of furniture, no outpainting, no inferred hidden structure.");
            sb.AppendLine();
            sb.AppendLine("ABSOLUTE STRUCTURAL FIDELITY (ZERO FURNITURE REDESIGN):");
            sb.AppendLine("- Do NOT add furniture components.");
            sb.AppendLine("- Do NOT remove furniture components.");
            sb.AppendLine("- Do NOT alter panel divisions, cabinet dimensions, or geometry.");
            sb.AppendLine("- Do NOT move doors, drawers, shelves, handles, hinges, or rails.");
            sb.AppendLine("- Materials must retain real identity (wood, melamine, stone, metal). Reveal texture clearly without inventing new patterns.");
            sb.AppendLine("- SUMMARY: Same furniture, much better photograph.");
            sb.AppendLine();

            var safeClutter = analysis.SceneObjects
                .Where(s => s.IsRemovable && s.Classification == SceneClassification.RemovableClutter && s.Occlusion == OcclusionType.SceneOnly && s.Confidence >= 0.65)
                .ToList();
            bool hasClutterStrings = analysis.RemovableClutter != null && analysis.RemovableClutter.Count > 0;

            if (safeClutter.Count > 0 || hasClutterStrings)
            {
                sb.AppendLine("LOCAL DETECTOR EVIDENCE (SUPPORTING ONLY; NOT THE CLEANUP GATE):");
                var clutterDetails = safeClutter.Count > 0
                    ? string.Join(", ", safeClutter.Select(c => c.Description))
                    : string.Join(", ", analysis.RemovableClutter!);
                sb.AppendLine($"- High-confidence removable-object candidates identified: {clutterDetails}.");
                sb.AppendLine();
            }

            if (intent != null && intent.HasAnyEnhancementRequested)
            {
                sb.AppendLine("SPECIFIC IMAGE-DEFECT RESTORATION INTENT (FROM LOCAL ASSESSMENT):");
                if (intent.DeblurStrength != RestorationIntensity.None)
                    sb.AppendLine($"- Deblur & Clarity: {intent.DeblurStrength} recovery requested to address source blur.");
                if (intent.DehazeStrength != RestorationIntensity.None)
                    sb.AppendLine($"- Dehazing & Veil: {intent.DehazeStrength} reduction requested to remove atmospheric haze.");
                if (intent.ContrastRecovery != RestorationIntensity.None)
                    sb.AppendLine($"- Contrast Recovery: {intent.ContrastRecovery} recovery requested to expand tonal range.");
                if (intent.ColorRecovery != RestorationIntensity.None)
                    sb.AppendLine($"- Color Revival: {intent.ColorRecovery} correction requested to revive genuine material colors.");
                if (intent.ExposureRecovery != RestorationIntensity.None)
                    sb.AppendLine($"- Exposure Balance: {intent.ExposureRecovery} normalization requested.");
                if (intent.PerspectiveCorrection != "None")
                    sb.AppendLine($"- Perspective Alignment: {intent.PerspectiveCorrection} straightening permitted.");
                sb.AppendLine();
            }
        }
        else if (isConservative)
        {
            sb.AppendLine("This is strict photographic restoration.");
            sb.AppendLine();
            sb.AppendLine("Preserve permanent furniture, architecture, and fixtures exactly. Apply only the separately specified cleanup policies below.");
            sb.AppendLine();
            sb.AppendLine("Do not add, replace, reconstruct, relocate, or redesign any permanent physical object, furniture component, hardware element, decoration, or environmental detail.");
            sb.AppendLine();
            sb.AppendLine("Do not zoom out.");
            sb.AppendLine("Do not outpaint.");
            sb.AppendLine("Do not extend the image beyond the original frame.");
            sb.AppendLine("Do not change crop, framing, camera perspective, or composition.");
            sb.AppendLine();
            sb.AppendLine("Only improve photographic quality.");
            sb.AppendLine();
        }

        AppendSceneAndPersonCleanupPolicy(sb, analysis, options);

        if (analysisSource == AnalysisSource.Local)
        {
            sb.AppendLine("LOCAL-FIRST RESTORATION SAFETY DIRECTIVES (SOURCE: LOCAL ANALYSIS ENGINE):");
            sb.AppendLine("This restoration contract was derived from conservative local machine-vision analysis with bounded semantic certainty.");
            sb.AppendLine("The model MUST adhere to the following heightened conservative safety constraints:");
            sb.AppendLine("1. PRESERVE UNCERTAINTY WITHOUT RECONSTRUCTION: Any structural candidate, panel division, or feature labeled with uncertainty or candidate status MUST NOT be re-imagined, reconstructed, or redesigned. Preserve the exact physical structure without generative redesign.");
            sb.AppendLine("2. PRESERVE GENERIC STRUCTURAL REGIONS: Generic structural panels, horizontal/vertical divisions, and candidate doors/drawers must remain exactly in place with no alteration to their count, proportions, alignment, or boundary lines.");
            sb.AppendLine("3. STRICT ACCENT COLOR CONFINEMENT: Localized accent colors and dominant tones must remain strictly confined to their observed positions. Do NOT bleed, spread, or generalize accent colors onto adjacent panels or furniture bodies.");
            sb.AppendLine("4. UNIDENTIFIED HARDWARE PRESERVATION: Unidentified hardware candidates (pulls, knobs, hinges, fasteners) must remain intact and unchanged at their original coordinates. Do NOT replace, add, remove, or modernize hardware.");
            sb.AppendLine("5. SCENE OBJECT PRESERVATION: Permanent background scene objects, room elements, and floor/wall surfaces must remain architecturally unchanged.");
            sb.AppendLine("6. CAMERA FRAMING & PERSPECTIVE: The camera angle, perspective, framing, and aspect ratio must remain exactly as in the original photograph. Absolutely no zooming out, reframing, or outpainting.");
            sb.AppendLine();
        }

        sb.AppendLine("FURNITURE PRESERVATION DIRECTIVE (ABSOLUTE PRIORITY):");
        sb.AppendLine("The original photograph is the absolute, immutable source of truth.");
        sb.AppendLine("Preserve exactly the real furniture geometry, dimensions, proportions, perspective, component layout, materials, hardware, colors, accent-color placement, glass extents, wood grain identity, stone/marble pattern identity, and all structural details.");
        sb.AppendLine("If there is tension between visual beauty and furniture fidelity, furniture fidelity ALWAYS wins.");
        sb.AppendLine();

        // ---------------------------------------------------------
        // SECTION 1: STRICT VERIFIED CONSTRAINTS
        // ---------------------------------------------------------
        sb.AppendLine("============================================================");
        sb.AppendLine("STRICT VERIFIED CONSTRAINTS (ABSOLUTE FIDELITY REQUIRED):");
        sb.AppendLine("============================================================");
        sb.AppendLine("1. ABSOLUTE STRUCTURAL FIDELITY (ZERO FURNITURE REDESIGN):");
        sb.AppendLine("   - Do NOT add furniture components, new frames, metal structures, braces, supports, or inferred architecture.");
        sb.AppendLine("   - Do NOT remove furniture components.");
        sb.AppendLine("   - Do NOT alter panel divisions, cabinet dimensions, or geometry.");
        sb.AppendLine("   - Do NOT move doors, drawers, shelves, handles, hinges, or rails.");
        sb.AppendLine("   - Materials must retain real identity (wood, melamine, stone, metal). Reveal texture clearly without inventing new patterns.");
        sb.AppendLine("   - NO SYMMETRY COMPLETION: The source may be intentionally asymmetric. Never mirror, duplicate, complete, or infer a component merely because a matching component appears on the other side. Observed asymmetry is authoritative: if only one hardware/support mechanism is observed, preserve exactly one. Do not invent a symmetric counterpart.");
        sb.AppendLine();

        var cardinalityConstraints = analysis.ComponentCardinalityConstraints.Count > 0
            ? analysis.ComponentCardinalityConstraints
            : DeriveCardinalityFromAnalysis(analysis);
        if (cardinalityConstraints.Count > 0)
        {
            sb.AppendLine("2. EXACT COMPONENT COUNT & HARDWARE ASYMMETRY PRESERVATION:");
            foreach (var c in cardinalityConstraints.Where(x => x.Strict))
            {
                if (c.IsSingletonMechanism)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "- Exactly 1 {0} is visually confirmed in [{1:F2},{2:F2},{3:F2},{4:F2}] on the {5} side. There is NO corresponding mechanism on the opposite side. Preserve count = 1. Do not mirror, duplicate, complete, infer, or symmetrize this hardware mechanism.",
                        c.Category, c.Region.X, c.Region.Y, c.Region.Width, c.Region.Height, c.Side));
                }
                else
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "- Exactly {0} observed {1} component(s) exist in [{2:F2},{3:F2},{4:F2},{5:F2}]. Preserve exactly {0}; do not add, duplicate, mirror, complete, or remove them.",
                        c.DeduplicatedCount, c.Category, c.Region.X, c.Region.Y, c.Region.Width, c.Region.Height));
                }
            }
            sb.AppendLine();
        }

        if (analysis.HardwareAbsenceConstraints.Count > 0)
        {
            sb.AppendLine("3. NEGATIVE HARDWARE ABSENCE PRESERVATION:");
            sb.AppendLine("Regions visually confirmed to have NO hardware mechanism must remain completely free of hardware. Do not invent or mirror hardware mechanisms across bilateral axes.");
            foreach (var h in analysis.HardwareAbsenceConstraints.Where(x => x.Strict))
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "- Region [{0:F2},{1:F2},{2:F2},{3:F2}] on the {4} side is visually confirmed EMPTY of any hardware mechanism (Observed count = 0). Do not invent, mirror, add, or complete any gas strut, support arm, hinge, or hardware mechanism in this region.",
                    h.Region.X, h.Region.Y, h.Region.Width, h.Region.Height, h.Side));
            }
            sb.AppendLine();
        }

        if (analysis.ProtectedAbsenceRegions.Count > 0)
        {
            sb.AppendLine("4. NEGATIVE SPACE PRESERVATION:");
            sb.AppendLine("Regions identified as non-furniture/open scene must remain non-furniture/open scene. Do not extend the upper cabinet downward. Do not add: lower cabinet, shelf, panel, drawer, door, furniture volume into protected open space.");
            foreach (var a in analysis.ProtectedAbsenceRegions.Where(x => x.Strict))
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "- Region [{0:F2},{1:F2},{2:F2},{3:F2}] is verified {4}. Keep it non-furniture; do not extend a cabinet or add panels, shelves, drawers, doors, hardware, or any furniture volume there.",
                    a.Region.X, a.Region.Y, a.Region.Width, a.Region.Height, a.AbsenceType));
            }
            sb.AppendLine();
        }

        var strictColors = analysis.SceneColorReferences
            .Where(x => x.Protected && x.Authority == ConstraintAuthority.Strict && !x.ColorConflictDetected && !x.HueFamily.Equals("observed_surface_tone", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (strictColors.Count > 0)
        {
            sb.AppendLine("5. PERMANENT SCENE SURFACE COLOR PRESERVATION: [STRICT VERIFIED CONSTRAINTS]");
            sb.AppendLine("Permanent background surfaces (such as walls and ceilings) have an established real color identity. While photographic exposure, dynamic range, and clarity should be enhanced, their intrinsic color character must remain faithful.");
            foreach (var sc in strictColors)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "- The {0} at [{1:F2},{2:F2},{3:F2},{4:F2}] is a permanent {5} ({6} tone) surface. You may improve photographic exposure, cleanliness, and clarity, but PRESERVE its {6} color identity. STRICT PROHIBITION: Do NOT neutralize, bleach, or alter this {7} surface into an incorrect or neutralized color. The original wall color character must remain unmistakably {5}.",
                    sc.Role, sc.Region.X, sc.Region.Y, sc.Region.Width, sc.Region.Height, sc.HueFamily, sc.WarmCoolBias, sc.WarmCoolBias.ToLowerInvariant()));
            }
            sb.AppendLine();
        }

        if (analysis.PersistentSceneDetails.Count > 0)
        {
            sb.AppendLine("6. PERSISTENT SCENE DETAIL PRESERVATION:");
            sb.AppendLine("Small real physical scene details identified near the furniture must remain present. Do not remove or paint over wall-mounted details or controls merely because they are visually small.");
            foreach (var d in analysis.PersistentSceneDetails.Where(x => x.Strict))
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "- Preserve the visually distinct real scene detail at [{0:F2},{1:F2},{2:F2},{3:F2}]. Do not remove, smooth away, or replace it.",
                    d.Region.X, d.Region.Y, d.Region.Width, d.Region.Height));
            }
            sb.AppendLine();
        }

        sb.AppendLine("7. FURNITURE CONTRACT TO PRESERVE EXACTLY:");
        if (analysis.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict && !string.IsNullOrWhiteSpace(analysis.FurnitureType))
            sb.AppendLine($"- Furniture Type: {analysis.FurnitureType}");

        if (analysis.FurnitureEnvelopeAuthority == ConstraintAuthority.Strict)
        {
            if (!string.IsNullOrWhiteSpace(analysis.GeometryDescription))
                sb.AppendLine($"- Geometry & Layout: {analysis.GeometryDescription}");
        }
        else
        {
            sb.AppendLine("- Geometry & Extent: Do not infer furniture outside clearly observed structural regions; use the entire original photograph as visual truth for complete furniture boundaries.");
        }

        if (analysis.MainColors.Count > 0)
            sb.AppendLine($"- Dominant Structural Colors: {string.Join(", ", analysis.MainColors)}");

        if (analysis.AccentColors.Count > 0)
            sb.AppendLine($"- Accent Colors: {string.Join(", ", analysis.AccentColors)}");

        if (analysis.ColorLocations.Count > 0)
        {
            sb.AppendLine("- Color Spatial Constraints:");
            foreach (var kvp in analysis.ColorLocations)
            {
                sb.AppendLine($"  * {kvp.Key}: MUST REMAIN STRICTLY RESTRICTED TO {kvp.Value}. DO NOT SPREAD OR EXPAND THIS COLOR.");
            }
        }

        if (assemblies != null && assemblies.Count > 0)
        {
            var strictAssemblies = assemblies.Where(a => a.Authority == ConstraintAuthority.Strict).ToList();
            if (strictAssemblies.Count > 0)
            {
                sb.AppendLine("- Strict Structural Assemblies (Verified Invariants):");
                foreach (var asm in strictAssemblies)
                {
                    var structuralTag = asm.Protected ? "[STRUCTURAL]" : "[ACCENT/DECORATIVE]";
                    var traceInfo = asm.MemberRegionIds.Count > 0 && asm.MemberRegionIds.Count <= 4
                        ? $" (Preserves {asm.MemberCount} local regions: {string.Join(", ", asm.MemberRegionIds)})"
                        : asm.MemberRegionIds.Count > 4
                            ? $" (Preserves {asm.MemberCount} local regions: {string.Join(", ", asm.MemberRegionIds.Take(3))}...)"
                            : "";
                    sb.AppendLine($"  * {asm.AssemblyId} ({asm.AssemblyType}) {structuralTag}: {asm.Summary}{traceInfo}");
                }
            }

            var allAssembledMemberIds = assemblies.SelectMany(a => a.MemberRegionIds).ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<FurnitureComponent> leftoverComponents;
            if (individualCriticalRegionIds != null)
            {
                var criticalIds = individualCriticalRegionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                leftoverComponents = analysis.Components
                    .Where(c => !allAssembledMemberIds.Contains(c.Id) && criticalIds.Contains(c.Id))
                    .Take(10)
                    .ToList();
            }
            else
            {
                leftoverComponents = analysis.Components
                    .Where(c => !allAssembledMemberIds.Contains(c.Id))
                    .Take(8)
                    .ToList();
            }

            foreach (var comp in leftoverComponents)
            {
                var structuralTag = comp.IsStructural ? "[STRUCTURAL]" : "[ACCENT/DECORATIVE]";
                sb.AppendLine($"  * {comp.Id} ({comp.Type}) {structuralTag}: Color must remain {comp.Color}, Material must remain {comp.Material}. {comp.Description}");
            }
        }
        else if (analysis.Components.Count > 0)
        {
            sb.AppendLine("- Structured Component Invariants:");
            foreach (var comp in analysis.Components)
            {
                var structuralTag = comp.IsStructural ? "[STRUCTURAL]" : "[ACCENT/DECORATIVE]";
                sb.AppendLine($"  * {comp.Id} ({comp.Type}) {structuralTag}: Color must remain {comp.Color}, Material must remain {comp.Material}. {comp.Description}");
            }
        }

        if (analysis.ProtectedFeatures.Count > 0)
        {
            sb.AppendLine("- Protected Features:");
            foreach (var feature in analysis.ProtectedFeatures)
            {
                sb.AppendLine($"  * {feature}");
            }
        }
        sb.AppendLine();

        // ---------------------------------------------------------
        // SECTION 2: ADVISORY OBSERVATIONS
        // ---------------------------------------------------------
        sb.AppendLine("============================================================");
        sb.AppendLine("ADVISORY OBSERVATIONS (CONTEXTUAL GUIDANCE):");
        sb.AppendLine("============================================================");

        var advisoryAssemblies = assemblies?.Where(a => a.Authority == ConstraintAuthority.Advisory).ToList();
        if (advisoryAssemblies != null && advisoryAssemblies.Count > 0)
        {
            sb.AppendLine("- Advisory Structural Patterns:");
            foreach (var asm in advisoryAssemblies)
            {
                sb.AppendLine($"  * {asm.AssemblyId}: {asm.Summary}");
            }
        }

        var advisoryColors = analysis.SceneColorReferences
            .Where(x => x.Protected && x.Authority == ConstraintAuthority.Advisory && !x.ColorConflictDetected)
            .ToList();
        if (advisoryColors.Count > 0)
        {
            sb.AppendLine("- Advisory Permanent Scene Colors:");
            foreach (var sc in advisoryColors)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  * The {0} at [{1:F2},{2:F2},{3:F2},{4:F2}] is an advisory {5} surface. Preserve its authentic observed appearance.",
                    sc.Role, sc.Region.X, sc.Region.Y, sc.Region.Width, sc.Region.Height, sc.HueFamily));
            }
        }

        if (analysis.MainMaterials.Count > 0)
        {
            sb.AppendLine($"- Material Observations: {string.Join(", ", analysis.MainMaterials)}");
        }
        sb.AppendLine("- Advisory Cleanup: Remove candidate temporary objects only if clearly temporary and removal does not require reconstructing furniture.");
        sb.AppendLine();

        // ---------------------------------------------------------
        // SECTION 3: UNCERTAIN / DO NOT INFER
        // ---------------------------------------------------------
        sb.AppendLine("============================================================");
        sb.AppendLine("UNCERTAIN / DO NOT INFER:");
        sb.AppendLine("============================================================");

        if (analysis.FurnitureEnvelopeAuthority == ConstraintAuthority.Uncertain)
        {
            sb.AppendLine("- Furniture Envelope Localization: The furniture envelope could not be determined reliably from local analysis. Do not infer furniture outside clearly observed structural regions; use the entire original photograph as visual truth for full furniture extent.");
        }

        var uncertainColors = analysis.SceneColorReferences
            .Where(x => x.Authority == ConstraintAuthority.Uncertain || x.ColorConflictDetected || x.HueFamily.Equals("observed_surface_tone", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (uncertainColors.Count > 0)
        {
            foreach (var sc in uncertainColors)
            {
                var roleLower = sc.Role.ToLowerInvariant();
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "- Surface Color Uncertainty: The {0} color could not be determined reliably. Preserve the observed source color; do not recolor it. Source appearance at [{1:F2},{2:F2},{3:F2},{4:F2}] remains the absolute reference.",
                    roleLower, sc.Region.X, sc.Region.Y, sc.Region.Width, sc.Region.Height));
            }
        }

        if (analysis.UncertainFeatures.Count > 0)
        {
            foreach (var uf in analysis.UncertainFeatures)
            {
                sb.AppendLine($"- {uf}");
            }
        }
        sb.AppendLine("- General Invariant: If any feature is ambiguous or candidate-based, preserve the original photographic appearance without generative redesign or symmetric invention.");
        sb.AppendLine();

        sb.AppendLine();

        if (isConservative)
        {
            sb.AppendLine("ALLOWED OPERATIONS (PHOTOGRAPHIC QUALITY IMPROVEMENT ONLY):");
            sb.AppendLine("- Conservative noise reduction and minor photographic artifact reduction.");
            sb.AppendLine("- Exposure correction and conservative contrast correction.");
            sb.AppendLine("- White balance correction.");
            sb.AppendLine("- Reasonable sharpness improvement and reasonable clarity improvement where visually supported.");
            sb.AppendLine("- Color rule: Correct photographic color cast while strictly preserving the original observed furniture and scene color identity.");
            sb.AppendLine("  Do NOT make wood warmer or more luxurious just for style. Do NOT whiten surfaces unnaturally. Do NOT replace marble/stone patterning.");
            sb.AppendLine();

            sb.AppendLine("STRICT PROHIBITIONS:");
            sb.AppendLine("- NO generative scene reconstruction.");
            sb.AppendLine("- No removal of permanent objects or any object excluded by the active cleanup policy.");
            sb.AppendLine("- No reflection removal unless separately authorized.");
            sb.AppendLine("- NO reframing.");
            sb.AppendLine("- NO zoom-out.");
            sb.AppendLine("- NO outpainting.");
            sb.AppendLine("- NO reconstruction of content outside the source frame.");
            sb.AppendLine("- DO NOT alter furniture geometry, layout, dimensions, proportions, or alignment.");
            sb.AppendLine("- DO NOT redesign, modernize, upgrade, replace, or alter the furniture or scene in any manner.");
            sb.AppendLine("- DO NOT add, remove, or modify cabinet doors, drawers, handles, pulls, hinges, rails, struts, countertops, glass, mirrors, reflections, decorative trims, or structural panels.");
            sb.AppendLine("- DO NOT alter the furniture's core materials, wood species, melamine textures, or structural finishes.");
            sb.AppendLine("- DO NOT recolor the furniture. Do not make wood warmer or redder just to look better.");
            sb.AppendLine("- DO NOT expand accent colors into structural panels or adjacent components.");
            sb.AppendLine("- DO NOT invent missing architectural details, ornamental trims, or artificial luxury features.");
            sb.AppendLine("- DO NOT alter camera angle, viewpoint, framing, crop, or composition.");
            sb.AppendLine();
        }
        else if (!isHighQualityConservative)
        {
            // Non-conservative / permissive mode (Phase 3.6 capabilities)
            sb.AppendLine("ALLOWED ENHANCEMENTS:");
            sb.AppendLine("1. PHOTO ENHANCEMENT (PHOTOGRAPHIC QUALITY & COLOR UPGRADE):");
            sb.AppendLine("   - Conservative white-balance correction.");
            sb.AppendLine("   - Conservative exposure correction and balanced contrast.");
            sb.AppendLine("   - Noise reduction and haze removal.");
            sb.AppendLine("   - Moderate sharpness and local detail improvement where visually supported.");
            sb.AppendLine("   - Color rule: Correct photographic color cast while strictly preserving the original observed furniture color identity.");
            sb.AppendLine("     Do NOT make wood warmer or more luxurious just for style. Do NOT whiten surfaces unnaturally. Do NOT replace marble/stone patterning.");
            sb.AppendLine();

            if (options?.AllowReflectionCleanup == true)
            {
                sb.AppendLine("REFLECTION CLEANUP (SEPARATE POLICY):");
                sb.AppendLine("   - Remove only clearly incidental photographer/camera reflections. Preserve reflections that define glass, mirror, metal, or glossy material appearance.");
                sb.AppendLine();
            }

            sb.AppendLine("STRICT PROHIBITIONS (FURNITURE PRESERVATION):");
            sb.AppendLine("- DO NOT alter furniture geometry, layout, dimensions, proportions, or alignment.");

            sb.AppendLine("4. SAFE ZOOM-OUT / REFRAMING DIRECTIVE:");
            if (analysis.FramingAnalysis != null && analysis.FramingAnalysis.SafeZoomOutRecommended && analysis.FramingAnalysis.IsFurnitureCropped)
            {
                var exp = analysis.FramingAnalysis.RecommendedExpansion;
                sb.AppendLine($"   - The furniture is visibly cropped at edges: {string.Join(", ", analysis.FramingAnalysis.CropEdges)}.");
                sb.AppendLine($"   - You may apply a conservative slight zoom-out (expand frame outward: Top {exp.TopPercent}%, Bottom {exp.BottomPercent}%, Left {exp.LeftPercent}%, Right {exp.RightPercent}%).");
                sb.AppendLine("   - Revealing nearby wall, floor, ceiling, or empty surrounding context is permitted.");
                sb.AppendLine("   - PRESERVE the exact same furniture proportions, relative scale, and camera viewpoint.");
                sb.AppendLine("   - DO NOT redesign hidden furniture structure. DO NOT invent new doors, drawers, shelves, or panels.");
                sb.AppendLine("   - If missing cropped area would require guessing hidden structure, keep zoom-out minimal.");
            }
            else
            {
                sb.AppendLine("   - Keep the original camera framing and perspective. Do not zoom out or outpaint.");
            }
            sb.AppendLine();

            sb.AppendLine("STRICT PROHIBITIONS (FURNITURE PRESERVATION):");
            sb.AppendLine("- DO NOT redesign, modernize, upgrade, replace, or alter the furniture in any manner.");
            sb.AppendLine("- DO NOT change the size, proportions, dimensions, or perspective of the furniture.");
            sb.AppendLine("- DO NOT add, remove, or modify cabinet doors, drawers, handles, pulls, hinges, rails, struts, countertops, glass, mirrors, decorative trims, or structural panels.");
            sb.AppendLine("- DO NOT alter the furniture's core materials, wood species, melamine textures, or structural finishes.");
            sb.AppendLine("- DO NOT recolor the furniture. Do not make wood warmer or redder just to look better.");
            sb.AppendLine("- DO NOT expand accent colors into structural panels or adjacent components.");
            sb.AppendLine("- DO NOT invent missing architectural details, ornamental trims, or artificial luxury features.");
            sb.AppendLine("- DO NOT alter camera angle, viewpoint, or framing beyond safe conservative zoom-out specified above.");
            sb.AppendLine();
        }

        if (attemptNumber > 1 && !string.IsNullOrWhiteSpace(failureFeedback))
        {
            sb.AppendLine("CORRECTIVE INSTRUCTION FROM PREVIOUS ATTEMPT FAILURE:");
            sb.AppendLine(failureFeedback);
            sb.AppendLine("You MUST correct this specific error in this restoration attempt.");
            sb.AppendLine();
        }

        if (isHighQualityConservative)
        {
            sb.AppendLine("FINAL INSTRUCTION:");
            sb.AppendLine("Generate and output a strongly restored, professional photographic image of this real furniture installation. Substantially improve optical clarity, local contrast, tonal separation, and dynamic range while strictly preserving the exact physical furniture geometry, hardware cardinality, and permanent architectural structure. Do not return an unchanged copy.");
            sb.AppendLine("The expected output is the restored image, not text.");
        }
        else if (isConservative)
        {
            sb.AppendLine("Generate and output a conservatively restored, high-quality photographic image of this furniture, improving photographic quality, and preserving the complete original scene, people, reflections, environment, and furniture structure exactly as specified.");
            sb.AppendLine("The expected output is the restored image, not text.");
        }
        else
        {
            sb.AppendLine("Generate and output a high-quality photographic restoration, applying only the active scene-cleanup and person-removal policies while preserving the exact permanent furniture structure.");
            sb.AppendLine("The expected output is the restored image, not text.");
        }

        var finalPrompt = sb.ToString();
        const int MaxSafePromptCharacters = 14000;
        if (finalPrompt.Length > MaxSafePromptCharacters)
        {
            throw new InvalidOperationException(
                $"PromptComplexityExceeded: Compacted restoration prompt length ({finalPrompt.Length} chars) exceeds the safe Gemini generation budget of {MaxSafePromptCharacters} characters.");
        }

        return finalPrompt;
    }

    private static void AppendSceneAndPersonCleanupPolicy(
        StringBuilder sb,
        FurnitureAnalysis analysis,
        OpenImgOptions? options)
    {
        var cleanupMode = options?.SceneCleanupMode ?? SceneCleanupMode.Disabled;
        var personMode = options?.PersonRemovalMode ?? PersonRemovalMode.Disabled;

        sb.AppendLine("SCENE CLEANUP:");
        switch (cleanupMode)
        {
            case SceneCleanupMode.SafeTemporaryOnly:
                sb.AppendLine("SCENE CLEANUP - REQUIRED (SafeTemporaryOnly)");
                sb.AppendLine("Inspect the full photo and remove clearly temporary, loose, non-structural clutter, even if local analysis detects none; detections are evidence, not a gate.");
                sb.AppendLine("Examples: loose bags/backpacks, bottles, packaging, cloths, tape, debris, tools, temporary containers, and unrelated counter/floor objects.");
                sb.AppendLine("Preserve furniture, hardware, installed accessories, electrical/plumbing fixtures, supports, permanent decor, and anything uncertain. Never guess hidden structure; if removal needs uncertain reconstruction, preserve the occluded area.");
                break;
            case SceneCleanupMode.StrongTemporaryCleanup:
                sb.AppendLine("SCENE CLEANUP - REQUIRED (StrongTemporaryCleanup)");
                sb.AppendLine("Carefully inspect the entire photograph and actively remove all clearly temporary, loose, non-structural clutter from floors, countertops, and surrounding surfaces, even when local analysis detected nothing.");
                sb.AppendLine("Local detector findings are supporting evidence only. Typical clutter includes loose bags, backpacks, bottles, packaging, cloths, tape, construction debris, loose tools, temporary containers, and unrelated loose objects.");
                sb.AppendLine("The same permanent-structure safety boundary applies: preserve furniture, hardware, installed accessories, electrical/plumbing fixtures, structural supports, permanent decoration, and anything uncertain. Never guess hidden structure; preserve ambiguous or occluded content.");
                break;
            default:
                sb.AppendLine("SCENE CLEANUP: Disabled. Do not remove loose objects, clutter, people, or background items under the scene-cleanup policy.");
                break;
        }

        sb.AppendLine();
        sb.AppendLine("PERSON REMOVAL POLICY (SEPARATE FROM GENERAL CLUTTER CLEANUP):");
        if (personMode == PersonRemovalMode.SafeWhenClearlyTemporary)
        {
            sb.AppendLine("Remove a person only when clearly incidental to the furniture photograph, fully separate from the furniture, and the background can be restored without guessing or altering permanent structure.");
            sb.AppendLine("If the person touches or occludes furniture, blocks uncertain structure, or safe background reconstruction is not evident, preserve the person and the obscured area. Do not invent furniture or architecture.");
        }
        else
        {
            sb.AppendLine("Person removal is disabled. Preserve all people and person reflections; general temporary-clutter cleanup does not authorize removing a person.");
        }

        sb.AppendLine();
    }

    private static List<ComponentCardinalityConstraint> DeriveCardinalityFromAnalysis(FurnitureAnalysis analysis)
    {
        // Gemini contracts may know a component name; preserve its observed count exactly rather than assuming symmetry.
        return analysis.Components
            .Where(c => !string.IsNullOrWhiteSpace(c.Type) && c.Region != null)
            .GroupBy(c => c.Type, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ComponentCardinalityConstraint
            {
                ConstraintId = $"gemini_{g.Key}_exact_count",
                Category = g.Key,
                RawCandidateCount = g.Count(), DeduplicatedCount = g.Count(),
                MinimumAllowedCount = g.Count(), MaximumAllowedCount = g.Count(),
                Region = new ProtectedRegion { RegionType = "gemini_component_group", Description = "Gemini observed component group" },
                Confidence = g.Average(c => c.Confidence), SourceRegionIds = g.Select(c => c.Id).ToList(), Strict = true
            }).ToList();
    }

    private static string BuildLegacyRestorationPrompt(
        FurnitureAnalysis analysis,
        int attemptNumber,
        string? failureFeedback,
        string version)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TASK: PHOTOGRAPHIC RESTORATION ONLY - NOT FURNITURE REDESIGN.");
        sb.AppendLine($"Prompt Version: {version}");
        sb.AppendLine($"Attempt: {attemptNumber}");
        sb.AppendLine();
        sb.AppendLine("CORE DIRECTIVE:");
        sb.AppendLine("The original photograph is the absolute, immutable source of truth.");
        sb.AppendLine("Your sole responsibility is photographic restoration of this real, existing furniture.");
        sb.AppendLine("Do not redesign, modernize, upgrade, replace, or alter the furniture in any manner.");
        sb.AppendLine();

        sb.AppendLine("FURNITURE CONTRACT TO PRESERVE EXACTLY:");
        if (!string.IsNullOrWhiteSpace(analysis.FurnitureType))
            sb.AppendLine($"- Furniture Type: {analysis.FurnitureType}");
        if (!string.IsNullOrWhiteSpace(analysis.GeometryDescription))
            sb.AppendLine($"- Geometry & Layout: {analysis.GeometryDescription}");
        if (analysis.MainColors.Count > 0)
            sb.AppendLine($"- Dominant Structural Colors: {string.Join(", ", analysis.MainColors)}");
        if (analysis.AccentColors.Count > 0)
            sb.AppendLine($"- Accent Colors: {string.Join(", ", analysis.AccentColors)}");

        if (analysis.ColorLocations.Count > 0)
        {
            sb.AppendLine("- Color Spatial Constraints:");
            foreach (var kvp in analysis.ColorLocations)
                sb.AppendLine($"  * {kvp.Key}: MUST REMAIN STRICTLY RESTRICTED TO {kvp.Value}. DO NOT SPREAD OR EXPAND THIS COLOR.");
        }

        if (analysis.Components.Count > 0)
        {
            sb.AppendLine("- Structured Component Invariants:");
            foreach (var comp in analysis.Components)
            {
                var structuralTag = comp.IsStructural ? "[STRUCTURAL]" : "[ACCENT/DECORATIVE]";
                sb.AppendLine($"  * {comp.Id} ({comp.Type}) {structuralTag}: Color must remain {comp.Color}, Material must remain {comp.Material}. {comp.Description}");
            }
        }

        if (analysis.ProtectedFeatures.Count > 0)
        {
            sb.AppendLine("- Protected Features:");
            foreach (var feature in analysis.ProtectedFeatures)
                sb.AppendLine($"  * {feature}");
        }

        sb.AppendLine();
        sb.AppendLine("STRICT PROHIBITIONS (FURNITURE PRESERVATION):");
        sb.AppendLine("- DO NOT alter furniture geometry, layout, dimensions, proportions, or alignment.");
        sb.AppendLine("- DO NOT add, remove, or modify cabinet doors, drawers, handles, pulls, hinges, or structural panels.");
        sb.AppendLine("- DO NOT alter the furniture's core materials, wood species, melamine textures, or structural finishes.");
        sb.AppendLine("- DO NOT recolor or re-stain any part of the furniture.");
        sb.AppendLine("- DO NOT expand accent colors into structural panels or adjacent components.");
        sb.AppendLine("- DO NOT invent missing architectural details, ornamental trims, or artificial luxury features.");
        sb.AppendLine("- DO NOT alter camera angle, perspective, or framing.");
        sb.AppendLine();

        sb.AppendLine("ALLOWED ENHANCEMENTS & SCENE CLEANUP DIRECTIVES:");
        sb.AppendLine("- ELIMINATE CLUTTER & TRASH: Completely remove all non-furniture clutter, construction tools, cleaning supplies, brooms, loose wires, bottles, jars, cans, dishes, cookware, trash, dust, dirt, stains, and loose debris from countertops, floors, and surrounding surfaces.");
        sb.AppendLine("- REMOVE PEOPLE & UNWANTED REFLECTIONS: Remove any people visible in the scene. Eliminate distracting reflections of people, photographers, or camera equipment from glass, mirrors, and glossy surfaces.");
        sb.AppendLine("- SPOTLESS SURFACES: Countertops, backsplashes, and floor tiles must be left clean, spotless, and impeccably finished.");
        sb.AppendLine("- PHOTOGRAPHIC QUALITY & COLOR UPGRADE: Substantially upgrade the photographic quality from vintage or degraded photos. Enhance color vibrancy, restore rich natural tones (such as warm wood grains and crisp white marble veining), balance exposure, optimize contrast and dynamic range, and eliminate camera haze or sensor noise.");
        sb.AppendLine("- OPTICAL SHARPNESS: Enhance optical sharpness, edge definition, and architectural lighting while preserving authentic materials.");
        sb.AppendLine();

        if (attemptNumber > 1 && !string.IsNullOrWhiteSpace(failureFeedback))
        {
            sb.AppendLine("CORRECTIVE INSTRUCTION FROM PREVIOUS ATTEMPT FAILURE:");
            sb.AppendLine(failureFeedback);
            sb.AppendLine("You MUST correct this specific error in this restoration attempt.");
            sb.AppendLine();
        }

        sb.AppendLine("Generate and output a cleanly restored, high-quality photographic image of this furniture, removing clutter and dirt, enhancing colors, and preserving the exact furniture structure as specified.");

        return sb.ToString();
    }

    public static string BuildComparativeValidationPrompt(string version = CurrentValidationVersion)
    {
        if (version == "v1.0")
        {
            return BuildLegacyComparativeValidationPrompt(version);
        }

        return $@"Compare the ORIGINAL photograph (Image 1) and the RESTORED image (Image 2).
Prompt Version: {version}

CRITICAL RULES:
1. This is an EVALUATION ONLY task.
2. DO NOT edit or generate an image.
3. ALLOW AND IGNORE legitimate photographic restoration and scene cleanup:
   - noise suppression, artifact removal, and sharpness improvement
   - exposure balance, contrast optimization, dynamic range, and natural color vibrancy enhancement
   - neutral white balance correction
   - removal of people, photographer reflections, or camera glare
   - removal of non-furniture clutter, trash, dirt, debris, dust, bottles, jars, pots, pans, tools, or cleaning equipment from countertops and floors
   - conservative slight zoom-out / reframing only when recovering cropped furniture, provided furniture proportions and scale remain faithful
4. SEPARATE EVALUATIONS:
   - Evaluate 'furnitureDecision': PASS / FAIL / UNCERTAIN
     Determine strictly whether the RESTORED image altered, added, removed, or modified ANY physical or structural characteristic of the furniture itself (cabinet doors, drawers, handles, panels, shelves, furniture dimensions, materials, or furniture colors).
   - Evaluate 'sceneCleanupDecision': ACCEPTABLE / EXCESSIVE / UNCERTAIN / NOT_APPLICABLE
     Determine whether non-furniture cleanup was conservative and avoided damaging furniture or permanent environment.
   - Evaluate 'reframingDecision': ACCEPTABLE / EXCESSIVE / UNCERTAIN / NOT_APPLICABLE
     Determine whether outward framing expansion was conservative, preserved viewpoint and proportions, and did not invent speculative furniture design.
5. OVERALL DECISION RULES:
   - Furniture failure ALWAYS dominates. Scene cleanliness must NEVER compensate for furniture alteration.
   - If 'furnitureDecision' is 'FAIL', 'decision' MUST be 'FAIL'.
   - If 'furnitureDecision' is 'UNCERTAIN', 'decision' MUST be 'UNCERTAIN'.
   - If 'furnitureDecision' is 'PASS' and scene cleanup & reframing are 'ACCEPTABLE' or 'NOT_APPLICABLE', 'decision' is 'PASS'.
   - If 'furnitureDecision' is 'PASS' but cleanup or reframing is 'EXCESSIVE', 'decision' is 'UNCERTAIN' or 'FAIL'.
6. BE STRICTLY CONSERVATIVE: If there is any doubt or uncertainty about whether a structural furniture detail was modified, return 'UNCERTAIN' instead of 'PASS'.

Evaluate and return STRICT JSON ONLY matching this schema:
{{
  ""furnitureDecision"": ""PASS"",
  ""sceneCleanupDecision"": ""ACCEPTABLE"",
  ""reframingDecision"": ""ACCEPTABLE"",
  ""geometryChanged"": false,
  ""colorsChanged"": false,
  ""colorDistributionChanged"": false,
  ""materialsChanged"": false,
  ""doorsChanged"": false,
  ""drawersChanged"": false,
  ""shelvesChanged"": false,
  ""handlesChanged"": false,
  ""glassChanged"": false,
  ""panelsChanged"": false,
  ""decorativeElementsChanged"": false,
  ""structuralElementsChanged"": false,
  ""cameraCompositionSignificantlyChanged"": false,
  ""unexpectedChanges"": [],
  ""uncertainChanges"": [],
  ""confidence"": 0.98,
  ""decision"": ""PASS"",
  ""notes"": ""Clean conservative restoration; furniture geometry and all components perfectly preserved.""
}}

Allowed values for 'decision' are strictly: 'PASS', 'FAIL', or 'UNCERTAIN'.";
    }

    private static string BuildLegacyComparativeValidationPrompt(string version)
    {
        return $@"Compare the ORIGINAL photograph (Image 1) and the RESTORED image (Image 2).
Prompt Version: {version}

CRITICAL RULES:
1. This is an EVALUATION ONLY task.
2. DO NOT edit or generate an image.
3. ALLOW AND IGNORE legitimate photographic restoration and scene cleanup:
   - noise suppression, artifact removal, and sharpness improvement
   - exposure balance, contrast optimization, dynamic range, and natural color vibrancy enhancement
   - neutral white balance correction
   - removal of people, photographer reflections, or camera glare
   - removal of non-furniture clutter, trash, dirt, debris, dust, bottles, jars, pots, pans, tools, or cleaning equipment from countertops and floors
4. FOCUS EXCLUSIVELY ON THE FURNITURE ITSELF:
   - Determine strictly whether the RESTORED image altered, added, removed, or invented ANY physical or structural characteristic of the furniture itself (cabinet doors, drawers, handles, panels, shelves, furniture dimensions, materials, or furniture colors).
5. BE STRICTLY CONSERVATIVE: If there is any doubt or uncertainty about whether a structural furniture detail was modified, return 'UNCERTAIN' instead of 'PASS'.

Evaluate and return STRICT JSON ONLY matching this schema:
{{
  ""geometryChanged"": false,
  ""colorsChanged"": false,
  ""colorDistributionChanged"": false,
  ""materialsChanged"": false,
  ""doorsChanged"": false,
  ""drawersChanged"": false,
  ""shelvesChanged"": false,
  ""handlesChanged"": false,
  ""glassChanged"": false,
  ""panelsChanged"": false,
  ""decorativeElementsChanged"": false,
  ""structuralElementsChanged"": false,
  ""cameraCompositionSignificantlyChanged"": false,
  ""unexpectedChanges"": [],
  ""uncertainChanges"": [],
  ""confidence"": 0.98,
  ""decision"": ""PASS"",
  ""notes"": ""Clean conservative restoration; furniture geometry and all components perfectly preserved.""
}}

Allowed values for 'decision' are strictly: 'PASS', 'FAIL', or 'UNCERTAIN'.
If any physical feature changed, decision MUST be 'FAIL'.
If any feature is ambiguous or cannot be ruled out, decision MUST be 'UNCERTAIN'.";
    }

    public static string BuildThreeWayComparativeValidationPrompt(string calibrationId = "")
    {
        return $@"TASK: THREE-WAY RESTORATION COMPARATIVE EVALUATION (CALIBRATION {calibrationId}).
You are provided with three images in order:
Image 1: ORIGINAL photograph (the ground truth reference)
Image 2: RESTORATION CANDIDATE A
Image 3: RESTORATION CANDIDATE B

EVALUATION OBJECTIVE:
Evaluate Candidate A and Candidate B INDEPENDENTLY against the Original photograph across two separate, non-conflated dimensions:
1. PHYSICAL FURNITURE FIDELITY (Strict preservation of real physical structure, geometry, components, materials, hardware, and designed colors).
2. PHOTOGRAPHIC QUALITY IMPROVEMENT (Perceived sharpness, clarity, exposure balance, noise reduction, white balance cast correction, and dynamic range).

CRITICAL EVALUATION RULES:
- DO NOT PICK A WINNER. Do not declare which candidate is preferred or better. Evaluate each candidate purely on its own merits against the original.
- SEPARATE FIDELITY FROM QUALITY: A candidate may have excellent photographic quality but fail on physical fidelity (e.g., redesigning doors or hallucinating hardware). Conversely, a candidate may have perfect physical fidelity but only weak photographic improvement. Evaluate these two dimensions completely independently.
- COLOR POLICY: Photographic white-balance correction (e.g., restoring a white cabinet that was yellow/dirty due to camera cast toward neutral white) is EXPECTED and ACCEPTABLE. However, altering designed colors (e.g., changing wood tone, changing trim color, or bleeding accent colors) is a FIDELITY FAILURE. Intentional warm lighting (LED strips, ambient fixtures) should be preserved.
- PERSPECTIVE POLICY: Safe global photographic straightening/rectification is ACCEPTABLE if it preserves global proportions without locally warping components.
- ZERO FURNITURE REDESIGN: Adding or removing doors, drawers, shelves, hinges, or handles, or changing cabinet dimensions is a strict REJECTION.

Return STRICT JSON ONLY matching this exact structure:
{{
  ""CandidateA"": {{
    ""FurnitureDecision"": ""ACCEPTED"",
    ""GeometryPreservation"": ""EXCELLENT"",
    ""ComponentPreservation"": ""EXCELLENT"",
    ""HardwarePreservation"": ""EXCELLENT"",
    ""HardwareCountPreservation"": ""EXCELLENT"",
    ""HardwareLocationPreservation"": ""EXCELLENT"",
    ""AsymmetryPreservation"": ""EXCELLENT"",
    ""FurnitureColorIdentity"": ""EXCELLENT"",
    ""SceneColorIdentity"": ""EXCELLENT"",
    ""ColorIdentityPreservation"": ""EXCELLENT"",
    ""MaterialPreservation"": ""EXCELLENT"",
    ""ScenePreservation"": ""EXCELLENT"",
    ""PersistentSceneDetailPreservation"": ""EXCELLENT"",
    ""NegativeSpacePreservation"": ""EXCELLENT"",
    ""UnexpectedAddedFurniture"": [],
    ""UnexpectedAddedHardware"": [],
    ""UnexpectedRemovedDetails"": [],
    ""PhotographicQualityImprovement"": ""STRONG"",
    ""SharpnessImprovement"": ""GOOD"",
    ""ExposureImprovement"": ""GOOD"",
    ""WhiteBalanceImprovement"": ""GOOD"",
    ""ColorImprovement"": ""GOOD"",
    ""OverprocessingDetected"": false,
    ""Decision"": ""ACCEPTED"",
    ""Notes"": ""Detailed evaluation notes for Candidate A.""
  }},
  ""CandidateB"": {{
    ""FurnitureDecision"": ""ACCEPTED"",
    ""GeometryPreservation"": ""EXCELLENT"",
    ""ComponentPreservation"": ""EXCELLENT"",
    ""HardwarePreservation"": ""EXCELLENT"",
    ""HardwareCountPreservation"": ""EXCELLENT"",
    ""HardwareLocationPreservation"": ""EXCELLENT"",
    ""AsymmetryPreservation"": ""EXCELLENT"",
    ""FurnitureColorIdentity"": ""EXCELLENT"",
    ""SceneColorIdentity"": ""EXCELLENT"",
    ""ColorIdentityPreservation"": ""EXCELLENT"",
    ""MaterialPreservation"": ""EXCELLENT"",
    ""ScenePreservation"": ""EXCELLENT"",
    ""PersistentSceneDetailPreservation"": ""EXCELLENT"",
    ""NegativeSpacePreservation"": ""EXCELLENT"",
    ""UnexpectedAddedFurniture"": [],
    ""UnexpectedAddedHardware"": [],
    ""UnexpectedRemovedDetails"": [],
    ""PhotographicQualityImprovement"": ""STRONG"",
    ""SharpnessImprovement"": ""GOOD"",
    ""ExposureImprovement"": ""GOOD"",
    ""WhiteBalanceImprovement"": ""GOOD"",
    ""ColorImprovement"": ""GOOD"",
    ""OverprocessingDetected"": false,
    ""Decision"": ""ACCEPTED"",
    ""Notes"": ""Detailed evaluation notes for Candidate B.""
  }},
  ""Comparison"": {{
    ""MeaningfulFidelityDifferenceDetected"": false,
    ""DifferenceDescription"": ""Comparative notes on fidelity and quality differences between the two candidates.""
  }}
}}

Valid values for FurnitureDecision / Decision: 'ACCEPTED', 'REJECTED', 'NEEDS_REVIEW'.
Valid values for Quality Improvement: 'EXCELLENT', 'STRONG', 'MODERATE', 'WEAK', 'OVERPROCESSED'.";
    }
}
