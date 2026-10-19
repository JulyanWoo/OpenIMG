using System;
using System.IO;
using System.Linq;

namespace OpenIMG.Tests;

internal static class TestArtifactWorkspace
{
    public static string Create(string purpose)
    {
        var safePurpose = string.Concat(purpose.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
        var path = Path.Combine(Path.GetTempPath(), "OpenIMG.Tests", $"{safePurpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(start);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "OpenIMG.slnx")))
                    return current.FullName;
                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("OpenIMG repository root could not be located from the test process.");
    }
}
