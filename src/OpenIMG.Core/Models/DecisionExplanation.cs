using System.Collections.Generic;

namespace OpenIMG.Core.Models;

public record DecisionExplanation(
    List<string> PrimaryDecisionReasons,
    List<string> SupportingWarnings,
    List<string> NonBlockingPhotographicDifferences
)
{
    public DecisionExplanation() : this(new List<string>(), new List<string>(), new List<string>()) { }
}
