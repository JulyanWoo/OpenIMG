using OpenIMG.Application.Configuration;

namespace OpenIMG.Application.Restoration;

/// <summary>
/// Product policy for the future dual-model path. It is deliberately disabled
/// by default until the in-app GPT WebView2 spike passes.
/// </summary>
public sealed class DualModelRestorationPolicy
{
    private readonly OpenImgOptions _options;

    public DualModelRestorationPolicy(OpenImgOptions options) => _options = options;

    public bool Enabled => _options.DualModelRestoration.Enabled;

    public bool ShouldRunCorrectiveStage(string geminiDraftPath) =>
        Enabled && !string.IsNullOrWhiteSpace(geminiDraftPath) && File.Exists(geminiDraftPath);

    public bool ShouldInvokeBarrier4 => false;

    public bool AllowsAutomaticRetry => false;
}
