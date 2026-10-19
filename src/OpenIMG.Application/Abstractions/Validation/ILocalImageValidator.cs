using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Validation;

public interface ILocalImageValidator
{
    Task<LocalValidationReport> ValidateAsync(
        string originalImagePath,
        string restoredImagePath,
        FurnitureAnalysis analysis,
        string diagnosticsDirectory,
        string assetBaseName,
        CancellationToken cancellationToken = default);
}
