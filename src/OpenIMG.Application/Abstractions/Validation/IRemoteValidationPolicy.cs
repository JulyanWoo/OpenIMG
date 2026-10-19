using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Validation;

public interface IRemoteValidationPolicy
{
    RemoteValidationResult Evaluate(
        LocalValidationReport localReport,
        ValidationDecision localDecision,
        LocalFurnitureContract? localContract = null,
        LocalAnalysisRisk? analysisRisk = null);
}
