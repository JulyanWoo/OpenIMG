using OpenIMG.Core.Enums;

namespace OpenIMG.Core.Models;

public record RemoteValidationResult
{
    public RemoteValidationRequirement Requirement { get; init; }
    public List<RemoteValidationReason> Reasons { get; init; } = new();
    public string Explanation { get; init; } = string.Empty;
    public bool WouldCallGemini => Requirement == RemoteValidationRequirement.Required;
}
