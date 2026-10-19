using OpenCvSharp;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Infrastructure.ImageProcessing;

/// <summary>
/// Backward-compatible forwarding facade for callers that still import the
/// historical Infrastructure namespace. The implementation lives exclusively
/// in OpenIMG.Application.Analysis.OpenCvQualityAnalyzer.
/// </summary>
public static class OpenCvQualityAnalyzer
{
    public static LocalImageQualityAssessment Analyze(string imagePath) =>
        OpenIMG.Application.Analysis.OpenCvQualityAnalyzer.Analyze(imagePath);

    public static LocalImageQualityAssessment Analyze(Mat mat) =>
        OpenIMG.Application.Analysis.OpenCvQualityAnalyzer.Analyze(mat);

    public static LocalImageQualityAssessment AnalyzeQuality(Mat mat) =>
        OpenIMG.Application.Analysis.OpenCvQualityAnalyzer.Analyze(mat);

    public static double CalculateDarkChannelVeil(Mat bgrMat) =>
        OpenIMG.Application.Analysis.OpenCvQualityAnalyzer.CalculateDarkChannelVeil(bgrMat);

    public static double CalculateDynamicRange(Mat gray) =>
        OpenIMG.Application.Analysis.OpenCvQualityAnalyzer.CalculateDynamicRange(gray);

    public static PhotographicImprovementAssessment AssessImprovement(
        LocalImageQualityAssessment sourceQuality,
        LocalValidationReport report) =>
        OpenIMG.Application.Analysis.OpenCvQualityAnalyzer.AssessImprovement(sourceQuality, report);
}
