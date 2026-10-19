using System.Text;

namespace OpenIMG.Application.Restoration;

/// <summary>Canonicalizes prompt content while ignoring formatting-only whitespace.</summary>
public static class GptPromptTextNormalizer
{
    public static string Canonicalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var value = text
            .Normalize(NormalizationForm.FormKC)
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\u200C", string.Empty, StringComparison.Ordinal)
            .Replace("\u200D", string.Empty, StringComparison.Ordinal)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal);

        var result = new StringBuilder(value.Length);
        var whitespacePending = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespacePending = result.Length > 0;
                continue;
            }

            if (whitespacePending) result.Append(' ');
            result.Append(character);
            whitespacePending = false;
        }

        return result.ToString();
    }
}
