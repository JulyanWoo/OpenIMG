using OpenIMG.Core.Models;
namespace OpenIMG.Application.Abstractions.Analysis;
public interface IPersistentSceneDetailDetector { IReadOnlyList<SceneDetailCandidate> Detect(string imagePath, LocalFurnitureContract contract, string diagnosticsPath); }
