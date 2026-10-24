using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace OpenIMG.App.Helpers;

internal static class DeploymentIntegrity
{
    private static readonly string[] Assemblies = ["OpenIMG.App.dll", "OpenIMG.Application.dll", "OpenIMG.Infrastructure.dll", "OpenIMG.Core.dll"];
    public static object Snapshot()
    {
        string? runtime;
        try { runtime = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { runtime = null; }
        return new { backendVersion = "WebView2/stabilization-v1", webView2RuntimeVersion = runtime,
            manifestPresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "build-manifest.json")),
            errors = Verify(), assemblies = Assemblies.Select(name =>
            {
                var path = Path.Combine(AppContext.BaseDirectory, name);
                return new { name, sha256 = File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null };
            }).ToArray() };
    }
    public static string[] Verify()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "build-manifest.json");
        if (!File.Exists(manifest)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var entries = document.RootElement.GetProperty("assemblies");
            var errors = new List<string>();
            foreach (var name in Assemblies)
            {
                var matches = entries.EnumerateArray().Where(item => item.GetProperty("name").GetString() == name).ToArray();
                var path = Path.Combine(AppContext.BaseDirectory, name);
                if (matches.Length != 1 || !File.Exists(path) || matches[0].GetProperty("sha256").GetString() != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
                    errors.Add(name);
            }
            var version = document.RootElement.GetProperty("version").GetString();
            var actual = typeof(DeploymentIntegrity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (version != actual) errors.Add("ApplicationVersion");
            return errors.ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return ["InvalidManifest"]; }
    }
}
