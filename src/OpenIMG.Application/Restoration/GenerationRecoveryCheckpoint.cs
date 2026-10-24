using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;
using OpenIMG.Application.Policies;

namespace OpenIMG.Application.Restoration;

/// <summary>Minimal private database checkpoint; excludes DOM dumps and download/session URLs.</summary>
public sealed record GenerationRecoveryCheckpoint
{
    public string Format { get; init; } = "OpenIMG.GptRecovery.v1";
    public string OperationId { get; init; } = "";
    public string AssetId { get; init; } = "";
    public string SourceSha256 { get; init; } = "";
    public string? ConversationPath { get; init; }
    public int BaselineAssistantResponseCount { get; init; }
    public string PromptSha256 { get; init; } = "";
    public ProviderOperationState State { get; init; }
    public bool SendClicked { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
    public GeneratedCandidateEvidence? Candidate { get; init; }
    public string? DownloadedPath { get; init; }
    public string? DownloadedSha256 { get; init; }

    public static string Capture(GenerationOwnershipContext context, string prompt) => JsonSerializer.Serialize(new GenerationRecoveryCheckpoint
    {
        OperationId = context.GenerationOperationId,
        AssetId = context.AssetId,
        SourceSha256 = context.ExpectedSourceSha256,
        ConversationPath = NormalizeConversationPath(context.ConversationPathAfterSubmit) ?? context.ConversationPathAfterSubmit,
        BaselineAssistantResponseCount = context.BaselineAssistantResponseCount ?? context.ExistingAssistantResponseCount,
        PromptSha256 = HashPrompt(prompt),
        State = context.OperationState,
        SendClicked = context.SendClickedExactlyOnce,
        SubmittedAt = context.PromptSentAt,
        Candidate = context.CandidateEvidence,
        DownloadedPath = context.DownloadedTempPath,
        DownloadedSha256 = context.DownloadedCandidateSha256
    });

    public static GenerationOwnershipContext? Restore(RestorationAttempt attempt, ImageAsset asset, string sourceSha256)
    {
        if (string.IsNullOrWhiteSpace(attempt.RawResponse)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<GenerationRecoveryCheckpoint>(attempt.RawResponse);
            var conversationPath = NormalizeConversationPath(saved?.ConversationPath);
            if (saved is null || saved.Format != "OpenIMG.GptRecovery.v1" || !IsOperationId(saved.OperationId) || saved.OperationId != attempt.ProviderOperationId ||
                saved.AssetId != asset.Id.ToString() || !string.Equals(saved.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase) ||
                saved.PromptSha256 != HashPrompt(attempt.RestorationPrompt) || saved.BaselineAssistantResponseCount < 0 ||
                conversationPath is null) return null;
            return new GenerationOwnershipContext
            {
                GenerationOperationId = saved.OperationId, AssetId = saved.AssetId,
                ExpectedSourcePath = asset.OriginalFilePath, ExpectedSourceSha256 = saved.SourceSha256,
                ConversationPathAfterSubmit = conversationPath,
                BaselineAssistantResponseCount = saved.BaselineAssistantResponseCount,
                ExistingAssistantResponseCount = saved.BaselineAssistantResponseCount,
                OperationState = ProviderOperationState.NeedsRecovery, ProviderName = "GPT",
                SendClickedExactlyOnce = saved.SendClicked || ProviderOperationRecoveryPolicy.MayHaveSubmittedGeneration(saved.State),
                PromptSentAt = saved.SubmittedAt,
                CandidateEvidence = saved.Candidate,
                DownloadedTempPath = saved.DownloadedPath,
                DownloadedCandidateSha256 = saved.DownloadedSha256
            };
        }
        catch (JsonException) { return null; }
    }

    public static bool IsConversationPath(string? value) => NormalizeConversationPath(value) is not null;

    public static string? NormalizeConversationPath(string? value)
    {
        if (value is null || !value.StartsWith("/c/", StringComparison.Ordinal) || value.Length <= 3 || value.EndsWith('/')) return null;
        var identifier = new StringBuilder();
        for (var index = 3; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '%')
            {
                if (index + 2 >= value.Length) return null;
                var high = Hex(value[index + 1]);
                var low = Hex(value[index + 2]);
                if (high < 0 || low < 0) return null;
                character = (char)((high << 4) | low);
                index += 2;
            }
            // A conversation identifier occupies exactly one URI path segment.
            // Accept RFC 3986 pchar characters after a single decode, while
            // rejecting separators, query/fragment markers, percent nesting,
            // Unicode, controls, and dot-segments that a browser could normalize.
            if (!char.IsAsciiLetterOrDigit(character) && !"-._~!$&'()*+,;=:@".Contains(character)) return null;
            identifier.Append(character);
        }
        var normalizedIdentifier = identifier.ToString();
        // ChatGPT's optimistic local route is replaced by the server conversation
        // shortly after Send. It is not a durable conversation identity.
        if (normalizedIdentifier.StartsWith("local-chatgpt:", StringComparison.OrdinalIgnoreCase)) return null;
        if (normalizedIdentifier is "." or "..") return null;
        return "/c/" + normalizedIdentifier;

        static int Hex(char character) => character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            >= 'A' and <= 'F' => character - 'A' + 10,
            _ => -1
        };
    }

    public static bool IsOperationId(string? value) => value?.Length >= 8 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string HashPrompt(string prompt) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(GptPromptTextNormalizer.Canonicalize(prompt))));
}
