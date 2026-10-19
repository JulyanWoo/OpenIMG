using OpenCvSharp;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Analysis;

public interface ILocalContractBuilder
{
    LocalFurnitureContract BuildContract(
        string sourceFileName,
        string sourceSha256,
        Mat normalizedImage,
        List<LocalDetectedObject> detectedObjects,
        List<LocalStructuralRegion> structuralRegions,
        List<HardwareCandidate> hardwareCandidates,
        List<LocalColorRegion> colorRegions,
        TimeSpan analysisDuration,
        LocalModelInfo? modelInfo,
        LocalAnalysisStatus status = LocalAnalysisStatus.Success,
        List<string>? warnings = null);
}
