using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CodexHud.Core;

namespace CodexHud.Mac;

internal sealed partial class MainWindow
{
    private readonly TaskNoticeTracker _notices = new();
    private TaskNotice? _recentNotice;
    private DateTimeOffset _recentUntil;
    private Window? _toast;
    private readonly TextBlock _resetCredits = Text("重置卡 —", 12);

    private void UpdateData()
    {
        if (IsVisible) UpdateHardware();
        if (_controller.Activity is { } activity)
        {
            // Consume observations in the menu bar too; the tracker deduplicates unchanged snapshots.
            ShowNotices(_notices.Observe(activity, DateTimeOffset.UtcNow));
            if (IsVisible && !ReferenceEquals(activity, _renderedActivity))
            {
                _renderedActivity = activity;
                RenderTasks(activity);
            }
        }
        if (_controller.Quota is { } quota && !ReferenceEquals(quota, _renderedQuota))
        {
            if (_settings.SelectedQuotaKey is null && quota.Health is SampleHealth.Fresh or SampleHealth.Partial)
            {
                _settings.SelectedQuotaKey = QuotaSelection.GetDefault(quota.Windows)?.Key;
                _controller.SelectQuota(_settings.SelectedQuotaKey);
                Save();
            }
            if (IsVisible) { _renderedQuota = quota; RenderQuotaOptions(); }
        }
        if (IsVisible) { UpdateSummary(); UpdateClock(); UpdateWorkload(); UpdateSessionCosts(); }
        UpdateBilling();
    }

    private void UpdateHardware()
    {
        var scope = _settings.HardwareScope;
        _scope.Content = scope switch { HardwareScope.System => "整机 ▾", HardwareScope.Codex => "Codex ▾", _ => "整机 / Codex ▾" };
        if (_controller.Hardware is not { } h) return;
        _cpu.Text = scope == HardwareScope.Compare ? $"{Percent(h.SystemCpu)}/{Percent(h.CodexCpu)}" : Percent(scope == HardwareScope.Codex ? h.CodexCpu : h.SystemCpu);
        _gpu.Text = scope == HardwareScope.Compare ? $"{Percent(h.SystemGpu)}/{Percent(h.CodexGpu)}" : Percent(scope == HardwareScope.Codex ? h.CodexGpu : h.SystemGpu);
        _cpu.FontSize = _gpu.FontSize = scope == HardwareScope.Compare ? 11 : 14;
        Hint(_cpu, "对照顺序：整机 / Codex\n" + MetricHint(h.SystemCpu) + "\n" + MetricHint(h.CodexCpu));
        Hint(_gpu, MetricHint(h.SystemGpu) + "\n" + MetricHint(h.CodexGpu));
        _disk.Text = $"DISK / 整机  读 {Bytes(h.SystemDiskReadBytesPerSecond, true)}  写 {Bytes(h.SystemDiskWriteBytesPerSecond, true)}";
        Hint(_disk, MetricHint(h.SystemDiskReadBytesPerSecond) + "\n" + MetricHint(h.SystemDiskWriteBytesPerSecond));
        _memory.Text = $"系统内存 {Bytes(h.SystemMemoryUsed)} / {Bytes(h.SystemMemoryTotal)}   Codex RSS {Bytes(h.CodexMemory)}\nCodex 进程组 {h.CodexProcessCount} 个 · GPU / 显存 —";
        Hint(_memory, MetricHint(h.SystemMemoryUsed) + "\n" + MetricHint(h.CodexMemory) + "\n" + h.Message + "\n" + h.GpuMemoryUsed.Detail);
    }

    private void UpdateSummary()
    {
        if (_controller.Activity is not { } snapshot) return;
        var now = DateTimeOffset.UtcNow;
        int active = snapshot.Tasks.Count(t => CurrentState(t, snapshot, now) == ActivityState.ExecutionEvidence);
        int unknown = snapshot.Tasks.Count(t => CurrentState(t, snapshot, now) == ActivityState.Unconfirmed);
        _summary.Foreground = StateBrush(active > 0 ? ActivityState.ExecutionEvidence : ActivityState.Unconfirmed);
        _summary.Text = snapshot.Health is SampleHealth.Stale or SampleHealth.Unavailable ? "状态待确认"
            : active > 0 ? $"{active:00} 执行迹象" : unknown > 0 ? $"{unknown:00} 待确认" : "暂无执行迹象";
        _hint.Text = snapshot.Message?.Contains("补齐", StringComparison.Ordinal) == true ? "记录补齐中 · 推断" : "本地推断";
        if (_recentNotice is { } recent && snapshot.Tasks.Any(t => t.Id == recent.TaskId && t.State is ActivityState.ExecutionEvidence or ActivityState.AwaitingApproval or ActivityState.AwaitingInput))
            _recentNotice = null;
        if (_recentNotice is { } notice && now < _recentUntil && snapshot.Health is SampleHealth.Fresh or SampleHealth.Partial)
        {
            _summary.Text = StateLabel(notice.State);
            _summary.Foreground = StateBrush(notice.State);
            _hint.Text = active > 0 ? $"{active} 项仍有执行迹象" : "最近任务";
        }
        Hint(_summary, snapshot.Message);
    }

    private void RenderTasks(ActivitySnapshot snapshot)
    {
        _activityStatus.Text = $"{snapshot.Tasks.Count} 项 · {snapshot.ObservedAt.ToLocalTime():HH:mm:ss} · 本地推断"
            + (snapshot.Health is SampleHealth.Stale or SampleHealth.Unavailable ? " · 记录过期" : "");
        Hint(_activityStatus, snapshot.Message);
        _tasks.Children.Clear(); _timingRows.Clear(); _taskCosts.Clear();
        foreach (var task in snapshot.Tasks)
        {
            var state = CurrentState(task, snapshot, DateTimeOffset.UtcNow);
            var title = Text(task.Title, 12); title.Foreground = StateBrush(state);
            var timing = Text("", 10); _timingRows.Add((task, timing));
            var status = Text(StateLabel(state), 10); status.Foreground = StateBrush(state);
            var token = Text("TOKEN  本轮 " + Tokens(task.TokenUsage.CurrentTurn) + "   自身累计 " + Tokens(task.TokenUsage.Thread), 10);
            var cost = Text("", 10); _taskCosts[task.Id] = cost;
            RenderSessionCost(cost, task.Id);
            var counts = task.TokenUsage.CurrentTurn.Counts;
            var breakdown = Text($"输入 {Count(counts?.InputTokens)}   缓存 {Count(counts?.CachedInputTokens)}   输出 {Count(counts?.OutputTokens)}", 10);
            Hint(token, TokenHint(task.TokenUsage.CurrentTurn, "本轮") + "\n\n" + TokenHint(task.TokenUsage.Thread, "本任务自身累计") + "\n" + TaskTokenUsage.Scope);
            Hint(breakdown, "缓存输入包含在输入中，推理输出包含在输出中，不重复相加。");
            _tasks.Children.Add(new Border { Padding = new Thickness(9), Background = Brush.Parse("#1C261D"),
                BorderBrush = StateBrush(state), BorderThickness = new Thickness(2, 0, 0, 0),
                Child = new StackPanel { Spacing = 5, Children = { title, Row(status, timing), token, breakdown, cost } } });
        }
        if (snapshot.Tasks.Count == 0) _tasks.Children.Add(Text(snapshot.Health == SampleHealth.Unavailable ? "本地记录暂不可用" : "没有可显示的本机顶层任务", 12));
    }

    private void RenderQuotaOptions()
    {
        _quotaOptions.Children.Clear();
        if (_controller.Quota is not { } quota) return;
        _quotaStatus.Text = $"额度 · {quota.ObservedAt.ToLocalTime():HH:mm:ss}"
            + (quota.Health is SampleHealth.Stale or SampleHealth.Unavailable ? " · 已过期" : "");
        Hint(_quotaStatus, quota.Message);
        foreach (var window in quota.Windows)
        {
            var selected = window.Key == _settings.SelectedQuotaKey;
            string value = window.RemainingPercent is { } v ? $"{v:0.#}%" : "—";
            var button = Button($"{(selected ? "●" : "○")}  {window.Label} · {QuotaProvider.FormatPeriod(window.WindowMinutes, window.Slot)} · 剩余 {value}", () =>
            { _settings.SelectedQuotaKey = window.Key; _controller.SelectQuota(window.Key); _resetRequested = null; Save(); RenderQuotaOptions(); UpdateClock(); UpdateBilling(); });
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            _quotaOptions.Children.Add(button);
        }
        if (quota.Windows.Count == 0) _quotaOptions.Children.Add(Text(quota.Message ?? "尚未取得额度，请检查 Codex CLI 登录与路径。", 12));
    }

    private void UpdateClock()
    {
        var now = DateTimeOffset.UtcNow;
        var quota = _controller.Quota;
        var selected = quota?.Windows.FirstOrDefault(w => w.Key == _settings.SelectedQuotaKey);
        bool stale = quota is null || quota.Health is SampleHealth.Stale or SampleHealth.Unavailable
            || now - quota.ObservedAt > TimeSpan.FromSeconds(150) || selected?.ResetsAt <= now;
        _ring.Update(selected?.RemainingPercent, stale);
        _quotaTitle.Text = selected?.Label ?? (_settings.SelectedQuotaKey is null ? "尚未取得额度" : "所选额度不可用");
        Hint(_quotaTitle, quota?.Message);
        _reset.Text = selected?.ResetsAt is { } reset ? reset > now ? "重置 " + TaskTiming.Format(reset - now) : "等待重置查询" : "重置时间 —";
        if (selected?.ResetsAt is { } due && due <= now && _resetRequested != due)
        { _resetRequested = due; _controller.RefreshQuota(); }
        var estimate = _controller.Estimate(selected, _settings.SelectedQuotaKey);
        var recovering = estimate.RecoveryRequired > 0;
        bool estimated = !recovering && (estimate.State is EstimateState.Estimated or EstimateState.Variable)
            && estimate.RemainingTokens is >= 0 && double.IsFinite(estimate.RemainingTokens.Value);
        var label = recovering && (estimate.State is EstimateState.Calibrating or EstimateState.Estimated or EstimateState.Variable)
            ? "恢复中" : estimate.State switch { EstimateState.Calibrating => "校准中", EstimateState.Estimated => "近期估算", EstimateState.Variable => "波动较大",
                EstimateState.Incomplete => "数据不全", EstimateState.Stale => "已过期", _ => "暂不可估" };
        var paused = estimate.State is EstimateState.Stale or EstimateState.Incomplete;
        var progress = recovering
            ? (paused ? "暂停 · " : "") + $"恢复 {Math.Clamp(estimate.RecoverySamples, 0, estimate.RecoveryRequired)}/{estimate.RecoveryRequired} 次健康采样 · 已累计下降 {estimate.PendingDrop:0.##} 个百分点"
            : paused ? $"当前段暂停 · 已累计下降 {estimate.PendingDrop:0.##} 个百分点"
                : $"当前段 {estimate.PendingDrop:0.##} / 2" + (estimated ? "" : " · 首次校准需 3 段 / 6 个百分点");
        _estimate.Text = estimated ? $"预估 ≈ {Count(estimate.RemainingTokens)} Token" : $"Token · {label}";
        _estimateDetail.Text = (estimated ? $"≈ {Count(estimate.RemainingTokens)} Token · {label}\n经验范围 {Count(estimate.LowerTokens)}–{Count(estimate.UpperTokens)}\n" : label + "\n")
            + $"已保留 {estimate.Segments} 段 · 累计下降 {estimate.ObservedDrop:0.##} 个百分点\n{progress}\n"
            + estimate.ProgressReason + (estimate.LastResetReason is { } reason ? "\n最近调整：" + reason : "");
        string hint = "本机近期等效估计，并非套餐固定 Token 上限。\n同期新增 Token ÷ 额度下降百分点 × 当前剩余百分比。"
            + "\n经验范围不是统计置信区间；其他设备和云端消耗可能影响比例。\n" + estimate.Detail
            + $"\n采样 {estimate.From?.ToLocalTime():MM-dd HH:mm}–{estimate.Through?.ToLocalTime():MM-dd HH:mm}";
        Hint(_estimate, hint); Hint(_estimateDetail, hint);
        UpdateResetCredits(now);
        if (_panel == "tasks" && _controller.Activity is { } activity)
            foreach (var (task, timing) in _timingRows)
            { var display = TaskTiming.Describe(task, activity, now); timing.Text = display.Label + " " + display.Text; }
    }

    private void ShowNotices(IReadOnlyList<TaskNotice> notices)
    {
        if (notices.Count == 0) return;
        var terminal = notices.LastOrDefault(n => n.State is ActivityState.Completed or ActivityState.Interrupted);
        if (terminal is null) return;
        _recentNotice = terminal; _recentUntil = DateTimeOffset.UtcNow.AddSeconds(60);
        if (!_settings.CompletionNotifications || _exiting) return;
        _toast?.Close();
        var button = Button(new StackPanel { Spacing = 7, Children = { Text(StateLabel(terminal.State), 15), Text(terminal.Title, 12) } }, () =>
        { Restore(); if (_panel != "tasks") OpenPanel("tasks"); _toast?.Close(); });
        var toast = new Window { Title = "Codex HUD 提醒", Width = 320, SizeToContent = SizeToContent.Height,
            SystemDecorations = SystemDecorations.None, CanResize = false, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
            Background = Brush.Parse("#172017"), Content = new Border { Padding = new Thickness(14), BorderBrush = StateBrush(terminal.State), BorderThickness = new Thickness(1), Child = button } };
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null) toast.Position = new PixelPoint(screen.WorkingArea.Right - (int)(340 * screen.Scaling), screen.WorkingArea.Y + 24);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) => { timer.Stop(); toast.Close(); };
        toast.Closed += (_, _) => { timer.Stop(); if (ReferenceEquals(_toast, toast)) _toast = null; };
        _toast = toast; toast.Show(); timer.Start();
        if (_settings.NotificationSound) MacSound.Beep();
    }

    private static ActivityState CurrentState(TaskActivity task, ActivitySnapshot snapshot, DateTimeOffset now) =>
        task.State is ActivityState.Completed or ActivityState.Interrupted ? task.State
        : snapshot.AppPresent && snapshot.Health is SampleHealth.Fresh or SampleHealth.Partial
            && now >= snapshot.ObservedAt && now - snapshot.ObservedAt <= TimeSpan.FromSeconds(120)
            && task.EvidenceAt is { } evidence && now >= evidence && now - evidence <= TimeSpan.FromSeconds(120)
            ? task.State : ActivityState.Unconfirmed;
    private static string StateLabel(ActivityState state) => state switch { ActivityState.ExecutionEvidence => "执行迹象", ActivityState.Completed => "已完成",
        ActivityState.Interrupted => "已中断", ActivityState.AwaitingApproval => "需要审批", ActivityState.AwaitingInput => "等待输入", _ => "待确认" };
    private static IBrush StateBrush(ActivityState state) => Brush.Parse(state switch { ActivityState.ExecutionEvidence => "#62B5FF", ActivityState.Completed => "#82D89C",
        ActivityState.Interrupted => "#F16D70", ActivityState.AwaitingApproval => "#FFB35A", ActivityState.AwaitingInput => "#C48FFF", _ => "#909B98" });
    private static string Percent(MetricSample metric) => metric.Value is { } v ? $"{v:0}%" + Mark(metric.Health) : "—";
    private static string Mark(SampleHealth health) => health is SampleHealth.Stale or SampleHealth.Partial ? "·" : "";
    private static string MetricHint(MetricSample sample) => $"{sample.Detail}\n{sample.Health} · {sample.ObservedAt?.ToLocalTime():HH:mm:ss}";
    private static string Bytes(MetricSample metric, bool rate = false)
    {
        if (metric.Value is not { } v || !double.IsFinite(v) || v < 0) return "—";
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" }; int unit = 0;
        while (v >= 1024 && unit < units.Length - 1) { v /= 1024; unit++; }
        return $"{v:0.#} {units[unit]}" + (rate ? "/s" : "") + Mark(metric.Health);
    }
    private static string Count(double? value) => value is not >= 0 || !double.IsFinite(value.Value) ? "—" : value switch
    { >= 1_000_000_000 => $"{value / 1_000_000_000:0.#}B", >= 1_000_000 => $"{value / 1_000_000:0.#}M", >= 1_000 => $"{value / 1_000:0.#}K", _ => $"{value:0}" };
    private static string Tokens(TokenUsageSample sample) => Count(sample.Counts?.TotalTokens) + Mark(sample.Health);
    private static string TokenHint(TokenUsageSample sample, string label)
    {
        static string Exact(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        var c = sample.Counts;
        return $"{label} · {sample.ObservedAt?.ToLocalTime():MM-dd HH:mm:ss} · {sample.Health}\n总计 {Exact(c?.TotalTokens)} / 输入 {Exact(c?.InputTokens)} / 缓存输入 {Exact(c?.CachedInputTokens)}"
            + $"\n缓存写入 {Exact(c?.CacheWriteInputTokens)} / 输出 {Exact(c?.OutputTokens)} / 推理输出 {Exact(c?.ReasoningOutputTokens)}\n{sample.Detail}";
    }
}

internal static class MacSound
{
    internal static void Beep() { try { NSBeep(); } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { } }
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/AppKit.framework/AppKit")]
    private static extern void NSBeep();
}
