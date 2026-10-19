using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Analysis;

public interface ILocalToFurnitureAnalysisAdapter
{
    FurnitureAnalysis Adapt(LocalFurnitureContract localContract);
}
