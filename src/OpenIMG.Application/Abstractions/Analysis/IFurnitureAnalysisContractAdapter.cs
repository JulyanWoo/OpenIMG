using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Analysis;

/// <summary>
/// Projects current local and provider analysis representations into the
/// canonical product contract, and projects it back for legacy consumers while
/// migration is staged.
/// </summary>
public interface IFurnitureAnalysisContractAdapter
{
    FurnitureAnalysisContract FromLocal(LocalFurnitureContract localContract);

    FurnitureAnalysisContract FromProvider(
        FurnitureAnalysis analysis,
        string sourceFileName = "",
        string sourceSha256 = "");

    FurnitureAnalysis ToLegacy(FurnitureAnalysisContract contract);
}
