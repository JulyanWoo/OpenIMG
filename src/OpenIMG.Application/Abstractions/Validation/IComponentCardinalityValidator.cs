using OpenIMG.Core.Models;
namespace OpenIMG.Application.Abstractions.Validation;
public interface IComponentCardinalityValidator
{
    IReadOnlyList<ValidationEvidence> Validate(
        string originalImagePath,
        string restoredImagePath,
        IReadOnlyList<ComponentCardinalityConstraint> constraints,
        string? diagnosticOutputDirectory = null,
        string? assetBaseName = null);
}
