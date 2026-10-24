using OpenIMG.App.Browser;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class GptDownloadControlPolicyTests
{
    [Fact]
    public void SelectsVisibleViewerDownloadAndIgnoresHiddenPageDuplicate()
    {
        var hiddenPageControl = new GptDownloadControlCandidate(
            "Descargar", null, null, null, IsVisible: false, IsInDialog: false, X: 20, Y: 30);
        var visibleViewerControl = new GptDownloadControlCandidate(
            "Download image", null, null, "download-image", IsVisible: true, IsInDialog: true, X: 120, Y: 80);

        var selected = GptDownloadControlPolicy.Select([hiddenPageControl, visibleViewerControl]);

        Assert.Same(visibleViewerControl, selected);
    }

    [Theory]
    [InlineData("Descargar")]
    [InlineData("Download")]
    public void RecognizesLocalizedDownloadControls(string ariaLabel)
    {
        var candidate = new GptDownloadControlCandidate(
            ariaLabel, null, null, null, IsVisible: true, IsInDialog: false, X: 1, Y: 2);

        Assert.Same(candidate, GptDownloadControlPolicy.Select([candidate]));
    }

    [Fact]
    public void FailsClosedWhenViewerHasMultipleVisibleDownloadControls()
    {
        var candidates = new[]
        {
            new GptDownloadControlCandidate("Download", null, null, null, true, true, 1, 2),
            new GptDownloadControlCandidate(null, "Descargar imagen", null, null, true, true, 3, 4)
        };

        Assert.Null(GptDownloadControlPolicy.Select(candidates));
    }
}
