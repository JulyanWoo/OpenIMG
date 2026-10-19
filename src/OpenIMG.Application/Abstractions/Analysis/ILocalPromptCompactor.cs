using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Analysis;

public interface ILocalPromptCompactor
{
    PromptCompactionReport Compact(LocalFurnitureContract localContract, FurnitureAnalysis adaptedAnalysis);
}
