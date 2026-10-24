using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenIMG.App.Helpers;
using OpenIMG.App.ViewModels;
using OpenIMG.App.Browser;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class ReleaseBuildIdentityTests
{
    [Fact]
    public void DisplayedReleaseComesFromDeployedAppAssemblyNotOldHardcodedVersion()
    {
        var assembly = typeof(MainViewModel).Assembly;
        var expected = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var viewModel = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        Assert.Equal("0.1.0-stabilization.1", expected);
        Assert.Equal(expected, viewModel.ReleaseVersion);
        Assert.Equal(expected, FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion);
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(OpenImgBuildIdentity.Snapshot()));
        Assert.Equal(viewModel.ReleaseVersion, snapshot.RootElement.GetProperty("displayedVersion").GetString());
        Assert.Equal(3, snapshot.RootElement.GetProperty("assemblies").GetArrayLength());
    }

    [Theory]
    [InlineData("GeneratedResultDetected")]
    [InlineData("OpeningGeneratedImageViewer")]
    [InlineData("VerifyingResult")]
    [InlineData("RecoveryRequired")]
    public void VisibleResultStagesDoNotKeepDisplayingGenerating(string stage)
    {
        var text = GptOperationDiagnostics.ResultStageDisplayText(stage);
        Assert.NotNull(text);
        Assert.DoesNotContain("Generating", text);
        Assert.DoesNotContain("Generating GPT restoration", text);
    }
}
