using Avalonia;
using Avalonia.Controls;
using CodexHud.Core;

namespace CodexHud.Mac;

internal sealed partial class MainWindow
{
    private void BuildSettings()
    {
        _settingsPanel.Children.Add(Toggle("始终置顶", _settings.AlwaysOnTop, value => { _settings.AlwaysOnTop = value; ApplyAppearance(); Save(); }));
        _settingsPanel.Children.Add(Toggle("锁定位置", _settings.PositionLocked, value => { _settings.PositionLocked = value; Save(); }));
        _settingsPanel.Children.Add(Toggle("完成 / 中断时弹出桌面提醒", _settings.CompletionNotifications, value => { _settings.CompletionNotifications = value; Save(); }));
        _settingsPanel.Children.Add(Toggle("提醒声音", _settings.NotificationSound, value => { _settings.NotificationSound = value; Save(); }));
        _settingsPanel.Children.Add(Toggle("显示会话 API 等效费用", _settings.ShowSessionCost, value => { _settings.ShowSessionCost = value; Save(); UpdateSessionCosts(); UpdateWorkload(); }));
        var startup = new CheckBox { Content = "登录自启（下次登录生效）", IsChecked = LoginStartup.IsEnabled };
        bool changing = false;
        startup.IsCheckedChanged += (_, _) =>
        {
            if (changing) return;
            try { LoginStartup.SetEnabled(startup.IsChecked == true); _settingsStatus.Text = "登录自启设置已保存"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            { changing = true; startup.IsChecked = LoginStartup.IsEnabled; changing = false; _settingsStatus.Text = e.Message; }
        };
        _settingsPanel.Children.Add(startup);
        _settingsPanel.Children.Add(Text("缩放", 11));
        var scale = new Slider { Minimum = .8, Maximum = 2, Value = _settings.Scale, TickFrequency = .1 };
        scale.ValueChanged += (_, _) => { _settings.Scale = scale.Value; ApplyAppearance(); Save(); };
        _settingsPanel.Children.Add(scale);
        _settingsPanel.Children.Add(Text("不透明度", 11));
        var opacity = new Slider { Minimum = .45, Maximum = 1, Value = _settings.PanelOpacity };
        opacity.ValueChanged += (_, _) => { _settings.PanelOpacity = opacity.Value; ApplyAppearance(); Save(); };
        _settingsPanel.Children.Add(opacity);
        _settingsPanel.Children.Add(Text("Codex 数据目录（重启挂件后生效）", 11));
        var home = new TextBox { Text = _settings.CodexHome, Watermark = "~/.codex" };
        _settingsPanel.Children.Add(home);
        _settingsPanel.Children.Add(Text("Codex CLI 路径（留空自动查找，重启后生效）", 11));
        var executable = new TextBox { Text = _settings.CodexExecutable, Watermark = "/Applications/Codex.app/Contents/Resources/codex" };
        _settingsPanel.Children.Add(executable);
        _settingsPanel.Children.Add(Button("保存路径", () =>
        {
            try
            {
                var newHome = ExpandHome(home.Text);
                var newExecutable = ExpandHome(executable.Text);
                if (!string.IsNullOrWhiteSpace(newHome) && !Directory.Exists(newHome)) throw new IOException("Codex 数据目录不存在。");
                if (!string.IsNullOrWhiteSpace(newExecutable) && !File.Exists(newExecutable)) throw new IOException("Codex CLI 文件不存在。");
                _settings.CodexHome = string.IsNullOrWhiteSpace(newHome) ? null : Path.GetFullPath(newHome);
                _settings.CodexExecutable = string.IsNullOrWhiteSpace(newExecutable) ? null : Path.GetFullPath(newExecutable);
                Save();
                if (File.Exists(_store.FilePath)) _settingsStatus.Text += "；路径在下次启动时使用，CODEX_HOME 环境变量优先。";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { _settingsStatus.Text = e.Message; }
        }));
        _settingsPanel.Children.Add(_settingsStatus);
        _settingsPanel.Children.Add(Row(Button("隐藏", Hide), Button("退出挂件", () => QuitRequested?.Invoke())));
    }

    private static CheckBox Toggle(string text, bool initial, Action<bool> change)
    {
        var checkbox = new CheckBox { Content = text, IsChecked = initial };
        checkbox.IsCheckedChanged += (_, _) => change(checkbox.IsChecked == true);
        return checkbox;
    }

    private static string? ExpandHome(string? value)
    {
        value = value?.Trim();
        if (value == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return value?.StartsWith("~/", StringComparison.Ordinal) == true
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]) : value;
    }
}
