namespace OpenIMG.Core.Models;

/// <summary>Durable evidence; image URLs and browser element handles remain browser memory only.</summary>
public sealed record GeneratedCandidateEvidence(
    string OperationId, string SourceSha256, string PromptSha256, string ConversationPath,
    int TurnBaseline, int Ordinal, int Width, int Height, string Fingerprint);
