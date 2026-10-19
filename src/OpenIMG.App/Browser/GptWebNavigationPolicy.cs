namespace OpenIMG.App.Browser;

/// <summary>
/// Keeps browser navigation decisions separate from GPT DOM automation so
/// authentication redirects can be verified without a live provider session.
/// </summary>
public static class GptWebNavigationPolicy
{
    private static readonly HashSet<string> AuthenticationHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth.openai.com",
        "auth0.openai.com",
        "login.openai.com"
    };

    private static readonly HashSet<string> SignInRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "accounts.google.com",
        "appleid.apple.com",
        "login.microsoftonline.com",
        "login.live.com"
    };

    /// <summary>
    /// Initialize blank controls and recover from unrelated pages. Preserve
    /// ChatGPT and known sign-in/OAuth routes while the user authenticates.
    /// </summary>
    public static bool ShouldNavigateToChatGpt(string? currentUrl)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var parsed))
            return true;

        if (string.Equals(parsed.Scheme, "about", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parsed.OriginalString, "about:blank", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return true;

        return !IsChatGptUrl(currentUrl) &&
            !AuthenticationHosts.Contains(parsed.IdnHost) &&
            !SignInRedirectHosts.Contains(parsed.IdnHost);
    }

    public static bool IsChatGptUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
        (string.Equals(parsed.IdnHost, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
         parsed.IdnHost.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase));

    public static bool IsAuthenticationUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
        AuthenticationHosts.Contains(parsed.IdnHost);
}
