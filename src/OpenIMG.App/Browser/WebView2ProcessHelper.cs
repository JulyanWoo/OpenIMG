using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Serilog;

namespace OpenIMG.App.Browser;

public static partial class WebView2ProcessHelper
{
    private static readonly ConcurrentDictionary<int, byte> _trackedProcessIds = new();

    [GeneratedRegex(@"(?:--|-|/)user-data-dir=(?:""([^""]+)""|([^\s]+))", RegexOptions.IgnoreCase)]
    private static partial Regex UserDataDirRegex();

    public static void TrackProcessId(int pid)
    {
        if (pid > 0)
        {
            _trackedProcessIds.TryAdd(pid, 0);
            Log.Information("Tracking OpenIMG WebView2 process PID {Pid}", pid);
        }
    }

    public static IReadOnlyCollection<int> GetTrackedProcessIds()
    {
        return _trackedProcessIds.Keys.ToList();
    }

    public static void ClearTrackedProcessIds()
    {
        _trackedProcessIds.Clear();
    }

    public static bool IsUserDataDirMatching(string? commandLine, string expectedProfilePath)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(expectedProfilePath))
            return false;

        try
        {
            var match = UserDataDirRegex().Match(commandLine);
            if (!match.Success)
                return false;

            var extracted = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(extracted))
                return false;

            var normalizedExtracted = Path.GetFullPath(Environment.ExpandEnvironmentVariables(extracted.Trim())).TrimEnd('\\', '/');
            var normalizedExpected = Path.GetFullPath(Environment.ExpandEnvironmentVariables(expectedProfilePath.Trim())).TrimEnd('\\', '/');

            return string.Equals(normalizedExtracted, normalizedExpected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsProcessOwnedByOpenImg(
        int processId,
        int parentProcessId,
        string? commandLine,
        string expectedProfilePath,
        IReadOnlyCollection<int>? trackedPids = null)
    {
        // Evidence 1: Command line references exact OpenIMG WebView2 user-data directory
        if (IsUserDataDirMatching(commandLine, expectedProfilePath))
        {
            return true;
        }

        // Evidence 2: Confirmed descendant of tracked OpenIMG WebView2 process tree
        var tracked = trackedPids ?? GetTrackedProcessIds();
        if (tracked.Count > 0)
        {
            if (tracked.Contains(processId) || tracked.Contains(parentProcessId))
            {
                return true;
            }
        }

        return false;
    }

    public static void TerminateLingeringWebViewProcesses(string profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath)) return;

        try
        {
            var currentPid = Environment.ProcessId;

            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine, ParentProcessId FROM Win32_Process WHERE Name = 'msedgewebview2.exe'");

            foreach (var obj in searcher.Get())
            {
                var pidObj = obj["ProcessId"];
                var parentPidObj = obj["ParentProcessId"];
                if (pidObj == null) continue;

                var pid = Convert.ToInt32(pidObj);
                var parentPid = parentPidObj != null ? Convert.ToInt32(parentPidObj) : 0;
                var cmd = obj["CommandLine"]?.ToString() ?? string.Empty;

                // Strict check: Only terminate if proven to belong to OpenIMG
                if (IsProcessOwnedByOpenImg(pid, parentPid, cmd, profilePath))
                {
                    // Do not kill ourselves or our current running parent/child if still active
                    if (parentPid != currentPid)
                    {
                        try
                        {
                            var proc = Process.GetProcessById(pid);
                            if (!proc.HasExited)
                            {
                                Log.Warning("Last-resort: Terminating orphaned OpenIMG-owned msedgewebview2 process {Pid} (Parent: {ParentPid})", pid, parentPid);
                                proc.Kill(entireProcessTree: true);
                                proc.WaitForExit(2000);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Failed to terminate orphaned OpenIMG process {Pid}", pid);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error while checking lingering WebView2 processes");
        }
    }

    public static void TerminateAllOurWebViewProcesses(string profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath)) return;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine, ParentProcessId FROM Win32_Process WHERE Name = 'msedgewebview2.exe'");

            foreach (var obj in searcher.Get())
            {
                var pidObj = obj["ProcessId"];
                var parentPidObj = obj["ParentProcessId"];
                if (pidObj == null) continue;

                var pid = Convert.ToInt32(pidObj);
                var parentPid = parentPidObj != null ? Convert.ToInt32(parentPidObj) : 0;
                var cmd = obj["CommandLine"]?.ToString() ?? string.Empty;

                if (IsProcessOwnedByOpenImg(pid, parentPid, cmd, profilePath))
                {
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        if (!proc.HasExited)
                        {
                            Log.Warning("Last-resort: Terminating confirmed OpenIMG msedgewebview2 process {Pid}", pid);
                            proc.Kill(entireProcessTree: true);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static Mutex? _instanceMutex;

    // Scope the lock to runtime data, not executable path: clean-room builds may coexist.
    // Never terminate another application instance to acquire its data/profile.
    public static bool EnsureSingleInstance(string runtimeRoot)
    {
        var canonical = Path.GetFullPath(runtimeRoot).TrimEnd('\\', '/').ToUpperInvariant();
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
        _instanceMutex = new Mutex(false, @"Local\OpenIMG-" + fingerprint);
        try { return _instanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    public static void ReleaseSingleInstance()
    {
        if (_instanceMutex is null) return;
        try { _instanceMutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _instanceMutex.Dispose();
        _instanceMutex = null;
    }
}
