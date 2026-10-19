using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Analysis;

public interface ILocalAnalysisEngine
{
    Task<LocalFurnitureContract> AnalyzeAsync(string imagePath, CancellationToken cancellationToken = default);
}
