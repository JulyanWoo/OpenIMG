using OpenIMG.Core.Entities;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Orchestration;

public interface IImageProcessingOrchestrator
{
    Task<ProcessingResult> ProcessAssetAsync(
        Project project,
        ImageAsset asset,
        Action<string>? progressReporter = null,
        CancellationToken cancellationToken = default);
}
