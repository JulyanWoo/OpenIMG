using OpenIMG.Core.Models;

namespace OpenIMG.Application.Abstractions.Prompts;

/// <summary>Compiles the deterministic prompt for the final corrective provider.</summary>
public interface ICorrectiveRestorationPromptCompiler
{
    string Compile(CorrectiveRestorationContract contract);
}
