using OpenCvSharp;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Analysis;

public interface ISceneColorAnalyzer
{
    List<SceneColorReference> ExtractSceneColorReferences(
        Mat image,
        NormalizedRectangle? furnitureBounds = null);
}
