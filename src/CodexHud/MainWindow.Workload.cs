using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CodexHud.Controls;
using CodexHud.Core;

namespace CodexHud;

public partial class MainWindow
{
    private readonly WorkloadUsageProvider _workload;
    private WorkloadUsageSnapshot? _workloadData;
    private readonly ObservableCollection<WorkloadRow> _workloadRows = [];
    private WorkloadUsageSnapshot? _renderedWorkload;
    private bool _renderedWorkloadExpired;
    private WorkloadUsageSnapshot? _renderedWorkloadList;
    private ActivitySnapshot? _renderedWorkloadActivity;
    private bool _renderedWorkloadListExpired;
    private volatile bool _workloadFixtureActive;

    private const string WorkloadHint = "最近已结束的 1 秒 tick，按本机首次观测到响应用量记录的时间统计。\n"
        + "输入包含 Cache hit；总量 = 输入 + 输出。波峰可能来自集中上报，不是逐字生成速度。\n"
        + "包含普通主任务、独立子任务与近期归档任务；每项只计自身。仅本机日志，非账户额度。";

    private async Task WorkloadLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try
                {
                    var snapshot = await _workload.ReadAsync(_stop.Token);
                    if (!_workloadFixtureActive) Volatile.Write(ref _workloadData, snapshot);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch
                {
                    var previous = Volatile.Read(ref _workloadData);
                    if (!_workloadFixtureActive && previous != null)
                        Volatile.Write(ref _workloadData, previous with
                        {
                            Health = SampleHealth.Unavailable,
                            Detail = "实时用量采集暂不可用"
                        });
                }
                // Hidden windows still collect every second, without queueing a render.
                if (!_hidden && !_stop.IsCancellationRequested)
                    await Dispatcher.InvokeAsync(UpdateWorkload);
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void UpdateWorkload()
    {
        if (!_ready || _hidden) return;
        var snapshot = Volatile.Read(ref _workloadData);
        var expired = snapshot == null || DateTimeOffset.UtcNow - snapshot.ObservedAt > TimeSpan.FromSeconds(3)
            || snapshot.Health == SampleHealth.Unavailable;
        if (!ReferenceEquals(snapshot, _renderedWorkload) || expired != _renderedWorkloadExpired)
        {
            _renderedWorkload = snapshot;
            _renderedWorkloadExpired = expired;
            var history = snapshot?.OverallHistory ?? [];
            var latest = expired ? null : history.LastOrDefault();
            UsageTotalValue.Text = WorkloadNumber(latest, c => c.TotalTokens);
            UsageInputValue.Text = WorkloadNumber(latest, c => c.InputTokens);
            UsageCacheValue.Text = WorkloadNumber(latest, c => c.CachedInputTokens);
            UsageOutputValue.Text = WorkloadNumber(latest, c => c.OutputTokens);
            OverallUsageChart.History = history;
            OverallUsageChart.EndTime = WorkloadEnd(snapshot);
            OverallUsageChart.Opacity = expired ? .4 : 1;
            UsageFooter.ToolTip = UsageButton.ToolTip = WorkloadHint + "\n" + TickHint(latest)
                + (expired ? "\n采集暂不可用或已过期" : snapshot?.Health == SampleHealth.Partial ? "\n部分数据未完整读取" : "");
        }
        if (UsagePanel.Visibility != Visibility.Visible) return;
        if (ReferenceEquals(snapshot, _renderedWorkloadList)
            && ReferenceEquals(_activityData, _renderedWorkloadActivity)
            && expired == _renderedWorkloadListExpired) return;
        _renderedWorkloadList = snapshot;
        _renderedWorkloadActivity = _activityData;
        _renderedWorkloadListExpired = expired;

        var activity = _activityData?.Tasks ?? [];
        var indexed = (snapshot?.Tasks ?? []).ToDictionary(t => t.ThreadId, StringComparer.Ordinal);
        var desired = new Dictionary<string, TaskUsageSeries>(StringComparer.Ordinal);
        foreach (var task in activity)
            desired[task.Id] = indexed.TryGetValue(task.Id, out var known) ? known
                : new TaskUsageSeries(task.Id, task.Title, false, false, []);
        foreach (var task in snapshot?.Tasks ?? [])
            if (task.History.Any(tick => tick.Counts?.TotalTokens > 0))
                desired[task.ThreadId] = task;

        // Rows retain their identity and order: current values never reorder the list.
        for (int i = _workloadRows.Count - 1; i >= 0; i--)
            if (!desired.ContainsKey(_workloadRows[i].Id)) _workloadRows.RemoveAt(i);
        var existing = _workloadRows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        double max = 0;
        foreach (var task in desired.Values)
            foreach (var tick in task.History)
                if (tick.Counts is { } counts)
                    max = Math.Max(max, Math.Max((double)(counts.InputTokens ?? 0),
                        Math.Max((double)(counts.CachedInputTokens ?? 0), counts.OutputTokens ?? 0)));
        var maximum = UsageSparkline.NiceMaximum(max);
        var end = WorkloadEnd(snapshot);
        foreach (var task in desired.Values)
        {
            if (!existing.TryGetValue(task.ThreadId, out var row))
            {
                row = new WorkloadRow(task.ThreadId);
                _workloadRows.Add(row);
            }
            row.Update(task, end, maximum, expired);
        }
        UsageScaleText.Text = $"共用纵轴 0–{FormatEstimatedTokens(maximum)} / tick";
        UsageEmptyText.Visibility = _workloadRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UsageStatusText.Text = $"{_workloadRows.Count} 项 · 1 秒/tick · "
            + (expired ? "采集暂不可用" : snapshot?.Health == SampleHealth.Partial ? "部分数据" : "本机记录")
            + (snapshot == null ? "" : $" · {snapshot.ObservedAt.ToLocalTime():HH:mm:ss}");
        UsageStatusText.ToolTip = WorkloadHint + "\n" + snapshot?.Detail;
        UpdateSessionCosts();
    }

    private static DateTimeOffset WorkloadEnd(WorkloadUsageSnapshot? snapshot) =>
        snapshot?.OverallHistory.LastOrDefault() is { } last ? last.TickStart.AddSeconds(1)
            : DateTimeOffset.FromUnixTimeSeconds((snapshot?.ObservedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());

    private static string WorkloadNumber(UsageTick? tick, Func<TokenCounts, long?> select)
    {
        if (tick?.Counts is not { } counts || tick.Health is SampleHealth.Loading or SampleHealth.Unavailable or SampleHealth.Stale)
            return "—";
        var value = select(counts);
        return value is null ? "—" : FormatEstimatedTokens(value) + (tick.Health == SampleHealth.Partial ? "·" : "");
    }

    private static string TickHint(UsageTick? tick)
    {
        if (tick == null) return "等待完整 tick";
        static string Exact(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        var c = tick.Counts;
        return $"{tick.TickStart.ToLocalTime():HH:mm:ss}–{tick.TickStart.AddSeconds(1).ToLocalTime():HH:mm:ss}\n"
            + $"输入 {Exact(c?.InputTokens)} · Cache hit {Exact(c?.CachedInputTokens)} · 输出 {Exact(c?.OutputTokens)}\n"
            + $"总量 {Exact(c?.TotalTokens)}"
            + (tick.Health == SampleHealth.Fresh ? "" : "\n该 tick 数据不完整");
    }

    private sealed class WorkloadRow(string id) : INotifyPropertyChanged
    {
        private CostPresentation _cost = CostPresentation.Hidden;
        public bool IsSubagent { get; private set; }
        public string CostText => _cost.Text;
        public string CostHint => _cost.Hint;
        public Visibility CostVisibility => _cost.Visibility;
        public void UpdateCost(CostPresentation value)
        {
            if (_cost == value) return;
            _cost = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CostText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CostHint)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CostVisibility)));
        }
        public string Id { get; } = id;
        public string Title { get; private set; } = "";
        public string Tags { get; private set; } = "";
        public string InputText { get; private set; } = "";
        public string CacheText { get; private set; } = "";
        public string OutputText { get; private set; } = "";
        public string Hint { get; private set; } = "";
        public IReadOnlyList<UsageTick> History { get; private set; } = [];
        public double Maximum { get; private set; }
        public DateTimeOffset EndTime { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(TaskUsageSeries task, DateTimeOffset end, double maximum, bool expired)
        {
            IsSubagent = task.IsSubagent;
            Title = string.IsNullOrWhiteSpace(task.Title) ? "任务 " + Id[..Math.Min(8, Id.Length)] : task.Title;
            Tags = (task.IsSubagent ? "子任务" : "主任务") + (task.IsArchived ? " · 已归档" : "");
            History = task.History;
            EndTime = end;
            Maximum = maximum;
            var latest = expired ? null : History.LastOrDefault(t => t.TickStart == end.AddSeconds(-1));
            InputText = "输入 " + WorkloadNumber(latest, c => c.InputTokens);
            CacheText = "Cache hit " + WorkloadNumber(latest, c => c.CachedInputTokens);
            OutputText = "输出 " + WorkloadNumber(latest, c => c.OutputTokens);
            Hint = Title + "\n" + Tags + "\n" + TickHint(latest) + "\n" + WorkloadHint;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    private void RunWorkloadUiChecks(List<object> results, string directory)
    {
        _workloadFixtureActive = true;
        var original = _workloadData;
        var originalActivity = _activityData;
        var originalScale = _settings.Scale;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var end = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
            IReadOnlyList<UsageTick> MakeHistory(int index) => Enumerable.Range(0, 60).Select(i =>
            {
                var amount = i == 59 ? 100L : i is 20 or 40 ? 1_000L * (index + 1) : 0L;
                return new UsageTick(end.AddSeconds(i - 60),
                    i == 30 ? null : new TokenCounts(amount, amount * 8 / 10, 0, amount / 5, 0, amount + amount / 5),
                    i == 30 ? SampleHealth.Unavailable : i == 41 ? SampleHealth.Partial : SampleHealth.Fresh);
            }).ToArray();
            var tasks = Enumerable.Range(0, 16).Select(i => new TaskUsageSeries("usage-fixture-" + i,
                i == 0 ? "长标题验证 · 实时 Token 曲线与主任务、独立子任务以及归档记录的显示完整性" : $"用量验证 {i + 1:00}",
                i % 3 == 1, i % 4 == 2, MakeHistory(i))).ToArray();
            _activityData = new ActivitySnapshot(now, true, [], SampleHealth.Fresh);
            _workloadData = new WorkloadUsageSnapshot(now, MakeHistory(0), tasks, SampleHealth.Fresh);
            OpenPanel("usage"); UpdateWorkload(); UpdateLayout();
            results.Add(new { scenario = "workload-values-and-cache-subset", passed = UsageTotalValue.Text == "120"
                && UsageInputValue.Text == "100" && UsageCacheValue.Text == "80" && UsageOutputValue.Text == "20"
                && UsageFooter.ToolTip.ToString()!.Contains("总量 120") });
            results.Add(new { scenario = "workload-series-and-shared-scale", passed = _workloadRows.Count == 16
                && _workloadRows.Select(r => r.Maximum).Distinct().Count() == 1
                && _workloadRows.All(r => r.EndTime == end && r.History.Count == 60)
                && _workloadRows.Any(r => r.Tags.Contains("子任务")) && _workloadRows.Any(r => r.Tags.Contains("已归档")) });
            foreach (var scale in new[] { 1d, 1.5d, 2d })
            {
                _settings.Scale = scale; ApplyAppearance(); UpdateLayout();
                var lastFooterRight = UsageOutputValue.TranslatePoint(new Point(UsageOutputValue.ActualWidth, 0), Shell).X;
                results.Add(new { scenario = $"workload-layout-{scale}", width = ActualWidth, height = ActualHeight,
                    passed = Math.Abs(ActualWidth - 654 * scale) < 2 && OverallUsageChart.ActualWidth == 80
                        && lastFooterRight < Shell.ActualWidth && UsageTaskList.ActualWidth >= 570
                        && ActualHeight <= WindowPlacementService.WorkingHeightDip(this, _settings) });
                Capture(Path.Combine(directory, $"workload-{scale * 100:0}.png"));
            }
            _settings.Scale = 1; ApplyAppearance();
            var selected = _workloadRows[5];
            UsageTaskList.SelectedItem = selected;
            UsageTaskList.ScrollIntoView(selected); UpdateLayout();
            var scroll = Descendants<ScrollViewer>(UsageTaskList).First();
            scroll.ScrollToVerticalOffset(4); UpdateLayout();
            var offset = scroll.VerticalOffset;
            _workloadData = _workloadData with { ObservedAt = DateTimeOffset.UtcNow, Tasks = tasks.Reverse().ToArray() };
            UpdateWorkload(); UpdateLayout();
            results.Add(new { scenario = "workload-refresh-preserves-scroll-and-identity", passed =
                ReferenceEquals(UsageTaskList.SelectedItem, selected) && ReferenceEquals(_workloadRows[5], selected)
                && Math.Abs(scroll.VerticalOffset - offset) < 1 });
            var idle = Enumerable.Range(0, 60).Select(i => new UsageTick(end.AddSeconds(i - 60),
                new TokenCounts(0, 0, 0, 0, 0, 0), SampleHealth.Fresh)).ToArray();
            _workloadData = new(DateTimeOffset.UtcNow, idle, [], SampleHealth.Fresh);
            UpdateWorkload();
            results.Add(new { scenario = "workload-idle-zero-and-empty-list", passed = UsageTotalValue.Text == "0"
                && UsageInputValue.Text == "0" && UsageEmptyText.Visibility == Visibility.Visible });
            _workloadData = _workloadData with { OverallHistory = [new(end.AddSeconds(-1), null, SampleHealth.Unavailable)], Health = SampleHealth.Partial };
            UpdateWorkload();
            results.Add(new { scenario = "workload-gap-is-not-zero", passed = UsageTotalValue.Text == "—"
                && UsageInputValue.Text == "—" && UsageCacheValue.Text == "—" && UsageOutputValue.Text == "—" });
            var large = new UsageTick(end.AddSeconds(-1), new TokenCounts(9_000_000_000, 8_000_000_000, 0, 1_000_000_000, 0, 10_000_000_000), SampleHealth.Fresh);
            _workloadData = new(DateTimeOffset.UtcNow, [large], [], SampleHealth.Fresh);
            UpdateWorkload(); UpdateLayout();
            results.Add(new { scenario = "workload-large-number-and-exact-tooltip", passed = UsageTotalValue.Text == "10B"
                && UsageFooter.ToolTip.ToString()!.Contains("10,000,000,000")
                && UsageOutputValue.TranslatePoint(new Point(UsageOutputValue.ActualWidth, 0), Shell).X < Shell.ActualWidth });
            _workloadData = _workloadData with { ObservedAt = DateTimeOffset.UtcNow.AddSeconds(-4) };
            UpdateWorkload();
            results.Add(new { scenario = "workload-stale-current-tick-hidden", passed = UsageTotalValue.Text == "—"
                && OverallUsageChart.Opacity < 1 });
            _workloadData = new(DateTimeOffset.UtcNow, MakeHistory(0), tasks, SampleHealth.Fresh);
            UpdateWorkload(); OpenPanel("quotas"); UpdateLayout();
            results.Add(new { scenario = "workload-tab-switch", passed = UsagePanel.Visibility == Visibility.Collapsed
                && QuotasPanel.Visibility == Visibility.Visible });
            OpenPanel("usage"); CollapseDetails(this, new RoutedEventArgs()); UpdateLayout();
            Capture(Path.Combine(directory, "workload-compact.png"));
            results.Add(new { scenario = "workload-compact-size", passed = Math.Abs(ActualWidth - 654) < 2
                && Math.Abs(ActualHeight - 134) < 2 });
        }
        finally
        {
            _workloadData = original;
            _activityData = originalActivity;
            _settings.Scale = originalScale;
            _workloadFixtureActive = false;
            ApplyAppearance(); UpdateWorkload();
        }
    }
}
