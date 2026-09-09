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
            : ExpandPath(home));
    }

    public static string? ResolveExecutable(string? explicitExecutable = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitExecutable))
            return ExistingExecutable(ExpandPath(explicitExecutable));
        return OperatingSystem.IsMacOS() ? ResolveMacExecutable() : ResolveWindowsExecutable();
    }

    private static string? ResolveMacExecutable()
    {
        // Finder-launched applications do not inherit an interactive shell's PATH.
        // Prefer the installed desktop CLI so its app-server protocol tracks that app.
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string app in new[] { "/Applications/Codex.app", Path.Combine(userHome, "Applications", "Codex.app") })
        {
            var executable = ExistingExecutable(Path.Combine(app, "Contents", "Resources", "codex"));
            if (executable is not null) return executable;
        }
        foreach (var directory in ExecutableSearchDirectories())
        {
            var executable = ExistingExecutable(Path.Combine(directory, "codex"));
            if (executable is not null) return executable;
        }
        return null;
    }

    /// <summary>Also supplies node to npm's codex launcher when the HUD is started from Finder.</summary>
    internal static IEnumerable<string> ExecutableSearchDirectories()
    {
        var directories = new List<string>();
        directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        if (OperatingSystem.IsMacOS())
        {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (string variable in new[] { "HOMEBREW_PREFIX", "npm_config_prefix", "NPM_CONFIG_PREFIX", "VOLTA_HOME" })
                if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } prefix)
                    directories.Add(Path.Combine(ExpandPath(prefix), "bin"));
            if (Environment.GetEnvironmentVariable("NVM_BIN") is { Length: > 0 } nvmBin) directories.Add(nvmBin);
            directories.AddRange([
                "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin",
                Path.Combine(userHome, ".local", "bin"),
                Path.Combine(userHome, ".npm-global", "bin"),
                Path.Combine(userHome, ".volta", "bin"),
                Path.Combine(userHome, ".nvm", "current", "bin")
            ]);
            // NVM has no stable current link by default. Discover its installed versions without
            // sourcing shell profiles or executing configuration. The search is shallow and bounded.
            string nvmRoot = Environment.GetEnvironmentVariable("NVM_DIR") is { Length: > 0 } configuredNvm
                ? ExpandPath(configuredNvm) : Path.Combine(userHome, ".nvm");
            string versions = Path.Combine(nvmRoot, "versions", "node");
            try
            {
                if (Directory.Exists(versions))
                    directories.AddRange(Directory.EnumerateDirectories(versions)
                        .OrderByDescending(path => ParseNodeVersion(Path.GetFileName(path)))
                        .Take(32).Select(path => Path.Combine(path, "bin")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string raw in directories)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string? directory = null;
            try
            {
                string candidate = ExpandPath(raw.Trim('"'));
                if (Path.IsPathFullyQualified(candidate)) directory = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
            if (directory is not null && seen.Add(directory)) yield return directory;
        }
    }

    private static Version ParseNodeVersion(string directory) =>
        Version.TryParse(directory.TrimStart('v'), out var version) ? version : new Version(0, 0);

    private static string ExpandPath(string value)
    {
        value = Environment.ExpandEnvironmentVariables(value);
        if (!OperatingSystem.IsWindows() && (value == "~" || value.StartsWith("~/", StringComparison.Ordinal)))
        {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            value = value.Length == 1 ? userHome : Path.Combine(userHome, value[2..]);
        }
        return value;
    }

    private static string? ExistingExecutable(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (!OperatingSystem.IsWindows())
            {
                var execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(path) & execute) == 0) return null;
            }
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    private static string? ResolveWindowsExecutable()
    {
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
