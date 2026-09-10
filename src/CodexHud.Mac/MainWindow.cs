using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CodexHud.Core;

namespace CodexHud.Mac;

internal sealed partial class MainWindow : Window
{
    private const double ContentWidth = 652;
    private static readonly IBrush Accent = Brush.Parse("#B7D776");
    private static readonly IBrush Muted = Brush.Parse("#94A395");
    private readonly SettingsStore _store = new();
    private readonly HudSettings _settings;
    private readonly HudController _controller;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _positionSave = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly QuotaRing _ring = new();
    private readonly TextBlock _summary = Text("正在读取", 18), _hint = Text("本地任务", 10);
    private readonly TextBlock _cpu = Text("—", 14), _gpu = Text("—", 14), _disk = Text("DISK  读 — / 写 —", 10);
    private readonly TextBlock _quotaTitle = Text("Codex 剩余额度", 11), _reset = Text("—", 10), _estimate = Text("Token · 校准中", 10);
    private readonly TextBlock _memory = Text("内存 —", 11), _activityStatus = Text("正在读取本地任务…", 11);
    private readonly TextBlock _quotaStatus = Text("等待额度", 11), _estimateDetail = Text("等待采样", 11);
    private readonly TextBlock _settingsStatus = Text("", 10);
    private readonly StackPanel _tasks = new() { Spacing = 7 }, _quotaOptions = new() { Spacing = 5 };
    private readonly StackPanel _tasksPanel = new() { Spacing = 10 }, _quotasPanel = new() { Spacing = 10 }, _settingsPanel = new() { Spacing = 8 };
    private readonly ScrollViewer _details = new() { IsVisible = false, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly StackPanel _layout = new() { Width = ContentWidth, Spacing = 6 };
    private readonly Viewbox _scaled = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
    private readonly Button _scope;
    private string? _panel;
    private bool _ready, _exiting;
    private ActivitySnapshot? _renderedActivity;
    private QuotaSnapshot? _renderedQuota;
    private DateTimeOffset? _resetRequested;
    private readonly List<(TaskActivity Task, TextBlock Timing)> _timingRows = [];
    public event Action? QuitRequested;

    public MainWindow()
    {
        _settings = _store.Load();
        _controller = new HudController(_settings, _store);
        _store.Save(_settings);
        _controller.SelectQuota(_settings.SelectedQuotaKey);
        Title = "Codex HUD";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Background = Brush.Parse("#111912");
        Foreground = Brush.Parse("#D9E4CD");
        FontFamily = new FontFamily("PingFang SC, Menlo");
        SizeToContent = SizeToContent.Height;

        var drag = new Border { Background = Brush.Parse("#27321E"), Height = 12 };
        drag.PointerPressed += (_, e) => { if (!_settings.PositionLocked && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        _layout.Children.Add(drag);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("165,125,*,102"), Margin = new Thickness(12, 0) };
        var activityButton = Button(new StackPanel { Spacing = 5, Children = { Text("CODEX / LOCAL", 11), _summary, _hint } }, () => OpenPanel("tasks"));
        grid.Children.Add(activityButton);
        _scope = Button("整机 ▾", ChangeScope);
        _scope.FontSize = 10;
        var hardware = new StackPanel { Spacing = 4, Children = { _scope, Row(Text("CPU", 10), _cpu), Row(Text("GPU", 10), _gpu) } };
        Grid.SetColumn(hardware, 1); grid.Children.Add(hardware);
        var quotaButton = Button(new StackPanel { Spacing = 6, Children = { _quotaTitle, _reset, _estimate } }, () => OpenPanel("quotas"));
        Grid.SetColumn(quotaButton, 2); grid.Children.Add(quotaButton);
        var ringButton = Button(_ring, () => OpenPanel("quotas"));
        ringButton.Padding = new Thickness(0);
        Grid.SetColumn(ringButton, 3); grid.Children.Add(ringButton);
        _layout.Children.Add(grid);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"), Margin = new Thickness(12, 0, 12, 5) };
        bottom.Children.Add(_disk);
        var taskNav = Button("任务", () => OpenPanel("tasks")); Grid.SetColumn(taskNav, 1); bottom.Children.Add(taskNav);
        var quotaNav = Button("额度", () => OpenPanel("quotas")); Grid.SetColumn(quotaNav, 2); bottom.Children.Add(quotaNav);
        var usageNav = Button("用量", () => OpenPanel("usage")); Grid.SetColumn(usageNav, 3); bottom.Children.Add(usageNav);
        var billingNav = Button("周期", () => OpenPanel("billing")); Grid.SetColumn(billingNav, 4); bottom.Children.Add(billingNav);
        var settingsNav = Button("设置", () => OpenPanel("settings")); Grid.SetColumn(settingsNav, 5); bottom.Children.Add(settingsNav);
        _layout.Children.Add(bottom);
        BuildWorkload(); BuildBilling();
        _layout.Children.Add(_workloadFooter);
        _tasksPanel.Children.Add(_memory); _tasksPanel.Children.Add(_activityStatus); _tasksPanel.Children.Add(_tasks);
        _quotasPanel.Children.Add(_quotaStatus); _quotasPanel.Children.Add(Button("刷新额度", _controller.RefreshQuota));
        _quotasPanel.Children.Add(_quotaOptions); _quotasPanel.Children.Add(_resetCredits); _quotasPanel.Children.Add(_estimateDetail);
        BuildSettings();
        _details.Margin = new Thickness(16, 2, 16, 12);
        _layout.Children.Add(_details);
        _scaled.Child = _layout;
        Content = new Border { BorderBrush = Brush.Parse("#617247"), BorderThickness = new Thickness(1), Child = _scaled };
        ContextMenu = new ContextMenu { ItemsSource = new[] {
            Menu("设置", () => OpenPanel("settings")), Menu("切换硬件范围", ChangeScope),
            Menu("隐藏", Hide), Menu("退出", () => QuitRequested?.Invoke()) } };
        Closing += (_, e) => { if (!_exiting) { e.Cancel = true; Hide(); } };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) { _controller.SetHidden(!IsVisible); if (_ready && IsVisible) UpdateData(); }

        };
        PositionChanged += (_, _) =>
        {
            if (!_ready) return;
            _settings.X = Position.X; _settings.Y = Position.Y;
            _positionSave.Stop(); _positionSave.Start();
        };
        _positionSave.Tick += (_, _) => { _positionSave.Stop(); Save(); };
        Opened += (_, _) =>
        {
            if (_ready) return;
            _ready = true; RestorePosition(); ApplyAppearance(); _controller.Start(); _clock.Start();
        };
        Screens.Changed += DisplaysChanged;
        _controller.Changed += UpdateData;
        _clock.Tick += (_, _) => { if (IsVisible) { UpdateClock(); UpdateSummary(); UpdateWorkload(); UpdateSessionCosts(); UpdateBilling(); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && _panel is not null) OpenPanel(_panel); };
        ApplyAppearance();
    }

    public void Restore() { Show(); RestorePosition(); Activate(); _controller.RefreshQuota(); }

    public void OpenPanel(string panel)
    {
        _panel = _panel == panel ? null : panel;
        _details.IsVisible = _panel is not null;
        _details.Content = _panel switch { "tasks" => _tasksPanel, "quotas" => _quotasPanel, "settings" => _settingsPanel, "usage" => _workloadPanel, "billing" => _billingPanel, _ => null };
        ApplyAppearance();
        if (_ready) { UpdateWorkload(); UpdateBilling(); UpdateClock(); UpdateSessionCosts(); Dispatcher.UIThread.Post(RestorePosition); }
    }

    private void ChangeScope()
    {
        _settings.HardwareScope = (HardwareScope)(((int)_settings.HardwareScope + 1) % 3);
        Save(); UpdateHardware();
    }

    private void ApplyAppearance()
    {
        Topmost = _settings.AlwaysOnTop;
        Opacity = _settings.PanelOpacity;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var available = screen?.WorkingArea;
        var scaling = screen?.Scaling ?? 1;
        var scale = Math.Min(_settings.Scale, available is { } area ? Math.Max(.5, area.Width / scaling / (ContentWidth + 4)) : 2);
        Width = ContentWidth * scale + 2;
        _scaled.Width = ContentWidth * scale;
        _details.MaxHeight = Math.Max(80, Math.Min(480, ((available?.Height ?? 900) / scaling - 28) / scale - 215));
    }

    private void DisplaysChanged(object? sender, EventArgs args)
    {
        if (!_ready || _exiting) return;
        ApplyAppearance();
        Dispatcher.UIThread.Post(RestorePosition);
    }
    private void RestorePosition()
    {
        var point = _settings.X is { } x && _settings.Y is { } y ? new PixelPoint(x, y) : (PixelPoint?)null;
        var screen = point is { } saved ? Screens.ScreenFromPoint(saved) : Screens.Primary;
        screen ??= Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        int width = (int)Math.Ceiling(Width * screen.Scaling);
        int height = (int)Math.Ceiling((double.IsFinite(Height) ? Height : 215 * _settings.Scale) * screen.Scaling);
        Position = new PixelPoint(Math.Clamp(point?.X ?? area.X + (area.Width - width) / 2, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(point?.Y ?? area.Y + 20, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    private void Save() => _settingsStatus.Text = _store.Save(_settings) ? "已保存" : "保存失败，请检查设置目录权限";

    public async Task StopAsync()
    {
        _exiting = true; _clock.Stop(); _positionSave.Stop(); _toast?.Close(); CloseBillingReport(); Save();
        Screens.Changed -= DisplaysChanged;
        _controller.Changed -= UpdateData;
        await _controller.DisposeAsync();
    }

    private static TextBlock Text(string value, double size) => new()
    { Text = value, FontSize = size, Foreground = Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static Button Button(object content, Action click)
    {
        var button = new Button { Content = content, Background = Brushes.Transparent, Foreground = Accent, Padding = new Thickness(5, 3),
            HorizontalContentAlignment = HorizontalAlignment.Left, FontSize = 11 };
        button.Click += (_, _) => click(); return button;
    }
    private static MenuItem Menu(string header, Action click) { var item = new MenuItem { Header = header }; item.Click += (_, _) => click(); return item; }
    private static StackPanel Row(params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var child in children) panel.Children.Add(child); return panel;
    }
    private static void Hint(Control control, string? text) => ToolTip.SetTip(control, text);
}
