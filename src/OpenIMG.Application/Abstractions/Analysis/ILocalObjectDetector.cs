using OpenCvSharp;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Analysis;

public interface ILocalObjectDetector
{
    bool IsModelAvailable { get; }
    LocalModelInfo? GetModelInfo();
    Task<List<LocalDetectedObject>> DetectObjectsAsync(Mat image, CancellationToken cancellationToken = default);
}
