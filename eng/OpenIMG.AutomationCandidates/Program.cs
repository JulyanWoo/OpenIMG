using System.IO;
using System.Text.Json;
using System.Windows;

namespace OpenIMG.AutomationCandidates;

internal static class Program
{
    public const string Prompt = "Restore the fixture photograph without redesign.";
    public const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aNp0AAAAASUVORK5CYII=";
    public static readonly string[] Cases = ["normal", "existing-session", "new-route", "gallery", "two-options", "viewer-unmount", "viewer-route", "restart-after-submit", "restart-after-generation", "download-interrupted", "home-redirect", "wrong-conversation", "wrong-prompt", "rate-limit", "warning", "network", "app-restart", "clean-profile", "existing-profile", "restart-viewer-open", "failed-route-recovery", "duplicate-send", "attachment-no-chooser", "sign-in-with-visible-composer"];
    public static bool ExpectedDownload(string name) => name is not ("download-interrupted" or "home-redirect" or "wrong-conversation" or "wrong-prompt" or "rate-limit" or "warning" or "network" or "failed-route-recovery" or "sign-in-with-visible-composer");
    public static int ExpectedSends(string name) => name == "sign-in-with-visible-composer" ? 0 : 1;
    public static string FixtureMode(string name) => name is "existing-session" or "new-route" or "restart-after-submit" or "restart-after-generation" or "app-restart" or "clean-profile" or "existing-profile" or "restart-viewer-open" or "failed-route-recovery" or "duplicate-send" ? "normal" : name;
    public static string Page(string mode) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "chat.html"))
        .Replace("__SCENARIO__", JsonSerializer.Serialize(FixtureMode(mode)), StringComparison.Ordinal);

    [STAThread]
    public static int Main(string[] args)
    {
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exit = 1;
        application.Startup += async (_, _) =>
        {
            try
            {
                if (args.Length == 1 && args[0] == "playwright-auth")
                {
                    await PlaywrightCandidate.OpenForManualAuthenticationAsync();
                    exit = 0;
                    return;
                }
                if (args.Length < 2 || args[0] is not ("webview" or "playwright" or "provider"))
                    throw new ArgumentException("Use webview|playwright|provider <result.json> [scenario], or playwright-auth (no generation).");
                var selected = args.Length > 2 ? new[] { args[2] } : Cases;
                var results = new List<CaseResult>();
                foreach (var scenario in selected)
                {
                    var result = args[0] == "playwright" ? await PlaywrightCandidate.RunAsync(scenario)
                        : args[0] == "provider" ? await ProductionProviderCheck.RunAsync(scenario) : await WebViewCandidate.RunAsync(scenario);
                    results.Add(result);
                    Console.WriteLine(JsonSerializer.Serialize(result));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
                await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { backend = args[0], passed = results.Count(r => r.Passed), total = results.Count, results }, new JsonSerializerOptions { WriteIndented = true }));
                exit = results.All(r => r.Passed) ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); }
            finally { application.Shutdown(); }
        };
        application.Run();return exit;
    }
}

internal sealed record CaseResult(string Scenario, bool Passed, bool Downloaded, int Sends, int NativeDownloads, string FinalState, string? Error);
