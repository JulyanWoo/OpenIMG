using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.AI;

/// <summary>AI image-generation capabilities, including ownership-scoped flows.</summary>
public interface IImageGenerationProvider
{
    Task<string> GenerateRestorationAsync(string imagePath, string restorationPrompt, string tempDownloadDirectory, CancellationToken cancellationToken = default);
    Task<string> GenerateRestorationScopedAsync(string imagePath, string restorationPrompt, string tempDownloadDirectory, GenerationOwnershipContext context, CancellationToken cancellationToken = default);
    Task<string> GenerateRestorationMultiViewScopedAsync(IReadOnlyList<string> imagePaths, string restorationPrompt, string tempDownloadDirectory, GenerationOwnershipContext context, CancellationToken cancellationToken = default);
}
