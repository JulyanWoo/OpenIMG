using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Analysis;

public interface IAnalysisComparator
{
    AnalysisComparisonReport Compare(
        string calibrationId,
        string sourceFileName,
        string sourceSha256,
        FurnitureAnalysis geminiAnalysis,
        LocalFurnitureContract localContract,
        double? peakMemoryMb = null);
}
