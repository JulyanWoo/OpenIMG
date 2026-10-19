using OpenIMG.Application.Restoration;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class GptPromptTextNormalizerTests
{
    [Fact]
    public void CanonicalizationIgnoresUnicodeWhitespaceAndParagraphFormatting()
    {
        const string compiled = "TASK: Produce the restoration.\nIMAGE 1 is truth.\n\nDo not add furniture.";
        const string composer = "TASK:\u00A0Produce the restoration.\r\n\r\nIMAGE 1 is truth.\u2003Do not add furniture.";

        Assert.Equal(GptPromptTextNormalizer.Canonicalize(compiled), GptPromptTextNormalizer.Canonicalize(composer));
    }

    [Fact]
    public void CanonicalizationRemovesZeroWidthCharactersAndNormalizesUnicode()
    {
        Assert.Equal(
            GptPromptTextNormalizer.Canonicalize("ＡＢＣ\u00A0word"),
            GptPromptTextNormalizer.Canonicalize("ABC word\u200B\u200C\u200D\uFEFF"));
    }

    [Theory]
    [InlineData("first line\nthird line")]
    [InlineData("first line\nsecond")]
    [InlineData("first line\nsecond line\nextra injected text")]
    [InlineData("Do add furniture")]
    public void MissingTruncatedOrChangedContentDoesNotMatch(string composer)
    {
        const string compiled = "first line\nsecond line\nthird line Do not add furniture";

        Assert.NotEqual(GptPromptTextNormalizer.Canonicalize(compiled), GptPromptTextNormalizer.Canonicalize(composer));
    }

    [Fact]
    public void EmptyOrWhitespaceOnlyInputCanonicalizesToEmpty()
    {
        Assert.Equal(string.Empty, GptPromptTextNormalizer.Canonicalize(" \r\n\u00A0\t"));
        Assert.Equal(string.Empty, GptPromptTextNormalizer.Canonicalize(null));
    }
}
