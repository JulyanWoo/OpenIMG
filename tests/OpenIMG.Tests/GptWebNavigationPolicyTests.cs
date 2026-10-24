using OpenIMG.App.Browser;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class GptWebNavigationPolicyTests
{
    [Theory]
    [InlineData("https://chatgpt.com/", false)]
    [InlineData("https://chatgpt.com/auth/login", false)]
    [InlineData("https://auth.openai.com/u/login/identifier", false)]
    [InlineData("https://accounts.google.com/signin", false)]
    [InlineData("https://appleid.apple.com/auth/authorize", false)]
    [InlineData("https://login.microsoftonline.com/common/oauth2", false)]
    [InlineData("https://example.com/unrelated", true)]
    [InlineData("about:blank", true)]
    [InlineData("not a url", true)]
    public void ReadinessDoesNotNavigateAwayFromAnExistingLoginOrOAuthPage(string currentUrl, bool expected)
    {
        Assert.Equal(expected, GptWebNavigationPolicy.ShouldNavigateToChatGpt(currentUrl));
    }

    [Fact]
    public void ChatGptHostMustNotAcceptLookalikeDomains()
    {
        Assert.False(GptWebNavigationPolicy.IsChatGptUrl("https://notchatgpt.com/"));
        Assert.False(GptWebNavigationPolicy.IsChatGptUrl("https://chatgpt.com.attacker.test/"));
        Assert.True(GptWebNavigationPolicy.IsChatGptUrl("https://chatgpt.com/"));
        Assert.True(GptWebNavigationPolicy.IsChatGptUrl("https://sub.chatgpt.com/"));
    }

    [Theory]
    [InlineData("https://auth.openai.com/u/login/identifier", true)]
    [InlineData("https://auth0.openai.com/authorize", true)]
    [InlineData("https://auth.openai.com.attacker.test/login", false)]
    [InlineData("https://accounts.google.com/signin", false)]
    public void AuthenticationRouteClassificationUsesTrustedHostBoundaries(string url, bool expected)
    {
        Assert.Equal(expected, GptWebNavigationPolicy.IsAuthenticationUrl(url));
    }
}
