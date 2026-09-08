using System.Diagnostics;

namespace CodexHud.Core;

/// <summary>Finds the local CLI without reading account credentials or pinning a desktop release hash.</summary>
public static class CodexLocator
{
    public static string ResolveHome(string? explicitHome = null)
    {
        var home = explicitHome ?? Environment.GetEnvironmentVariable("CODEX_HOME");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : Environment.ExpandEnvironmentVariables(home));
    }

    public static string? ResolveExecutable(string? explicitExecutable = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitExecutable))
            return File.Exists(explicitExecutable) ? Path.GetFullPath(explicitExecutable) : null;

        var desktopBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        // A running desktop CLI is a stronger version signal than download-directory timestamps.
        // The process object is disposed even when its module is inaccessible or exits during inspection.
        var running = new List<(string Path, DateTime Started)>();
        foreach (var process in Process.GetProcessesByName("codex"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && path.StartsWith(desktopBin + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(path))
                        running.Add((path, process.StartTime));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        var active = running.OrderBy(x => x.Started).FirstOrDefault().Path;
        if (active is not null) return active;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var path = Path.Combine(Environment.ExpandEnvironmentVariables(directory.Trim('"')), "codex.exe");
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }

        try
        {
            if (Directory.Exists(desktopBin))
                return Directory.EnumerateDirectories(desktopBin)
                    .Select(directory => new FileInfo(Path.Combine(directory, "codex.exe")))
                    .Where(file => file.Exists).OrderByDescending(file => file.LastWriteTimeUtc)
                    .Select(file => file.FullName).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
