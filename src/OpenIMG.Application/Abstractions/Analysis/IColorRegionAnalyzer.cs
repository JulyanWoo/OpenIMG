using OpenCvSharp;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Analysis;

public interface IColorRegionAnalyzer
{
    List<LocalColorRegion> AnalyzeColorRegions(Mat image, NormalizedRectangle? furnitureBounds = null);
}
