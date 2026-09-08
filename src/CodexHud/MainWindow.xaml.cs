using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CodexHud.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CodexHud;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store;
    private readonly HudSettings _settings;
    private readonly IQuotaProvider _quota;
    private readonly IHardwareProvider _hardware;
    private readonly IActivityProvider _activity;
    private readonly DesktopActivityProvider _desktopActivity = new();
    private readonly UsageLedgerProvider _usageLedger;
    private readonly QuotaTokenEstimator _tokenEstimator;
    private UsageLedgerSnapshot? _usageData;
    private readonly CancellationTokenSource _stop = new();
    private Task[] _loops = [];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private bool _ready, _exiting, _selecting;
    private volatile bool _hidden, _appPresent;
    private int _forceQuota = 1;
    private QuotaSnapshot? _quotaData;
    private HardwareSnapshot? _hardwareData;
    private ActivitySnapshot? _activityData;
    private readonly ObservableCollection<TaskRow> _taskRows = [];
    private string? _openPanel;
    private DateTimeOffset? _lastResetRequested;
    private readonly string? _uiCheckDirectory;
    private readonly string? _diagnosticDirectory;
    private readonly int _soakSeconds;
    private HudDiagnostics? _diagnostics;

    public MainWindow(string? uiCheckDirectory = null, string? diagnosticDirectory = null, int soakSeconds = 0)
    {
        InitializeComponent();
        TaskList.ItemsSource = _taskRows;
        _uiCheckDirectory = uiCheckDirectory;
        _diagnosticDirectory = diagnosticDirectory;
        _soakSeconds = soakSeconds;
        _store = new SettingsStore(uiCheckDirectory == null ? null : Path.Combine(uiCheckDirectory, "settings"));
        _settings = _store.Load();
        // Explorer/login startup does not inherit the desktop Codex app's private CODEX_HOME.
        // Remember the discovered directory so subsequent launches use the same account/history.
        var environmentHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = CodexLocator.ResolveHome(!string.IsNullOrWhiteSpace(environmentHome) ? environmentHome : _settings.CodexHome);
        _settings.CodexHome = codexHome;
        _store.Save(_settings);
        _quota = new QuotaProvider(codexHome: codexHome);
        _hardware = new HardwareProvider(() => _quota.OwnedProcessId);
        _activity = new ActivityProvider(codexHome);
        _usageLedger = new UsageLedgerProvider(codexHome);
        var directoryKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(codexHome).ToUpperInvariant())))[..12];
        _tokenEstimator = new QuotaTokenEstimator(Path.Combine(_store.DirectoryPath, $"token-estimates-{directoryKey}.json"));
        CodexDirectoryText.Text = codexHome;
        TopmostCheck.IsChecked = _settings.AlwaysOnTop;
        LockCheck.IsChecked = _settings.PositionLocked;
        AttentionNotifyCheck.IsChecked = _settings.AttentionNotifications;
        CompletionNotifyCheck.IsChecked = _settings.CompletionNotifications;
        NotificationSoundCheck.IsChecked = _settings.NotificationSound;
        StartupCheck.IsChecked = WindowPlacementService.StartupEnabled();
        ScaleSlider.Value = _settings.Scale;
        OpacitySlider.Value = _settings.PanelOpacity;
        _ready = true;
        ApplyAppearance();
        SetupTray();
        Loaded += LoadedWindow;
        IsVisibleChanged += (_, _) => { _hidden = !IsVisible; if (!_hidden) Interlocked.Exchange(ref _forceQuota, 1); };
        LocationChanged += (_, _) => { if (_ready && IsLoaded && Details.Visibility != Visibility.Visible) WindowPlacementService.Remember(this, _settings); };
        DpiChanged += (_, _) => { if (_ready) { ResizeToContent(); WindowPlacementService.Restore(this, _settings); } };
        _clock.Tick += (_, _) => { UpdateCountdown(); UpdateEstimate(); UpdateTaskDurations(); };
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.DisplaySettingsChanged += DisplaysChanged;
    }

    private async void LoadedWindow(object sender, RoutedEventArgs e)
    {
        WindowPlacementService.Restore(this, _settings);
        _loops = [Task.Run(HardwareLoop), Task.Run(ActivityLoop), Task.Run(UsageLoop), Task.Run(QuotaLoop)];
        _clock.Start();
        if (_diagnosticDirectory != null)
        {
            _diagnostics = new HudDiagnostics(_diagnosticDirectory, () => _quota.OwnedProcessId, () => new
            {
                quota = _quotaData?.Health.ToString(), quotaObservedAt = _quotaData?.ObservedAt,
                hardware = _hardwareData?.Health.ToString(), hardwareObservedAt = _hardwareData?.ObservedAt,
                activity = _activityData?.Health.ToString(), activityObservedAt = _activityData?.ObservedAt, visible = !_hidden,
                usageLedger = _usageData?.Health.ToString(), usageObservedAt = _usageData?.ObservedAt,
                usageTokens = _usageData?.Counts.TotalTokens, usageThreads = _usageData?.ObservedThreads
            });
            _diagnostics.Start(_soakSeconds);
        }
        if (_uiCheckDirectory != null) await RunUiCheck(_uiCheckDirectory);
    }

    private async Task HardwareLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var data = await _hardware.ReadAsync(_stop.Token);
                _appPresent = data.CodexPresent;
                await Dispatcher.InvokeAsync(() => { _hardwareData = data; UpdateHardware(); });
                await Task.Delay(TimeSpan.FromSeconds(_hidden ? 5 : 1), _stop.Token);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_hardwareData is { } previous)
                    {
                        static MetricSample Old(MetricSample metric) => metric with { Health = SampleHealth.Stale, Detail = "采集暂不可用，显示上次读数" };
                        _hardwareData = previous with { SystemCpu = Old(previous.SystemCpu), CodexCpu = Old(previous.CodexCpu), SystemGpu = Old(previous.SystemGpu), CodexGpu = Old(previous.CodexGpu), SystemDiskReadBytesPerSecond = Old(previous.SystemDiskReadBytesPerSecond), SystemDiskWriteBytesPerSecond = Old(previous.SystemDiskWriteBytesPerSecond), Health = SampleHealth.Stale };
                        UpdateHardware();
                    }
                    GpuDetail.Text = "硬件 · 已过期";
                });
                try { await Task.Delay(5000, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    private async Task ActivityLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var local = await _activity.ReadAsync(_appPresent, _stop.Token);
                var data = await _desktopActivity.EnrichAsync(local, _stop.Token);
                await Dispatcher.InvokeAsync(() => { _activityData = PreserveActivityOnFailure(_activityData, data); UpdateActivity(); });
                await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_activityData is { } previous)
                    {
                        _activityData = previous with { Health = SampleHealth.Stale, Message = "活动采集暂不可用；时长停在最后证据。" };
                        UpdateActivity();
                    }
                    ActivityLabel.Text = "状态待确认"; ActivityHint.Text = "记录暂不可用"; ActiveNumber.Text = "—";
                });
                try { await Task.Delay(10000, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    private async Task UsageLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var usage = await _usageLedger.ReadAsync(_stop.Token);
                await Dispatcher.InvokeAsync(() => { _usageData = usage; UpdateEstimate(); });
                await Task.Delay(TimeSpan.FromSeconds(_hidden ? 15 : 5), _stop.Token);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                await Dispatcher.InvokeAsync(() => { _tokenEstimator.Invalidate("Token 采集暂不可用"); UpdateEstimate(); });
                try { await Task.Delay(10000, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    private async Task QuotaLoop()
    {
        var lastAttempt = DateTimeOffset.MinValue;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (Interlocked.Exchange(ref _forceQuota, 0) == 1 || DateTimeOffset.UtcNow - lastAttempt >= TimeSpan.FromSeconds(_hidden ? 120 : 60))
                {
                    lastAttempt = DateTimeOffset.UtcNow;
                    await _usageLedger.ReadAsync(_stop.Token);
                    var data = await _quota.ReadAsync(_stop.Token);
                    var usage = await _usageLedger.ReadAsync(_stop.Token);
                    await Dispatcher.InvokeAsync(() => { _usageData = usage; _tokenEstimator.Observe(data, usage); _sampledUsageEpoch = usage.Epoch; _quotaData = data; UpdateQuota(); RefreshButton.IsEnabled = true; });
                }
                await Task.Delay(500, _stop.Token);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                await Dispatcher.InvokeAsync(() => { Ring.IsStale = true; _tokenEstimator.Invalidate("额度采样中断"); UpdateEstimate(); QuotaStatusText.Text = "额度 · 已过期"; RefreshButton.IsEnabled = true; });
                try { await Task.Delay(10000, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private void UpdateHardware()
    {
        var h = _hardwareData;
        if (h == null) return;
        ScopeButton.Content = _settings.HardwareScope switch { HardwareScope.System => "整机 ▾", HardwareScope.Codex => "Codex ▾", _ => "整机 / Codex ▾" };
        var scope = _settings.HardwareScope;
        CpuValue.FontSize = GpuValue.FontSize = scope == HardwareScope.Compare ? 10 : 13;
        CpuValue.Text = scope == HardwareScope.Compare ? $"{Percent(h.SystemCpu)}/{Percent(h.CodexCpu)}" : Percent(scope == HardwareScope.System ? h.SystemCpu : h.CodexCpu);
        GpuValue.Text = scope == HardwareScope.Compare ? $"{Percent(h.SystemGpu)}/{Percent(h.CodexGpu)}" : Percent(scope == HardwareScope.System ? h.SystemGpu : h.CodexGpu);
        CpuValue.ToolTip = MetricHint(scope == HardwareScope.System ? h.SystemCpu : h.CodexCpu);
        GpuValue.ToolTip = "该显卡最忙引擎占用；对照顺序：整机 / Codex。\n" + MetricHint(scope == HardwareScope.System ? h.SystemGpu : h.CodexGpu);
        CpuFill.Width = Math.Clamp((scope == HardwareScope.Codex ? h.CodexCpu.Value : h.SystemCpu.Value) ?? 0, 0, 100) / 100 * 98;
        GpuFill.Width = Math.Clamp((scope == HardwareScope.Codex ? h.CodexGpu.Value : h.SystemGpu.Value) ?? 0, 0, 100) / 100 * 98;
        MemoryText.Text = $"系统内存  {Bytes(h.SystemMemoryUsed)} / {Bytes(h.SystemMemoryTotal)}     Codex 私有工作集  {Bytes(h.CodexMemory)}";
        GpuDetail.Text = $"{h.GpuName}  ·  显存 {Bytes(h.GpuMemoryUsed)} / {Bytes(h.GpuMemoryTotal)}";
        GpuDetail.ToolTip = $"Codex 进程组 {h.CodexProcessCount} 个；GPU 按最忙引擎统计。\n{h.Message}";
        DiskReadValue.Text = ByteRate(h.SystemDiskReadBytesPerSecond);
        DiskWriteValue.Text = ByteRate(h.SystemDiskWriteBytesPerSecond);
        DiskReadValue.ToolTip = MetricHint(h.SystemDiskReadBytesPerSecond);
        DiskWriteValue.ToolTip = MetricHint(h.SystemDiskWriteBytesPerSecond);
        DiskDetail.Text = $"磁盘  ·  读 {DiskReadValue.Text}  /  写 {DiskWriteValue.Text}";
        DiskDetail.ToolTip = "整机物理磁盘合计；不随 CPU/GPU 范围切换。";
    }
    private static string Percent(MetricSample m) => m.Value is double v ? $"{v:0}%" + (m.Health is SampleHealth.Stale or SampleHealth.Partial ? "·" : "") : "—";
    private static string Bytes(MetricSample m) => m.Value is double v ? (v >= 1073741824 ? $"{v / 1073741824:0.0} GB" : $"{v / 1048576:0} MB") + (m.Health is SampleHealth.Stale or SampleHealth.Partial ? "·" : "") : "—";
    private static string ByteRate(MetricSample m)
    {
        if (m.Value is not double v || !double.IsFinite(v) || v < 0) return "—";
        string value = v >= 1073741824 ? $"{v / 1073741824:0.00} GiB/s"
            : v >= 1048576 ? $"{v / 1048576:0.00} MiB/s" : v >= 1024 ? $"{v / 1024:0.0} KiB/s" : $"{v:0} B/s";
        return value + (m.Health is SampleHealth.Stale or SampleHealth.Partial ? "·" : "");
    }
    private static string MetricHint(MetricSample m) => $"{m.Detail ?? ""}\n{(m.Health is SampleHealth.Partial ? "部分数据" : m.Health is SampleHealth.Stale ? "数据过期" : "")} · {m.ObservedAt?.ToLocalTime():HH:mm:ss}";

    private void UpdateActivity()
    {
        var a = _activityData;
        if (a == null) return;
        HandleTaskNotices(_noticeTracker.Observe(a, DateTimeOffset.UtcNow));
        UpdateActivitySummary(a, DateTimeOffset.UtcNow);
        var desiredIds = a.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = _taskRows.Count - 1; i >= 0; i--)
            if (!desiredIds.Contains(_taskRows[i].Id)) _taskRows.RemoveAt(i);
        var rowsById = _taskRows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        for (int i = 0; i < a.Tasks.Count; i++)
        {
            var task = a.Tasks[i];
            if (rowsById.TryGetValue(task.Id, out var row))
            {
                int oldIndex = _taskRows.IndexOf(row);
                if (oldIndex != i) _taskRows.Move(oldIndex, i);
                row.UpdateData(task, a, DateTimeOffset.UtcNow);
            }
            else _taskRows.Insert(i, new TaskRow(task, a, DateTimeOffset.UtcNow));
        }
        TaskStatusText.Text = $"{a.Tasks.Count} 项" + (a.LiveTaskCount > 0 ? $" · 实时 {a.LiveTaskCount}" : "") + $" · {a.ObservedAt.ToLocalTime():HH:mm:ss}" + (a.Health is SampleHealth.Stale or SampleHealth.Unavailable ? " · 已过期" : a.Message?.Contains("补齐") == true ? " · 补齐中" : "");
        TaskStatusText.ToolTip = a.Message;
    }

    private static ActivitySnapshot PreserveActivityOnFailure(ActivitySnapshot? previous, ActivitySnapshot incoming) =>
        incoming.Health == SampleHealth.Unavailable && previous is { Tasks.Count: > 0 }
            ? previous with { AppPresent = incoming.AppPresent, Health = SampleHealth.Stale,
                Message = (incoming.Message ?? "活动采集暂不可用") + "；保留上次任务记录，时长截至最后证据。" }
            : incoming;

    private static ActivityState CurrentTaskState(TaskActivity task, ActivitySnapshot snapshot, DateTimeOffset now)
    {
        if (task.State is ActivityState.Completed or ActivityState.Interrupted) return task.State;
        return task.State is ActivityState.ExecutionEvidence or ActivityState.AwaitingApproval or ActivityState.AwaitingInput && snapshot.AppPresent
            && snapshot.Health is SampleHealth.Fresh or SampleHealth.Partial
            && snapshot.ObservedAt <= now && now - snapshot.ObservedAt <= TimeSpan.FromSeconds(120)
            && task.EvidenceAt is { } evidence && evidence <= now && now - evidence <= TimeSpan.FromSeconds(120)
            ? task.State : ActivityState.Unconfirmed;
    }

    private void UpdateActivitySummary(ActivitySnapshot a, DateTimeOffset now)
    {
        var active = a.Tasks.Count(t => CurrentTaskState(t, a, now) == ActivityState.ExecutionEvidence);
        var unknown = a.Tasks.Count(t => CurrentTaskState(t, a, now) == ActivityState.Unconfirmed);
        AppPresence.Text = a.AppPresent ? " / 在线" : " / 未启动";
        ApplyStatusAccent(active > 0 ? ActivityState.ExecutionEvidence : ActivityState.Unconfirmed);
        ActiveNumber.Text = a.Health is SampleHealth.Unavailable or SampleHealth.Stale ? "—" : active.ToString("00");
        ActivityLabel.Text = a.Health == SampleHealth.Unavailable ? "状态不可用" : a.Health == SampleHealth.Stale ? "状态待确认" : active > 0 ? "执行迹象" : unknown > 0 ? "状态待确认" : "暂无执行迹象";
        ActivityHint.Text = a.Health is SampleHealth.Unavailable or SampleHealth.Stale ? "记录暂不可用" : a.Message?.Contains("补齐") == true ? "记录补齐中 · 推断" : unknown > 0 ? $"{unknown} 项待确认 · 推断" : "本地推断";
        UpdateAttentionSummary(a, now, active);
    }

    private void UpdateTaskDurations()
    {
        if (_hidden) return;
        var now = DateTimeOffset.UtcNow;
        if (_activityData is { } snapshot) UpdateActivitySummary(snapshot, now);
        if (Details.Visibility != Visibility.Visible || TasksPanel.Visibility != Visibility.Visible) return;
        foreach (var row in TaskList.Items.OfType<TaskRow>()) row.UpdateTime(now);
    }

    private void UpdateQuota()
    {
        var q = _quotaData;
        if (q == null) return;
        if (_settings.SelectedQuotaKey == null && q.Health is SampleHealth.Fresh or SampleHealth.Partial)
        {
            var initial = QuotaSelection.GetDefault(q.Windows);
            if (initial != null) { _settings.SelectedQuotaKey = initial.Key; SaveSettings(); }
        }
        var selected = q.Windows.FirstOrDefault(w => w.Key == _settings.SelectedQuotaKey);
        Ring.Value = selected?.RemainingPercent ?? double.NaN;
        Ring.IsStale = q.Health is SampleHealth.Stale or SampleHealth.Unavailable;
        QuotaTitle.Text = selected?.Label ?? (_settings.SelectedQuotaKey == null ? "尚未取得额度" : "所选额度不可用");
        QuotaTitle.ToolTip = selected?.Label;
        QuotaPeriod.Text = selected == null ? "点击圆环选择额度" : Period(selected.WindowMinutes);
        _selecting = true;
        var rows = q.Windows.Select(w => new QuotaRow(w.Key, w.Label, w.RemainingPercent is double p ? $"{p:0}%" : "—", $"{Period(w.WindowMinutes)} · 重置 {w.ResetsAt?.ToLocalTime():MM-dd HH:mm}")).ToArray();
        QuotaList.ItemsSource = rows;
        QuotaList.SelectedItem = rows.FirstOrDefault(r => r.Key == _settings.SelectedQuotaKey);
        _selecting = false;
        QuotaStatusText.Text = q.Health is SampleHealth.Stale or SampleHealth.Unavailable ? "额度 · 已过期" : $"更新 {q.ObservedAt.ToLocalTime():HH:mm:ss}";
        QuotaStatusText.ToolTip = q.Message;
        UpdateCountdown();
        UpdateEstimate();
    }
    internal static string Period(int? minutes) => minutes switch { null => "周期未知", <= 0 => "周期未知", 10080 => "每周额度", 1440 => "24 小时额度", var m when m % 1440 == 0 => $"{m / 1440} 天额度", var m when m % 60 == 0 => $"{m / 60} 小时额度", var m => $"{m} 分钟额度" };
    private void UpdateCountdown()
    {
        UpdateResetCredits();
        var q = _quotaData;
        if (q == null) return;
        var w = q.Windows.FirstOrDefault(w => w.Key == _settings.SelectedQuotaKey);
        var stale = q.Health is SampleHealth.Stale or SampleHealth.Unavailable || DateTimeOffset.UtcNow - q.ObservedAt > TimeSpan.FromSeconds(150);
        Ring.IsStale = stale;
        if (w?.ResetsAt == null) { ResetText.Text = stale ? "数据过期 · 等待刷新" : "重置时间未知"; return; }
        var left = w.ResetsAt.Value - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            ResetText.Text = "重置待确认 · 正在刷新";
            Ring.IsStale = true;
            if (_lastResetRequested != w.ResetsAt) { _lastResetRequested = w.ResetsAt; Interlocked.Exchange(ref _forceQuota, 1); }
        }
        else
        {
            var time = left.TotalDays >= 1 ? $"{(int)left.TotalDays}天 {left.Hours}小时" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}小时 {left.Minutes}分" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}分钟";
            ResetText.Text = (stale ? "旧数据 · " : "") + time + "后重置";
        }
        ResetText.ToolTip = $"本地时间 {w.ResetsAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}";
    }

    private void SetupTray()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.FromArgb(20, 26, 23));
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(221, 236, 99), 3);
            g.DrawEllipse(pen, 4, 4, 23, 23);
            g.DrawLine(pen, 17, 8, 12, 17); g.DrawLine(pen, 12, 17, 20, 15); g.DrawLine(pen, 20, 15, 15, 25);
        }
        var h = bitmap.GetHicon();
        using (var temporary = System.Drawing.Icon.FromHandle(h)) _trayIcon = (System.Drawing.Icon)temporary.Clone();
        DestroyIcon(h);
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "Codex 电量 HUD", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (_, _) => Dispatcher.Invoke(() => { if (IsVisible) Hide(); else RestoreFromTray(); }));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(() => { RestoreFromTray(); OpenPanel("settings"); }));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(async () => await StopAndExit()));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        var context = new System.Windows.Controls.ContextMenu();
        AddContext(context, "设置", () => OpenPanel("settings"));
        AddContext(context, "切换硬件范围", () => CycleScope(this, new RoutedEventArgs()));
        AddContext(context, "隐藏到托盘", Hide);
        AddContext(context, "退出", async () => await StopAndExit());
        ContextMenu = context;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private static void AddContext(System.Windows.Controls.ContextMenu menu, string text, Action action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item);
    }
    internal void RestoreFromTray() { Show(); WindowState = WindowState.Normal; WindowPlacementService.Restore(this, _settings); }
    private void DragHud(object sender, MouseButtonEventArgs e)
    {
        if (_settings.PositionLocked || e.ChangedButton != MouseButton.Left) return;
        DependencyObject? p = e.OriginalSource as DependencyObject;
        while (p != null) { if (p is Button) return; p = VisualTreeHelper.GetParent(p); }
        try { DragMove(); WindowPlacementService.Remember(this, _settings); SaveSettings(); } catch (InvalidOperationException) { }
    }
    private void ShowTasks(object sender, RoutedEventArgs e) => TogglePanel("tasks");
    private void ShowQuotas(object sender, RoutedEventArgs e) => TogglePanel("quotas");
    private void ShowSettings(object sender, RoutedEventArgs e) => TogglePanel("settings");
    private void TogglePanel(string panel)
    {
        if (Details.Visibility == Visibility.Visible && _openPanel == panel) CollapseDetails(this, new RoutedEventArgs());
        else OpenPanel(panel);
    }
    private void OpenPanel(string panel)
    {
        _openPanel = panel;
        Details.Visibility = Visibility.Visible;
        TasksPanel.Visibility = panel == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        QuotasPanel.Visibility = panel == "quotas" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = panel == "settings" ? Visibility.Visible : Visibility.Collapsed;
        UpdateTaskDurations();
        ResizeToContent();
        WindowPlacementService.Restore(this, _settings);
    }
    private void CollapseDetails(object sender, RoutedEventArgs e) { Details.Visibility = Visibility.Collapsed; _openPanel = null; ResizeToContent(); WindowPlacementService.Restore(this, _settings); }
    private void ResizeToContent()
    {
        Details.MaxHeight = Math.Max(0, (WindowPlacementService.WorkingHeightDip(this, _settings) - 8) / _settings.Scale - 134);
        SizeToContent = SizeToContent.WidthAndHeight; Shell.InvalidateMeasure(); InvalidateMeasure(); UpdateLayout();
    }
    private void CycleScope(object sender, RoutedEventArgs e) { _settings.HardwareScope = (HardwareScope)(((int)_settings.HardwareScope + 1) % 3); UpdateHardware(); SaveSettings(); }
    private void SelectQuota(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _selecting || QuotaList.SelectedItem is not QuotaRow row) return;
        _settings.SelectedQuotaKey = row.Key; _lastResetRequested = null; SaveSettings(); UpdateQuota();
    }
    private void RefreshQuota(object sender, RoutedEventArgs e) { RefreshButton.IsEnabled = false; Interlocked.Exchange(ref _forceQuota, 1); }
    private void ChangeOptions(object sender, RoutedEventArgs e) { if (!_ready) return; _settings.AlwaysOnTop = TopmostCheck.IsChecked == true; _settings.PositionLocked = LockCheck.IsChecked == true; ApplyAppearance(); SaveSettings(); }
    private void ChangeAppearance(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.Scale = ScaleSlider.Value; _settings.PanelOpacity = OpacitySlider.Value; ApplyAppearance(); SaveSettings();
        if (IsLoaded) WindowPlacementService.Restore(this, _settings);
    }
    private void ApplyAppearance()
    {
        UiScale.ScaleX = UiScale.ScaleY = _settings.Scale;
        Topmost = _settings.AlwaysOnTop;
        Backdrop.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(_settings.PanelOpacity * 255), 20, 26, 23));
        ScaleLabel.Text = $"缩放 {_settings.Scale:P0}"; OpacityLabel.Text = $"不透明度 {_settings.PanelOpacity:P0}";
        ResizeToContent();
    }
    private void ChangeStartup(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        try { WindowPlacementService.SetStartup(StartupCheck.IsChecked == true); SettingsStatus.Text = "启动设置已保存。"; }
        catch
        {
            _ready = false;
            var previous = StartupCheck.IsChecked != true;
            bool confirmed = false;
            try
            {
                try { StartupCheck.IsChecked = WindowPlacementService.StartupEnabled(); confirmed = true; }
                catch { StartupCheck.IsChecked = previous; }
            }
            finally { _ready = true; }
            SettingsStatus.Text = confirmed ? "无法更新登录启动设置。" : "无法更新或确认启动设置；已恢复原显示。";
        }
    }
    private void ChooseCodexDirectory(object sender, RoutedEventArgs e)
    {
        using var picker = new Forms.FolderBrowserDialog { Description = "选择 Codex 数据目录（通常是用户目录中的 .codex）", UseDescriptionForTitle = true, ShowNewFolderButton = false, SelectedPath = _settings.CodexHome ?? "" };
        if (picker.ShowDialog() != Forms.DialogResult.OK) return;
        _settings.CodexHome = picker.SelectedPath;
        CodexDirectoryText.Text = picker.SelectedPath;
        SaveSettings();
        SettingsStatus.Text = "已保存 · 重启生效";
    }
    private void SaveSettings() { if (!_store.Save(_settings)) SettingsStatus.Text = "设置未能保存，请检查目录权限。"; }
    private void ResetPosition(object sender, RoutedEventArgs e) { _settings.X = _settings.Y = null; WindowPlacementService.Restore(this, _settings); WindowPlacementService.Remember(this, _settings); SaveSettings(); }
    private void HideToTray(object sender, RoutedEventArgs e) { SaveSettings(); Hide(); }
    private async void ExitApp(object sender, RoutedEventArgs e) => await StopAndExit();
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Dispatcher.BeginInvoke(() => { _hardware.ResetBaseline(); _tokenEstimator.Invalidate("休眠恢复，重新对齐采样"); UpdateEstimate(); Interlocked.Exchange(ref _forceQuota, 1); WindowPlacementService.Restore(this, _settings); });
    }
    private void DisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { ResizeToContent(); WindowPlacementService.Restore(this, _settings); });
    protected override void OnClosing(CancelEventArgs e) { if (!_exiting) { e.Cancel = true; Hide(); } base.OnClosing(e); }
    internal async Task StopAndExit()
    {
        if (_exiting) return;
        _exiting = true; _clock.Stop(); _stop.Cancel();
        SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
        if (_uiCheckDirectory == null) { if (Details.Visibility != Visibility.Visible) WindowPlacementService.Remember(this, _settings); SaveSettings(); }
        _taskToast?.Close(); _taskToast = null;
        _tray?.Dispose(); _trayIcon?.Dispose();
        try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        try { _hardware.Dispose(); } catch { }
        try { _activity.Dispose(); } catch { }
        try { await _desktopActivity.DisposeAsync(); } catch { }
        try { await _quota.DisposeAsync(); } catch { }
        try { if (_diagnostics != null) await _diagnostics.DisposeAsync(); } catch { }
        finally { _stop.Dispose(); System.Windows.Application.Current.Shutdown(); }
    }

    private async Task RunUiCheck(string directory)
    {
        Directory.CreateDirectory(directory);
        await Task.Delay(15000);
        var results = new List<object>();
        foreach (var scale in new[] { 1d, 1.5d, 2d })
        {
            _settings.Scale = scale; Details.Visibility = Visibility.Collapsed; ApplyAppearance(); UpdateLayout();
            Capture(Path.Combine(directory, $"hud-{scale * 100:0}.png"));
            results.Add(new { scenario = $"scale-{scale}", width = ActualWidth, height = ActualHeight, ringVisible = Ring.ActualWidth == 80, fits = Math.Abs(ActualWidth - 560 * scale) < 2 && Math.Abs(ActualHeight - 134 * scale) < 2 });
        }
        _settings.Scale = 1; ApplyAppearance();
        foreach (var scope in Enum.GetValues<HardwareScope>())
        {
            _settings.HardwareScope = scope; UpdateHardware(); UpdateLayout(); Capture(Path.Combine(directory, $"scope-{scope}.png"));
        }
        foreach (var panel in new[] { "tasks", "quotas", "settings" })
        {
            OpenPanel(panel); UpdateLayout(); Capture(Path.Combine(directory, $"panel-{panel}.png"));
        }
        var originalQuota = _quotaData;
        var originalSelection = _settings.SelectedQuotaKey;
        if (originalQuota is { Windows.Count: > 1 })
        {
            QuotaList.SelectedIndex = 1;
            results.Add(new { scenario = "manual-selection-persists", passed = _store.Load().SelectedQuotaKey == originalQuota.Windows[1].Key && Ring.Value == originalQuota.Windows[1].RemainingPercent });
            _settings.SelectedQuotaKey = "missing-fixture-key";
            UpdateQuota();
            results.Add(new { scenario = "missing-selection-not-replaced", passed = double.IsNaN(Ring.Value) && _settings.SelectedQuotaKey == "missing-fixture-key" });
            _settings.SelectedQuotaKey = null;
            _quotaData = originalQuota with { Health = SampleHealth.Partial };
            UpdateQuota();
            results.Add(new { scenario = "valid-default-in-partial-snapshot", passed = _settings.SelectedQuotaKey == QuotaSelection.GetDefault(originalQuota.Windows)?.Key && !double.IsNaN(Ring.Value) && !Ring.IsStale });
            var previousValue = Ring.Value;
            _quotaData = originalQuota with { Health = SampleHealth.Stale, ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-10) };
            UpdateQuota();
            results.Add(new { scenario = "stale-preserves-reading", passed = Ring.IsStale && Ring.Value == previousValue });
        }
        _quotaData = originalQuota; _settings.SelectedQuotaKey = originalSelection; UpdateQuota();
        var originalActivity = _activityData;
        _activityData = new ActivitySnapshot(DateTimeOffset.UtcNow, true, [], SampleHealth.Unavailable);
        UpdateActivity();
        results.Add(new { scenario = "unknown-is-not-idle", passed = ActivityLabel.Text == "状态不可用" && ActiveNumber.Text == "—" });
        _activityData = originalActivity; UpdateActivity();
        var checkNow = DateTimeOffset.UtcNow;
        var timingFixtures = new[]
        {
            new TaskActivity("running", "时长验证：当前一轮执行迹象", ActivityState.ExecutionEvidence, checkNow, checkNow.AddSeconds(-65), "UI 测试夹具"),
            new TaskActivity("completed", "时长验证：完成后固定", ActivityState.Completed, checkNow.AddSeconds(-5), checkNow.AddSeconds(-65), "UI 测试夹具", checkNow.AddSeconds(-5)),
            new TaskActivity("unconfirmed", "时长验证：证据过期", ActivityState.Unconfirmed, checkNow.AddMinutes(-3), checkNow.AddMinutes(-5), "UI 测试夹具"),
            new TaskActivity("missing", "时长验证：缺少开始记录", ActivityState.ExecutionEvidence, checkNow, null, "UI 测试夹具")
        };
        _activityData = new ActivitySnapshot(checkNow, true, timingFixtures, SampleHealth.Partial);
        var failedActivity = PreserveActivityOnFailure(_activityData, new ActivitySnapshot(checkNow.AddSeconds(5), false, [], SampleHealth.Unavailable));
        var recoveredActivity = PreserveActivityOnFailure(failedActivity, _activityData);
        results.Add(new { scenario = "task-read-failure-retains-and-recovers", passed = failedActivity.Health == SampleHealth.Stale
            && failedActivity.Tasks.Count == timingFixtures.Length && failedActivity.ObservedAt == checkNow && !failedActivity.AppPresent
            && !TaskTiming.Describe(failedActivity.Tasks[0], failedActivity, checkNow.AddSeconds(5)).IsAdvancing
            && recoveredActivity.Health == SampleHealth.Partial && recoveredActivity.AppPresent });
        UpdateActivity(); OpenPanel("tasks");
        var timingRows = TaskList.Items.OfType<TaskRow>().ToArray();
        foreach (var row in timingRows) row.UpdateTime(checkNow);
        results.Add(new { scenario = "task-duration-bindings", passed = timingRows[0].DurationText.Contains("00:01:05")
            && timingRows[1].DurationText.Contains("00:01:00") && timingRows[2].DurationText.Contains("00:02:00")
            && timingRows[2].DurationText.StartsWith("截至证据") && timingRows[3].DurationText.Contains("—") });
        foreach (var row in timingRows) row.UpdateTime(checkNow.AddSeconds(1));
        results.Add(new { scenario = "task-duration-ticks-and-freezes", passed = timingRows[0].DurationText.Contains("00:01:06")
            && timingRows[1].DurationText.Contains("00:01:00") && timingRows[2].DurationText.Contains("00:02:00") });
        timingRows[0].UpdateTime(checkNow.AddSeconds(121));
        UpdateActivitySummary(_activityData, checkNow.AddSeconds(121));
        results.Add(new { scenario = "task-status-expires-without-new-snapshot", passed = timingRows[0].StatusText == "待确认"
            && timingRows[0].DurationText.StartsWith("截至证据") && ActiveNumber.Text == "00" && ActivityLabel.Text == "状态待确认" });
        timingRows[0].UpdateTime(checkNow);
        UpdateActivitySummary(_activityData, checkNow);
        Capture(Path.Combine(directory, "task-duration-fixtures.png"));
        foreach (var scale in new[] { 1d, 1.5d, 2d })
        {
            _settings.Scale = scale; ApplyAppearance();
            Capture(Path.Combine(directory, $"tasks-{scale * 100:0}.png"));
            results.Add(new { scenario = $"expanded-tasks-scale-{scale}", width = ActualWidth, height = ActualHeight,
                availableHeight = WindowPlacementService.WorkingHeightDip(this, _settings), scrollableHeight = DetailsScroll.ScrollableHeight,
                passed = TaskList.ActualWidth >= 460 && DiskReadValue.ActualWidth > 0 && Math.Abs(ActualWidth - 560 * scale) < 2
                    && ActualHeight <= WindowPlacementService.WorkingHeightDip(this, _settings) });
        }
        _settings.Scale = 1; ApplyAppearance();
        _activityData = originalActivity; UpdateActivity();
        RunExtendedUiChecks(results, directory);
        RunEstimateUiChecks(results, directory);
        RunResetCreditUiChecks(results, directory);
        results.Add(new { scenario = "disk-rate-format", passed = ByteRate(new MetricSample(0, checkNow)) == "0 B/s"
            && ByteRate(new MetricSample(1048576, checkNow)) == "1.00 MiB/s"
            && ByteRate(MetricSample.Missing()) == "—" && ByteRate(new MetricSample(1024, checkNow, SampleHealth.Stale)).EndsWith("·") });
        var originalHardware = _hardwareData;
        if (originalHardware != null)
        {
            _hardwareData = originalHardware with { SystemDiskReadBytesPerSecond = new MetricSample(1048576, checkNow), SystemDiskWriteBytesPerSecond = new MetricSample(2048, checkNow, SampleHealth.Stale) };
            var scopeIndependent = true;
            foreach (var scope in Enum.GetValues<HardwareScope>())
            {
                _settings.HardwareScope = scope; UpdateHardware();
                scopeIndependent &= DiskReadValue.Text == "1.00 MiB/s" && DiskWriteValue.Text == "2.0 KiB/s·";
            }
            results.Add(new { scenario = "disk-is-system-in-all-scopes", passed = scopeIndependent });
            _hardwareData = originalHardware; UpdateHardware();
        }
        _settings.PositionLocked = true; LockCheck.IsChecked = true;
        SaveSettings();
        results.Add(new { scenario = "lock-persists", passed = _store.Load().PositionLocked });
        Hide(); await Task.Delay(200); RestoreFromTray();
        results.Add(new { scenario = "hide-restore", visible = IsVisible });
        Details.Visibility = Visibility.Collapsed;
        _settings.Scale = 1; ApplyAppearance();
        var monitorIndex = 0;
        foreach (var monitor in Forms.Screen.AllScreens)
        {
            _settings.X = monitor.WorkingArea.Left + 24; _settings.Y = monitor.WorkingArea.Top + 24;
            WindowPlacementService.Restore(this, _settings);
            await Task.Delay(300); UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            Capture(Path.Combine(directory, $"monitor-{monitorIndex}.png"));
            results.Add(new { scenario = $"monitor-{monitorIndex++}", dpiX = dpi.PixelsPerInchX, dpiY = dpi.PixelsPerInchY,
                width = ActualWidth, height = ActualHeight, passed = Math.Abs(ActualWidth - 560) < 2 && Math.Abs(ActualHeight - 134) < 2 });
        }
        _settings.X = _settings.Y = 999999;
        WindowPlacementService.Restore(this, _settings); WindowPlacementService.Remember(this, _settings);
        var point = new System.Drawing.Point(_settings.X ?? int.MaxValue, _settings.Y ?? int.MaxValue);
        results.Add(new { scenario = "offscreen-placement-recovered", passed = Forms.Screen.AllScreens.Any(s => s.WorkingArea.Contains(point)) });
        results.Add(new { scenario = "live-sources", quota = _quotaData?.Health.ToString(), quotaWindows = _quotaData?.Windows.Count, hardware = _hardwareData?.Health.ToString(), activity = _activityData?.Health.ToString(),
            diskRead = _hardwareData?.SystemDiskReadBytesPerSecond, diskWrite = _hardwareData?.SystemDiskWriteBytesPerSecond,
            taskCountWithStart = _activityData?.Tasks.Count(t => t.StartedAt.HasValue), taskCountWithEnd = _activityData?.Tasks.Count(t => t.EndedAt.HasValue),
            tasksWithTurnTokens = _activityData?.Tasks.Count(t => t.TokenUsage.CurrentTurn.Counts?.TotalTokens.HasValue == true),
            tasksWithThreadTokens = _activityData?.Tasks.Count(t => t.TokenUsage.Thread.Counts?.TotalTokens.HasValue == true),
            accountIdentityAvailable = _quotaData?.AccountKey != null,
            desktopStatus = _activityData?.LiveHealth.ToString(), desktopTaskCount = _activityData?.LiveTaskCount,
            usageLedger = _usageData?.Health.ToString(), usageTokens = _usageData?.Counts.TotalTokens,
            usageThreads = _usageData?.ObservedThreads, estimateState = CurrentEstimate().State.ToString() });
        RunTaskStatusUiChecks(results, directory);
        File.WriteAllText(Path.Combine(directory, "ui-check.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        await StopAndExit();
    }

    private void RunExtendedUiChecks(List<object> results, string directory)
    {
        foreach (var panel in new[] { "tasks", "quotas", "settings" })
        {
            OpenPanel(panel); TogglePanel(panel);
            bool collapsed = Details.Visibility == Visibility.Collapsed;
            TogglePanel(panel);
            results.Add(new { scenario = "toggle-" + panel, passed = collapsed && Details.Visibility == Visibility.Visible && _openPanel == panel });
        }
        OpenPanel("tasks"); TogglePanel("settings");
        results.Add(new { scenario = "toggle-switches-other-panel", passed = SettingsPanel.Visibility == Visibility.Visible && TasksPanel.Visibility == Visibility.Collapsed });
        OpenPanel("settings");
        results.Add(new { scenario = "explicit-open-remains-open", passed = Details.Visibility == Visibility.Visible });
        var oldTopmost = TopmostCheck.IsChecked;
        var peer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(TopmostCheck);
        ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
        results.Add(new { scenario = "themed-checkbox-toggle", passed = TopmostCheck.IsChecked != oldTopmost && _store.Load().AlwaysOnTop == (TopmostCheck.IsChecked == true)
            && TopmostCheck.Template.FindName("CheckOutline", TopmostCheck) != null });
        ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
        double oldOpacity = OpacitySlider.Value;
        OpacitySlider.Value = 0.7;
        System.Windows.Controls.Slider.IncreaseSmall.Execute(null, OpacitySlider);
        results.Add(new { scenario = "themed-slider-command-and-binding", passed = OpacitySlider.Value > 0.7
            && Math.Abs(_store.Load().PanelOpacity - OpacitySlider.Value) < 0.001
            && OpacitySlider.Template.FindName("PART_Track", OpacitySlider) is System.Windows.Controls.Primitives.Track });
        OpacitySlider.Value = oldOpacity;
        Capture(Path.Combine(directory, "settings-themed.png"));

        var originalActivity = _activityData;
        var now = DateTimeOffset.UtcNow;
        var counts = new TokenCounts(1000, 600, 0, 100, 20, 1100);
        var sample = new TokenUsageSample(counts, now, SampleHealth.Fresh, TaskTokenUsage.Scope);
        var tasks = Enumerable.Range(0, 12).Select(i => new TaskActivity("ui-token-" + i, $"任务明细验证 {i + 1:00}",
            ActivityState.ExecutionEvidence, now, now.AddMinutes(-2), "界面测试夹具")
            { TokenUsage = new TaskTokenUsage("ui-turn", sample, sample) }).ToArray();
        tasks[1] = tasks[1] with { TokenUsage = new TaskTokenUsage("next-turn", TokenUsageSample.Missing("本轮尚无 Token 记录"), sample) };
        tasks[2] = tasks[2] with { TokenUsage = new TaskTokenUsage("ui-turn", sample with { Health = SampleHealth.Stale }, sample) };
        tasks[3] = tasks[3] with { TokenUsage = TaskTokenUsage.Missing };
        _activityData = new ActivitySnapshot(now, true, tasks, SampleHealth.Partial);
        UpdateActivity(); OpenPanel("tasks"); UpdateLayout();
        var rows = _taskRows.ToArray();
        results.Add(new { scenario = "task-token-display", passed = rows[0].TokenSummary.Contains("1.1K") && rows[0].TokenHint.Contains("1,100")
            && rows[0].TokenBreakdown.Contains("600") && rows[1].TokenSummary.Contains("本轮 —")
            && rows[1].TokenSummary.Contains("累计 1.1K") && rows[2].TokenSummary.Contains("·") && rows[3].TokenSummary.Contains("累计 —") });
        rows[0].UpdateTime(now.AddSeconds(121));
        results.Add(new { scenario = "task-tokens-expire-without-new-snapshot", passed = rows[0].TokenSummary.Contains("·")
            && rows[0].TokenHint.Contains("采集过期") });
        rows[0].UpdateTime(now);
        Capture(Path.Combine(directory, "tokens-themed.png"));
        var listScroll = Descendants<ScrollViewer>(TaskList).First();
        var listBar = Descendants<System.Windows.Controls.Primitives.ScrollBar>(listScroll).First(b => b.Orientation == Orientation.Vertical);
        listScroll.ScrollToTop(); UpdateLayout();
        System.Windows.Controls.Primitives.ScrollBar.LineDownCommand.Execute(null, listBar);
        UpdateLayout();
        results.Add(new { scenario = "themed-scrollbar-command", passed = listScroll.VerticalOffset > 0
            && ReferenceEquals(listBar.Template, FindResource("HudVerticalScrollBarTemplate")) });
        var track = (System.Windows.Controls.Primitives.Track)listBar.Template.FindName("PART_Track", listBar);
        double previousOffset = listScroll.VerticalOffset;
        listBar.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent, Source = track.Thumb });
        track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, 12));
        track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 12, false));
        UpdateLayout();
        results.Add(new { scenario = "themed-scrollbar-drag-routing", passed = listScroll.VerticalOffset > previousOffset });
        TaskList.SelectedItem = rows[5]; listScroll.ScrollToVerticalOffset(150); UpdateLayout();
        double savedOffset = listScroll.VerticalOffset;
        UpdateActivity(); UpdateLayout();
        results.Add(new { scenario = "task-refresh-preserves-selection-and-scroll", passed = ReferenceEquals(TaskList.SelectedItem, rows[5])
            && Math.Abs(listScroll.VerticalOffset - savedOffset) < 1 });
        double oldMaximum = Details.MaxHeight;
        Details.MaxHeight = 210; UpdateLayout();
        DetailsScroll.ScrollToBottom(); UpdateLayout();
        results.Add(new { scenario = "constrained-details-scroll", passed = Details.ActualHeight <= 211 && DetailsScroll.ScrollableHeight > 0 && DetailsScroll.VerticalOffset > 0 });
        Capture(Path.Combine(directory, "details-constrained-themed.png"));
        Details.MaxHeight = oldMaximum;
        DetailsScroll.ScrollToTop();
        _activityData = originalActivity; UpdateActivity(); ResizeToContent();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    internal void Capture(string path)
    {
        UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
    private sealed class TaskRow(TaskActivity task, ActivitySnapshot snapshot, DateTimeOffset now) : INotifyPropertyChanged
    {
        private DateTimeOffset _now = now;
        public string Id => task.Id;
        public string Title => task.Title;
        public string Source => task.Source;
        public string StatusText => CurrentTaskState(task, snapshot, _now) switch
        {
            ActivityState.Completed => "已完成", ActivityState.Interrupted => "已中断",
            ActivityState.ExecutionEvidence => "执行迹象",
            ActivityState.AwaitingApproval => "等待审批", ActivityState.AwaitingInput => "等待输入",
            _ => "待确认"
        };
        public Brush StatusBrush => StateBrush(CurrentTaskState(task, snapshot, _now));
        public string Detail => task.EvidenceAt.HasValue ? $"最近记录  {task.EvidenceAt.Value.ToLocalTime():MM-dd HH:mm:ss}" : "最近记录  —";
        public string DurationText { get; private set; } = TimingText(task, snapshot, now);
        public string TokenSummary => $"TOKEN   本轮 {TokenTotal(task.TokenUsage?.CurrentTurn)}    累计 {TokenTotal(task.TokenUsage?.Thread)}";
        public string TokenBreakdown => $"本轮 输入 {ShortTokens(task.TokenUsage?.CurrentTurn.Counts?.InputTokens)}  ·  缓存 {ShortTokens(task.TokenUsage?.CurrentTurn.Counts?.CachedInputTokens)}  ·  输出 {ShortTokens(task.TokenUsage?.CurrentTurn.Counts?.OutputTokens)}";
        public string TokenHint => "本任务自身的 Token 记录；不含独立子任务，不是订阅额度百分比。\n缓存包含在输入中，推理包含在输出中，不重复相加。\nK=千；M=百万；B=十亿。\n\n"
            + TokenDetails("本轮", task.TokenUsage?.CurrentTurn) + "\n\n" + TokenDetails("任务累计", task.TokenUsage?.Thread);
        public event PropertyChangedEventHandler? PropertyChanged;
        private bool IsTokenStale(TokenUsageSample? sample) => sample?.Health == SampleHealth.Stale
            || snapshot.Health is SampleHealth.Stale or SampleHealth.Unavailable
            || _now - snapshot.ObservedAt > TimeSpan.FromSeconds(120)
            || (task.State is ActivityState.ExecutionEvidence or ActivityState.Unconfirmed && sample?.ObservedAt is { } time
                && _now - time > TimeSpan.FromSeconds(120));
        private string TokenTotal(TokenUsageSample? sample) => ShortTokens(sample?.Counts?.TotalTokens)
            + (sample?.Counts?.TotalTokens is not null && (sample.Health == SampleHealth.Partial || IsTokenStale(sample)) ? "·" : "");
        private string TokenDetails(string label, TokenUsageSample? sample)
        {
            var c = sample?.Counts;
            var state = IsTokenStale(sample)
                ? "上次记录，采集过期" : sample?.Health == SampleHealth.Partial ? "部分字段可用" : c is null ? "暂无记录" : "已记录";
            return $"{label} · {state}\n总量 {ExactTokens(c?.TotalTokens)}\n输入 {ExactTokens(c?.InputTokens)}（其中缓存 {ExactTokens(c?.CachedInputTokens)}）"
                + $"\n缓存写入 {ExactTokens(c?.CacheWriteInputTokens)}\n输出 {ExactTokens(c?.OutputTokens)}（其中推理 {ExactTokens(c?.ReasoningOutputTokens)}）"
                + $"\n记录时间 {sample?.ObservedAt?.ToLocalTime():MM-dd HH:mm:ss}\n{sample?.Detail ?? "尚无该任务的用量记录"}";
        }
        private static string ExactTokens(long? value) => value is >= 0 ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : "—";
        private static string ShortTokens(long? value) => value switch
        {
            >= 1_000_000_000 => (value.Value / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B",
            >= 1_000_000 => (value.Value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M",
            >= 1_000 => (value.Value / 1_000d).ToString("0.##", CultureInfo.InvariantCulture) + "K",
            >= 0 => value.Value.ToString(CultureInfo.InvariantCulture),
            _ => "—"
        };
        public void UpdateData(TaskActivity value, ActivitySnapshot observation, DateTimeOffset time)
        {
            task = value; snapshot = observation;
            UpdateTime(time);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
        private static string TimingText(TaskActivity task, ActivitySnapshot snapshot, DateTimeOffset now)
        {
            var timing = TaskTiming.Describe(task, snapshot, now);
            return $"{timing.Label}  {timing.Text}";
        }
        public void UpdateTime(DateTimeOffset time)
        {
            var previousStatus = StatusText;
            var previousStale = (IsTokenStale(task.TokenUsage?.CurrentTurn), IsTokenStale(task.TokenUsage?.Thread));
            _now = time;
            if (previousStatus != StatusText)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
            }
            if (previousStale != (IsTokenStale(task.TokenUsage?.CurrentTurn), IsTokenStale(task.TokenUsage?.Thread)))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TokenSummary)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TokenHint)));
            }
            var value = TimingText(task, snapshot, time);
            if (DurationText != value)
            {
                DurationText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationText)));
            }
        }
    }
    private sealed record QuotaRow(string Key, string Title, string Percent, string Detail);
}
