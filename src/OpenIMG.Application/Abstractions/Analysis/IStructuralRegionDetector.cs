using OpenCvSharp;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Analysis;

public interface IStructuralRegionDetector
{
    List<LocalStructuralRegion> DetectStructuralRegions(Mat image);
}
