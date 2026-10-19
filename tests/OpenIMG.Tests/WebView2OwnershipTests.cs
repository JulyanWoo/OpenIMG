using System.IO;
using OpenIMG.App.Browser;
using Xunit;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public class WebView2OwnershipTests
{
    private const string ExpectedProfile = @"C:\Users\TestUser\AppData\Local\OpenIMG\WebView2Profile";

    [Fact]
    public void IsUserDataDirMatching_ExactQuotedMatch_ReturnsTrue()
    {
        var cmd = "\"C:\\Program Files (x86)\\Microsoft\\EdgeWebView\\Application\\msedgewebview2.exe\" --embedded-browser-webview=1 --user-data-dir=\"C:\\Users\\TestUser\\AppData\\Local\\OpenIMG\\WebView2Profile\"";
        var result = WebView2ProcessHelper.IsUserDataDirMatching(cmd, ExpectedProfile);
        Assert.True(result);
    }

    [Fact]
    public void IsUserDataDirMatching_UnquotedMatch_ReturnsTrue()
    {
        var cmd = "C:\\msedgewebview2.exe --type=gpu-process --user-data-dir=C:\\Users\\TestUser\\AppData\\Local\\OpenIMG\\WebView2Profile /prefetch:2";
        var result = WebView2ProcessHelper.IsUserDataDirMatching(cmd, ExpectedProfile);
        Assert.True(result);
    }

    [Fact]
    public void IsUserDataDirMatching_TrailingSlashVariation_ReturnsTrue()
    {
        var cmd = "C:\\msedgewebview2.exe --user-data-dir=\"C:\\Users\\TestUser\\AppData\\Local\\OpenIMG\\WebView2Profile\\\"";
        var result = WebView2ProcessHelper.IsUserDataDirMatching(cmd, ExpectedProfile + "\\");
        Assert.True(result);
    }

    [Fact]
    public void IsUserDataDirMatching_GoogleDriveProfile_ReturnsFalse()
    {
        var cmd = "\"C:\\Program Files (x86)\\Microsoft\\EdgeWebView\\Application\\msedgewebview2.exe\" --embedded-browser-webview=1 --webview-exe-name=GoogleDriveFS.exe --user-data-dir=\"C:\\Users\\TestUser\\AppData\\Local\\Google\\DriveFS\\webview2_user_data\\EBWebView\"";
        var result = WebView2ProcessHelper.IsUserDataDirMatching(cmd, ExpectedProfile);
        Assert.False(result);
    }

    [Fact]
    public void IsUserDataDirMatching_WindowsSearchProfile_ReturnsFalse()
    {
        var cmd = "\"C:\\Program Files (x86)\\Microsoft\\EdgeWebView\\Application\\msedgewebview2.exe\" --user-data-dir=\"C:\\Users\\TestUser\\AppData\\Local\\Packages\\MicrosoftWindows.Client.CBS\\EBWebView\"";
        var result = WebView2ProcessHelper.IsUserDataDirMatching(cmd, ExpectedProfile);
        Assert.False(result);
    }

    [Fact]
    public void IsUserDataDirMatching_ProfileMentionedInOtherArgument_ReturnsFalse()
    {
        var cmd = "msedgewebview2.exe --custom-flag=\"OpenIMG\\WebView2Profile\" --user-data-dir=\"C:\\OtherApp\\Profile\"";
        var result = WebView2ProcessHelper.IsUserDataDirMatching(cmd, ExpectedProfile);
        Assert.False(result);
    }

    [Fact]
    public void IsProcessOwnedByOpenImg_TrackedProcessId_ReturnsTrue()
    {
        var tracked = new[] { 12345, 67890 };
        var cmdWithoutUserData = "msedgewebview2.exe --type=renderer";

        var result = WebView2ProcessHelper.IsProcessOwnedByOpenImg(
            processId: 12345,
            parentProcessId: 99999,
            commandLine: cmdWithoutUserData,
            expectedProfilePath: ExpectedProfile,
            trackedPids: tracked);

        Assert.True(result);
    }

    [Fact]
    public void IsProcessOwnedByOpenImg_TrackedParentProcessId_ReturnsTrue()
    {
        var tracked = new[] { 12345 };
        var cmdWithoutUserData = "msedgewebview2.exe --type=utility";

        var result = WebView2ProcessHelper.IsProcessOwnedByOpenImg(
            processId: 54321,
            parentProcessId: 12345,
            commandLine: cmdWithoutUserData,
            expectedProfilePath: ExpectedProfile,
            trackedPids: tracked);

        Assert.True(result);
    }

    [Fact]
    public void IsProcessOwnedByOpenImg_UnrelatedProcess_ReturnsFalse()
    {
        var tracked = new[] { 12345 };
        var unrelatedCmd = @"msedgewebview2.exe --user-data-dir=\""C:\Users\TestUser\AppData\Local\Microsoft\Teams\EBWebView\""";

        var result = WebView2ProcessHelper.IsProcessOwnedByOpenImg(
            processId: 99999,
            parentProcessId: 88888,
            commandLine: unrelatedCmd,
            expectedProfilePath: ExpectedProfile,
            trackedPids: tracked);

        Assert.False(result);
    }

    [Fact]
    public void IsProcessOwnedByOpenImg_NullCommandLine_UntrackedPid_ReturnsFalse()
    {
        var result = WebView2ProcessHelper.IsProcessOwnedByOpenImg(
            processId: 99999,
            parentProcessId: 88888,
            commandLine: null,
            expectedProfilePath: ExpectedProfile,
            trackedPids: new[] { 11111 });

        Assert.False(result);
    }
}
