using OpenIMG.Core.Models;
namespace OpenIMG.Application.Abstractions.Analysis;
public interface IProtectedAbsenceDetector { IReadOnlyList<ProtectedAbsenceRegion> Detect(string imagePath, LocalFurnitureContract contract, string diagnosticsPath); }
