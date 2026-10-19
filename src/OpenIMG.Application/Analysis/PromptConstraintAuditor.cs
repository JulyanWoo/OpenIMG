using OpenIMG.Core.Models;

namespace OpenIMG.Application.Analysis;

public static class PromptConstraintAuditor
{
    public static PromptConstraintComparisonReport Audit(
        string calibrationId,
        string category,
        string localPrompt,
        string geminiPrompt,
        FurnitureAnalysis? localAnalysis = null,
        FurnitureAnalysis? geminiAnalysis = null,
        LocalFurnitureContract? localContract = null)
    {
        var lp = localPrompt.ToLowerInvariant();
        var gp = geminiPrompt.ToLowerInvariant();

        // 1. Furniture Bounds Protection
        var lbBounds = lp.Contains("bounds") || lp.Contains("envelope") || lp.Contains("geometry") || lp.Contains("proportions") || lp.Contains("dimensions");
        var gbBounds = gp.Contains("bounds") || gp.Contains("envelope") || gp.Contains("geometry") || gp.Contains("proportions") || gp.Contains("dimensions");
        var boundsAgreement = lbBounds && gbBounds;

        // 2. Structural Region Protection
        var lbStruct = lp.Contains("structural") || lp.Contains("panel") || lp.Contains("division") || lp.Contains("carcase");
        var gbStruct = gp.Contains("structural") || gp.Contains("panel") || gp.Contains("door") || gp.Contains("drawer");
        var structAgreement = lbStruct && gbStruct;

        // 3. Color Protection
        var lbColor = lp.Contains("color") || lp.Contains("dominant") || lp.Contains("tone");
        var gbColor = gp.Contains("color") || gp.Contains("dominant");
        var colorAgreement = lbColor && gbColor;

        // 4. Accent Color Protection
        var lbAccent = lp.Contains("accent") || lp.Contains("confined") || lp.Contains("bleed");
        var gbAccent = gp.Contains("accent") || gp.Contains("color locations") || gp.Contains("restricted");
        var accentAgreement = lbAccent && gbAccent;

        // 5. Hardware Protection
        var lbHw = lp.Contains("hardware") || lp.Contains("candidate") || lp.Contains("pull") || lp.Contains("knob");
        var gbHw = gp.Contains("hardware") || gp.Contains("handle") || gp.Contains("pull") || gp.Contains("door");
        var hwAgreement = lbHw && gbHw;

        // 6. Material Protection
        var lbMat = lp.Contains("material") || lp.Contains("finish") || lp.Contains("surface") || lp.Contains("reflective");
        var gbMat = gp.Contains("material") || gp.Contains("finish") || gp.Contains("wood") || gp.Contains("melamine");
        var matAgreement = lbMat && gbMat;

        // 7. Scene Protection
        var lbScene = lp.Contains("scene") || lp.Contains("background") || lp.Contains("environment");
        var gbScene = gp.Contains("scene") || lp.Contains("background") || gp.Contains("cleanup") || gp.Contains("preserve");
        var sceneAgreement = lbScene && gbScene;

        // 8. Framing Protection
        var lbFrame = lp.Contains("framing") || lp.Contains("perspective") || lp.Contains("zoom") || lp.Contains("outpaint");
        var gbFrame = gp.Contains("framing") || gp.Contains("perspective") || gp.Contains("zoom") || gp.Contains("outpaint");
        var frameAgreement = lbFrame && gbFrame;

        // 9. Quality Enhancement (Phase 4C.1)
        var lbQuality = lp.Contains("sharpness") || lp.Contains("clarity") || lp.Contains("exposure") || lp.Contains("photographic quality") || lp.Contains("highquality");
        var gbQuality = gp.Contains("sharpness") || gp.Contains("clarity") || gp.Contains("exposure") || gp.Contains("photographic quality") || gp.Contains("highquality");
        var qualityAgreement = lbQuality && gbQuality;

        // 10. White Balance Correction (Phase 4C.1)
        var lbWb = lp.Contains("white balance") || lp.Contains("color cast") || lp.Contains("aged");
        var gbWb = gp.Contains("white balance") || gp.Contains("color cast") || gp.Contains("aged");
        var wbAgreement = lbWb && gbWb;

        // 11. Surface Color Restoration (Phase 4C.1)
        var lbSurf = lp.Contains("surface color") || lp.Contains("neutral white") || lp.Contains("color restoration");
        var gbSurf = gp.Contains("surface color") || gp.Contains("neutral white") || gp.Contains("color restoration");
        var surfAgreement = lbSurf && gbSurf;

        // 12. Illumination Color Preservation (Phase 4C.1)
        var lbIllum = lp.Contains("illumination") || lp.Contains("warm led") || lp.Contains("emitted light") || lp.Contains("warm light");
        var gbIllum = gp.Contains("illumination") || gp.Contains("warm led") || gp.Contains("emitted light") || gp.Contains("warm light");
        var illumAgreement = lbIllum && gbIllum;

        // 13. Perspective Correction Policy (Phase 4C.1)
        var lbPersp = lp.Contains("perspective") || lp.Contains("straightening") || lp.Contains("alignment") || lp.Contains("keystone") || lp.Contains("roll");
        var gbPersp = gp.Contains("perspective") || gp.Contains("straightening") || gp.Contains("alignment") || gp.Contains("keystone") || gp.Contains("roll");
        var perspAgreement = lbPersp && gbPersp;

        var baseAgreedCount = (boundsAgreement ? 1 : 0) +
                              (structAgreement ? 1 : 0) +
                              (colorAgreement ? 1 : 0) +
                              (accentAgreement ? 1 : 0) +
                              (hwAgreement ? 1 : 0) +
                              (matAgreement ? 1 : 0) +
                              (sceneAgreement ? 1 : 0) +
                              (frameAgreement ? 1 : 0);

        var totalAgreedCount = baseAgreedCount +
                              (qualityAgreement ? 1 : 0) +
                              (wbAgreement ? 1 : 0) +
                              (surfAgreement ? 1 : 0) +
                              (illumAgreement ? 1 : 0) +
                              (perspAgreement ? 1 : 0);

        bool isHqConservative = lp.Contains("highqualityconservative") || gp.Contains("highqualityconservative");
        var agreedCount = isHqConservative ? totalAgreedCount : baseAgreedCount;
        var totalDimensions = isHqConservative ? 13.0 : 8.0;
        var overallRatio = agreedCount / totalDimensions;

        return new PromptConstraintComparisonReport
        {
            CalibrationId = calibrationId,
            Category = category,
            FurnitureBoundsProtection = boundsAgreement,
            StructuralRegionProtection = structAgreement,
            ColorProtection = colorAgreement,
            AccentColorProtection = accentAgreement,
            HardwareProtection = hwAgreement,
            MaterialProtection = matAgreement,
            SceneProtection = sceneAgreement,
            FramingProtection = frameAgreement,
            QualityEnhancement = qualityAgreement,
            WhiteBalanceCorrection = wbAgreement,
            SurfaceColorRestoration = surfAgreement,
            IlluminationColorPreservation = illumAgreement,
            PerspectiveCorrectionPolicy = perspAgreement,
            ExplicitComponentCount = (localAnalysis?.Components.Count ?? 0) > 0 ? localAnalysis!.Components.Count : (geminiAnalysis?.Components.Count ?? 0),
            ExplicitProtectedRegionCount = (localContract?.StructuralRegions.Count ?? 0) > 0 ? localContract!.StructuralRegions.Count : (localAnalysis?.Components.Count ?? 0),
            ExplicitColorConstraintCount = (localAnalysis != null ? localAnalysis.MainColors.Count + localAnalysis.AccentColors.Count + localAnalysis.ColorLocations.Count : 0),
            ExplicitHardwareConstraintCount = localContract?.HardwareCandidates.Count ?? (localAnalysis?.Components.Count(c => c.Type.Contains("hardware") || c.Type.Contains("handle")) ?? 0),
            GenericStructuralConstraintCount = localContract?.StructuralRegions.Count(r => r.RegionType == Core.Enums.StructuralRegionType.StructuralPanelCandidate) ?? 0,
            UncertainConstraintCount = localContract?.StructuralRegions.Count(r => r.Confidence < 0.8) ?? (localAnalysis?.Components.Count(c => c.Description.Contains("uncertainty")) ?? 0),
            OverallConstraintAgreement = overallRatio,
            LocalPromptLength = localPrompt.Length,
            GeminiPromptLength = geminiPrompt.Length,
            Notes = $"Agreement: {agreedCount}/13 semantic constraints protected across Local and Gemini prompts."
        };
    }
}
