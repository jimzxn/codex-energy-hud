using System.IO;
using System.Windows;
using System.Threading;

namespace CodexHud;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _wait;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var eventSuffix = Environment.UserName.Replace('\\', '_');
        if (e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            try { using var request = EventWaitHandle.OpenExisting("Local\\CodexHud.Exit." + eventSuffix); request.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, error) =>
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "CodexHud-error.log"), DateTimeOffset.Now + " " + error.Exception.GetType().Name + "\n"); } catch { }
            error.Handled = false;
        };
        var check = Argument(e.Args, "--ui-check");
        var diagnostic = Argument(e.Args, "--diagnostics");
        var secondsText = Argument(e.Args, "--soak-seconds");
        var seconds = int.TryParse(secondsText, out var n) ? Math.Clamp(n, 10, 86400) : 0;
        if (check == null)
        {
            var suffix = Environment.UserName.Replace('\\', '_');
            _mutex = new Mutex(true, "Local\\CodexHud.Instance." + suffix, out var fresh);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexHud.Show." + suffix);
            if (!fresh)
            {
                _showEvent.Set();
                if (diagnostic != null)
                {
                    try
                    {
                        Directory.CreateDirectory(diagnostic);
                        File.WriteAllText(Path.Combine(diagnostic, "startup-error.json"), "{\"started\":false,\"reason\":\"already_running\",\"message\":\"请先从托盘退出已有挂件，再启动诊断。\"}");
                    }
                    catch { }
                    Shutdown(2);
                }
                else Shutdown();
                return;
            }
        }
        var window = new MainWindow(check, diagnostic, seconds);
        MainWindow = window;
        if (_showEvent != null) _wait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(window.RestoreFromTray), null, Timeout.Infinite, false);
        if (check == null)
        {
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexHud.Exit." + eventSuffix);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => Dispatcher.BeginInvoke(async () => await window.StopAndExit()), null, Timeout.Infinite, false);
        }
        window.Show();
    }
    private static string? Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _wait?.Unregister(null); _exitWait?.Unregister(null); _showEvent?.Dispose(); _exitEvent?.Dispose(); _mutex?.Dispose();
        base.OnExit(e);
    }
}
