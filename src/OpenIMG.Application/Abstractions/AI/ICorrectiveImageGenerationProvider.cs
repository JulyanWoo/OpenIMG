using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.AI;

/// <summary>
/// Narrow capability for a corrective provider that receives the immutable
/// source and a separate photographic draft.
/// </summary>
public interface ICorrectiveImageGenerationProvider
{
    Task<string> GenerateCorrectedAsync(
        string originalImagePath,
        string restoredDraftPath,
        string correctivePrompt,
        string tempDownloadDirectory,
        GenerationOwnershipContext context,
        CancellationToken cancellationToken = default);
}
