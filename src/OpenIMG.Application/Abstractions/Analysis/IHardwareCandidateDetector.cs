using OpenCvSharp;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Analysis;

public interface IHardwareCandidateDetector
{
    List<HardwareCandidate> DetectHardwareCandidates(Mat image, NormalizedRectangle? furnitureBounds = null);
}
