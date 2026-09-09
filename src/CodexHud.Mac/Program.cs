using Avalonia;

namespace CodexHud.Mac;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("CodexHud.Mac requires macOS. Use CodexHud on Windows.");
            return 1;
        }
        // FileShare.None holds an OS lock for the lifetime of this process; no stale PID file.
        Directory.CreateDirectory(SettingsStore.DefaultDirectory);
        FileStream instance;
        try
        {
            instance = new FileStream(Path.Combine(SettingsStore.DefaultDirectory, "instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Codex HUD is already open or its settings directory is unavailable.");
            return 2;
        }
        using (instance)
            return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()
                .StartWithClassicDesktopLifetime(args);
    }
}
