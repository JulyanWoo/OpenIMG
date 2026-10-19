using System.Globalization;
using System.Text;
using OpenIMG.Application.Abstractions.Prompts;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.Application.Restoration;

/// <summary>Renders the typed Original-vs-draft reconciliation contract for GPT.</summary>
public sealed class CorrectiveRestorationPromptCompiler : ICorrectiveRestorationPromptCompiler
{
    public string Compile(CorrectiveRestorationContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var prompt = new StringBuilder();
        prompt.AppendLine("TASK: Reconcile the existing photographic draft using only the local, evidence-backed correction contract below.");
        prompt.AppendLine("IMAGE 1 = ORIGINAL SOURCE PHOTOGRAPH.");
        prompt.AppendLine("IMAGE 2 = GEMINI PHOTOGRAPHIC DRAFT.");
        prompt.AppendLine("Neither image is globally authoritative for every property.");
        prompt.AppendLine("PHYSICAL STRUCTURE: the original is authoritative for permanent components, geometry, count, position, materials, permanent scene details, and visible empty space.");
        prompt.AppendLine("PHOTOGRAPHIC IMPROVEMENT AND APPROVED CLEANUP: preserve valid improvements and temporary-clutter removals already present in the Gemini draft. Do not restore loose clutter merely because it appears in the original.");
        prompt.AppendLine("Do not rebuild the full scene from either image. Make only corrections explicitly supported by the contract; preserve ambiguity rather than inventing geometry.");
        prompt.AppendLine();
        prompt.AppendLine($"ROUTE: {contract.Route}");
        prompt.AppendLine($"ROUTE GUIDANCE: {RouteGuidance(contract.Route)}");

        AppendInstructions(prompt, "PRESERVE FROM DRAFT", contract.PreserveFromDraft, "Preserve the draft's photographic enhancement and valid temporary cleanup.");
        AppendInstructions(prompt, "RESTORE FROM ORIGINAL", contract.RestoreFromOriginal, "Restore source-supported permanent structure");
        AppendInstructions(prompt, "REMOVE UNSUPPORTED FROM DRAFT", contract.RemoveUnsupportedFromDraft, "Remove only localized draft-only unsupported structure");
        AppendInstructions(prompt, "UNCERTAIN — DO NOT INFER", contract.UncertainDoNotInfer, "Do not guess or alter uncertain");

        if (contract.Route == CorrectiveRestorationRoute.NeedsReview)
        {
            prompt.AppendLine();
            prompt.AppendLine("This contract requires human review. Do not generate a corrected image or guess at unresolved physical conflicts.");
        }
        else
        {
            prompt.AppendLine();
            prompt.AppendLine("If the contract has no restore/remove instructions, return the Gemini draft essentially unchanged.");
            prompt.AppendLine("Return one final photographic image only. Do not return explanations, diagrams, or alternative designs.");
        }

        return prompt.ToString().TrimEnd();
    }

    private static void AppendInstructions(StringBuilder prompt, string heading, IReadOnlyCollection<CorrectiveInstruction> instructions, string action)
    {
        prompt.AppendLine();
        prompt.AppendLine(heading + ":");
        if (instructions.Count == 0)
        {
            prompt.AppendLine("- None identified by local evidence.");
            return;
        }

        foreach (var instruction in instructions)
        {
            var safeRegion = string.IsNullOrWhiteSpace(instruction.Region) ? string.Empty : SafeRegion(instruction.Region);
            var region = string.IsNullOrWhiteSpace(safeRegion) ? string.Empty : $" (region {safeRegion})";
            var location = SafeLocation(instruction.Location);
            var detail = instruction.EvidenceType switch
            {
                "PhotographicImprovement" => "photographic enhancement",
                "GeminiCleanup" => "removal of temporary loose clutter",
                _ => SafeFeature(instruction.Feature)
            };
            prompt.AppendLine($"- {action} {detail}{region}{location}.");
        }
    }

    private static string RouteGuidance(CorrectiveRestorationRoute route) => route switch
    {
        CorrectiveRestorationRoute.DeltaCorrection => "no structural conflict was identified; preserve the Gemini draft apart from maintaining listed identities.",
        CorrectiveRestorationRoute.TargetedStructuralCorrection => "apply only the listed localized permanent-structure corrections.",
        _ => "human review is required; no corrected image is authorized for generation."
    };

    private static string SafeFeature(string value) => value switch
    {
        "hydraulic mechanism" or "handle" or "hinge" or "drawer" or "shelf" or "door" or "panel" or "opening" or "support" or "countertop" or "sink" or "wall switch" or "electrical outlet" or "wall structure" or "floor structure" => value,
        _ => "permanent component"
    };

    private static string SafeRegion(string value)
        => value.Length <= 80 && value.Length > 0 && char.IsAsciiLetter(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? value
            : string.Empty;

    private static string SafeLocation(NormalizedRectangle? location)
    {
        if (location is not { } bounds ||
            !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
            return string.Empty;

        bounds = bounds.Clamp();
        return string.Create(CultureInfo.InvariantCulture,
            $" (approximate normalized image region x={bounds.X:F3}, y={bounds.Y:F3}, width={bounds.Width:F3}, height={bounds.Height:F3})");
    }
}
