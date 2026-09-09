using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace CodexHud.Mac;

public sealed class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _stopping, _stopped;

    public override void Initialize()
    {
        Name = "Codex HUD";
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _window = new MainWindow();
            _window.QuitRequested += Quit;
            desktop.MainWindow = _window;
            var menu = new NativeMenu();
            var show = new NativeMenuItem("显示 Codex HUD");
            show.Click += (_, _) => _window.Restore();
            var hide = new NativeMenuItem("隐藏");
            hide.Click += (_, _) => _window.Hide();
            var settings = new NativeMenuItem("设置…");
            settings.Click += (_, _) => { _window.Restore(); _window.OpenPanel("settings"); };
            var quit = new NativeMenuItem("退出 Codex HUD");
            quit.Click += (_, _) => Quit();
            menu.Items.Add(show); menu.Items.Add(hide); menu.Items.Add(settings);
            menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(quit);
            using var icon = AssetLoader.Open(new Uri("avares://CodexHud.Mac/Assets/app.ico"));
            _tray = new TrayIcon { Icon = new WindowIcon(icon), ToolTipText = "Codex HUD", Menu = menu, IsVisible = true };
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
            // Native Quit / logout follows the same asynchronous provider cleanup as our menu.
            desktop.ShutdownRequested += (_, e) => { if (!_stopped) { e.Cancel = true; Quit(); } };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async void Quit()
    {
        if (_stopping) return;
        _stopping = true;
        try { if (_window is not null) await _window.StopAsync(); }
        catch
        {
            // Shutdown callbacks are async void. Do not rethrow cleanup failures into the
            // dispatcher after requesting exit; the controller has already attempted all cleanup.
            System.Diagnostics.Trace.TraceWarning("Codex HUD provider cleanup reported an error during shutdown.");
        }
        finally
        {
            try { _tray?.Dispose(); }
            catch { System.Diagnostics.Trace.TraceWarning("Codex HUD tray cleanup reported an error during shutdown."); }
            finally
            {
                _stopped = true;
                (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            }
        }
    }
}
