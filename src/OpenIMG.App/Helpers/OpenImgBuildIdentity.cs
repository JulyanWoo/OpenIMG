using System.Diagnostics;
using System.IO;
using System.Reflection;
using OpenIMG.Application.Configuration;
using OpenIMG.Infrastructure.Configuration;

namespace OpenIMG.App.Helpers;

/// <summary>One version source for the UI and deployment diagnostics.</summary>
internal static class OpenImgBuildIdentity
{
    public static string DisplayedVersion => typeof(OpenImgBuildIdentity).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown";

    public static object Snapshot() => new
    {
        displayedVersion = DisplayedVersion,
        executableProductVersion = string.IsNullOrEmpty(Environment.ProcessPath) ? null : FileVersionInfo.GetVersionInfo(Environment.ProcessPath).ProductVersion,
        deploymentIntegrity = DeploymentIntegrity.Snapshot(),
        assemblies = new[] { typeof(OpenImgBuildIdentity).Assembly, typeof(OpenImgOptions).Assembly, typeof(SettingsService).Assembly }
            .Select(assembly => new
            {
                name = assembly.GetName().Name,
                assemblyVersion = assembly.GetName().Version?.ToString(),
                informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version,
                loadedFromAppAssemblyDirectory = string.Equals(Path.GetDirectoryName(assembly.Location),
                    Path.GetDirectoryName(typeof(OpenImgBuildIdentity).Assembly.Location), StringComparison.OrdinalIgnoreCase)
            }).ToArray()
    };
}
