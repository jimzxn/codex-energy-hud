using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CodexHud.Core;

namespace CodexHud.Mac;

internal sealed partial class MainWindow
{
    private readonly StackPanel _workloadPanel = new() { Spacing = 10 };
    private readonly Button _workloadFooter = new()
    {
        Background = Brushes.Transparent, Padding = new Thickness(6, 2), Margin = new Thickness(12, 0, 12, 4),
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly UsageSparkline _overallUsageChart = new() { Width = 80, Height = 38 };
    private readonly TextBlock _usageTotal = Text("—", 13), _usageInput = Text("—", 13),
        _usageCache = Text("—", 13), _usageOutput = Text("—", 13);
    private readonly TextBlock _usageStatus = Text("等待本机用量采样…", 11), _usageScale = Text("共用纵轴 0–1 Token/s", 10),
        _usageEmpty = Text("暂无活动任务或最近有用量的任务。", 11);
    private readonly StackPanel _workloadRowsPanel = new() { Spacing = 9 };
    private readonly Dictionary<string, WorkloadRow> _workloadRows = new(StringComparer.Ordinal);
    private WorkloadUsageSnapshot? _renderedWorkload, _renderedWorkloadList;
    private ActivitySnapshot? _renderedWorkloadActivity;
    private SessionCostSnapshot? _renderedWorkloadCosts;
    private bool _renderedWorkloadExpired, _renderedWorkloadListExpired, _renderedWorkloadCostSetting, _renderedWorkloadCostsExpired;

    private const string WorkloadHint = "20 秒平均用量 · Token/s，每秒刷新。\n"
        + "最近 20 秒首次观测到的 Token 总量 ÷ 20；每批数据从收到时起均摊 20 秒，多批叠加。\n"
        + "输入包含 Cache hit；总量 = 输入 + 输出。停止后最多拖尾 20 秒，不是逐字生成速度。\n"
        + "包含普通主任务、独立子任务与近期归档任务；每项只计自身。累计 Token 和费用仍按原始记录计算。\n"
        + "曲线展示最近 60 秒；· 表示窗口尚未收满或数据不完整。仅本机日志，非账户额度。";

    private void BuildWorkload()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("108,90,*,*,*,*") };
        grid.Children.Add(new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children = { Text("TOKEN / 秒", 11), Text("20 秒均值  ▾", 10) } });
        Grid.SetColumn(_overallUsageChart, 1); grid.Children.Add(_overallUsageChart);
        AddUsageValue(grid, 2, "总量", _usageTotal, Accent);
        AddUsageValue(grid, 3, "输入", _usageInput, Brush.Parse("#53B8FF"));
        AddUsageValue(grid, 4, "Cache hit", _usageCache, Brush.Parse("#78DB88"));
        AddUsageValue(grid, 5, "输出", _usageOutput, Brush.Parse("#DDEC63"));
        _workloadFooter.Content = grid;
        _workloadFooter.Click += (_, _) => OpenPanel("usage");
        Hint(_workloadFooter, WorkloadHint);
        _workloadPanel.Children.Add(_usageStatus);
        _workloadPanel.Children.Add(_usageScale);
        _workloadPanel.Children.Add(Text("最近 60 秒 · 蓝：输入 / 绿虚线：Cache hit / 黄：输出 · 每项只计自身", 10));
        _workloadPanel.Children.Add(_usageEmpty);
        _workloadPanel.Children.Add(_workloadRowsPanel);
    }

    private static void AddUsageValue(Grid grid, int column, string label, TextBlock value, IBrush brush)
    {
        value.Foreground = brush;
        value.TextWrapping = TextWrapping.NoWrap;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Children = { Text(label, 9), value } };
        Grid.SetColumn(panel, column); grid.Children.Add(panel);
    }

    private void UpdateWorkload()
    {
        if (!_ready || !IsVisible) return;
        var snapshot = _controller.Workload;
        var now = DateTimeOffset.UtcNow;
        var expired = snapshot is null || now - snapshot.ObservedAt > TimeSpan.FromSeconds(3)
            || snapshot.ObservedAt > now.AddSeconds(1)
            || snapshot.Health is SampleHealth.Unavailable or SampleHealth.Stale;
        if (!ReferenceEquals(snapshot, _renderedWorkload) || expired != _renderedWorkloadExpired)
        {
            _renderedWorkload = snapshot;
            _renderedWorkloadExpired = expired;
            var history = UsageSmoothing.Calculate(snapshot?.OverallHistory ?? []).TakeLast(UsageSmoothing.DisplaySeconds).ToArray();
            var latest = expired ? null : history.LastOrDefault();
            _usageTotal.Text = WorkloadNumber(latest, c => c.TotalTokens);
            _usageInput.Text = WorkloadNumber(latest, c => c.InputTokens);
            _usageCache.Text = WorkloadNumber(latest, c => c.CachedInputTokens);
            _usageOutput.Text = WorkloadNumber(latest, c => c.OutputTokens);
            _overallUsageChart.History = history;
            _overallUsageChart.EndTime = WorkloadEnd(snapshot);
            _overallUsageChart.Opacity = expired ? .4 : 1;
            Hint(_workloadFooter, WorkloadHint + "\n\n" + RateHint(latest) + "\n\n"
                + TickHint(expired ? null : snapshot?.OverallHistory.LastOrDefault())
                + (expired ? "\n采集暂不可用或已过期" : snapshot?.Health == SampleHealth.Partial ? "\n部分数据未完整读取" : ""));
        }
        if (_panel != "usage") return;
        var activity = _controller.Activity;
        var costs = _controller.SessionCosts;
        var costsExpired = costs is null || costs.Health is SampleHealth.Stale or SampleHealth.Unavailable
            || now < costs.ObservedAt || now - costs.ObservedAt > TimeSpan.FromSeconds(20);
        if (ReferenceEquals(snapshot, _renderedWorkloadList) && ReferenceEquals(activity, _renderedWorkloadActivity)
            && expired == _renderedWorkloadListExpired && ReferenceEquals(costs, _renderedWorkloadCosts)
            && costsExpired == _renderedWorkloadCostsExpired
            && _settings.ShowSessionCost == _renderedWorkloadCostSetting) return;
        _renderedWorkloadList = snapshot;
        _renderedWorkloadActivity = activity;
        _renderedWorkloadListExpired = expired;
        _renderedWorkloadCosts = costs;
        _renderedWorkloadCostsExpired = costsExpired;
        _renderedWorkloadCostSetting = _settings.ShowSessionCost;

        var indexed = (snapshot?.Tasks ?? []).ToDictionary(task => task.ThreadId, StringComparer.Ordinal);
        var desired = new Dictionary<string, TaskUsageSeries>(StringComparer.Ordinal);
        foreach (var task in activity?.Tasks ?? [])
            desired[task.Id] = indexed.TryGetValue(task.Id, out var known) ? known : new(task.Id, task.Title, false, false, []);
        foreach (var task in snapshot?.Tasks ?? [])
            if (task.History.Any(tick => tick.Counts?.TotalTokens > 0)) desired[task.ThreadId] = task;

        // Keep controls and their order between snapshots so refreshes preserve the scroll position.
        foreach (var id in _workloadRows.Keys.Where(id => !desired.ContainsKey(id)).ToArray())
        {
            _workloadRowsPanel.Children.Remove(_workloadRows[id].Container);
            _workloadRows.Remove(id);
        }
        var histories = desired.Values.ToDictionary(task => task.ThreadId,
            task => UsageSmoothing.Calculate(task.History).TakeLast(UsageSmoothing.DisplaySeconds).ToArray(), StringComparer.Ordinal);
        double maximum = 0;
        foreach (var history in histories.Values)
            foreach (var tick in history)
                if (tick.Rates is { } rates && tick.Health is SampleHealth.Fresh or SampleHealth.Partial)
                    maximum = Math.Max(maximum, Math.Max(rates.InputTokens ?? 0, Math.Max(rates.CachedInputTokens ?? 0, rates.OutputTokens ?? 0)));
        maximum = UsageSparkline.NiceMaximum(maximum);
        var end = WorkloadEnd(snapshot);
        foreach (var task in desired.Values)
        {
            if (!_workloadRows.TryGetValue(task.ThreadId, out var row))
            {
                row = new(task.ThreadId);
                _workloadRows.Add(task.ThreadId, row);
                _workloadRowsPanel.Children.Add(row.Container);
            }
            row.Update(task, histories[task.ThreadId], end, maximum, expired);
            RenderSessionCost(row.Cost, row.Id, row.IsSubagent);
        }
        _usageScale.Text = $"共用纵轴 0–{FormatWorkloadRate(maximum)} Token/s";
        _usageEmpty.IsVisible = _workloadRows.Count == 0;
        _usageStatus.Text = $"{_workloadRows.Count} 项 · 20 秒均值 · 每秒刷新 · "
            + (expired ? "采集暂不可用" : snapshot?.Health == SampleHealth.Partial ? "部分数据" : "本机记录")
            + (snapshot is null ? "" : $" · {snapshot.ObservedAt.ToLocalTime():HH:mm:ss}");
        Hint(_usageStatus, WorkloadHint + "\n" + snapshot?.Detail);
    }

    private static DateTimeOffset WorkloadEnd(WorkloadUsageSnapshot? snapshot) =>
        snapshot?.OverallHistory.LastOrDefault() is { } last ? last.TickStart.AddSeconds(1)
            : DateTimeOffset.FromUnixTimeSeconds((snapshot?.ObservedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());

    private static string FormatWorkloadRate(double? value)
    {
        if (value is not { } number || !double.IsFinite(number) || number < 0) return "—";
        if (number < 1_000) return number.ToString("0.##", CultureInfo.InvariantCulture);
        var divisor = number >= 1_000_000_000 ? 1_000_000_000d : number >= 1_000_000 ? 1_000_000d : 1_000d;
        var suffix = divisor == 1_000_000_000 ? "B" : divisor == 1_000_000 ? "M" : "K";
        return (number / divisor).ToString("0.#", CultureInfo.InvariantCulture) + suffix;
    }

    private static string WorkloadNumber(UsageRateTick? tick, Func<TokenRates, double?> select)
    {
        if (tick?.Rates is not { } rates || tick.Health is SampleHealth.Loading or SampleHealth.Unavailable or SampleHealth.Stale) return "—";
        var value = select(rates);
        return value is null ? "—" : FormatWorkloadRate(value) + (tick.Health == SampleHealth.Partial ? "·" : "");
    }
    private static string RateHint(UsageRateTick? tick)
    {
        if (tick is null) return "等待用量采样";
        static string Exact(double? value) => value?.ToString("N2", CultureInfo.InvariantCulture) ?? "—";
        var rates = tick.Rates;
        return $"20 秒平均 · {tick.TickStart.AddSeconds(1 - UsageSmoothing.WindowSeconds).ToLocalTime():HH:mm:ss}–{tick.TickStart.AddSeconds(1).ToLocalTime():HH:mm:ss}\n"
            + $"输入 {Exact(rates?.InputTokens)} · Cache hit {Exact(rates?.CachedInputTokens)} · 输出 {Exact(rates?.OutputTokens)} Token/s\n"
            + $"总量 {Exact(rates?.TotalTokens)} Token/s"
            + (tick.Health == SampleHealth.Fresh ? "" : "\n窗口尚未收满或数据不完整；正数仅表示已观测部分");
    }
    private static string TickHint(UsageTick? tick)
    {
        if (tick is null) return "等待完整原始采样";
        static string Exact(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        var counts = tick.Counts;
        return $"原始记录 · {tick.TickStart.ToLocalTime():HH:mm:ss}–{tick.TickStart.AddSeconds(1).ToLocalTime():HH:mm:ss}\n"
            + $"输入 {Exact(counts?.InputTokens)} · Cache hit {Exact(counts?.CachedInputTokens)} · 输出 {Exact(counts?.OutputTokens)}\n"
            + $"总量 {Exact(counts?.TotalTokens)} Token" + (tick.Health == SampleHealth.Fresh ? "" : "\n该秒数据不完整");
    }

    private sealed class WorkloadRow
    {
        public string Id { get; }
        public bool IsSubagent { get; private set; }
        public TextBlock Cost { get; } = Text("", 10);
        public Border Container { get; }
        private readonly TextBlock _title = Text("", 12), _tags = Text("", 10), _input = Text("", 10),
            _cache = Text("", 10), _output = Text("", 10);
        private readonly UsageSparkline _chart = new() { Breakdown = true, Height = 52, HorizontalAlignment = HorizontalAlignment.Stretch };

        public WorkloadRow(string id)
        {
            Id = id;
            _title.TextWrapping = TextWrapping.NoWrap;
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            _title.Foreground = Accent;
            _input.Foreground = Brush.Parse("#53B8FF"); _cache.Foreground = Brush.Parse("#78DB88"); _output.Foreground = Brush.Parse("#DDEC63");
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            header.Children.Add(_title);
            _tags.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(_tags, 1); header.Children.Add(_tags);
            var rates = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
            rates.Children.Add(_input); Grid.SetColumn(_cache, 1); rates.Children.Add(_cache); Grid.SetColumn(_output, 2); rates.Children.Add(_output);
            var contents = new StackPanel { Spacing = 4, Children = { header, Cost, rates, _chart } };
            Container = new Border { Padding = new Thickness(8), BorderBrush = Brush.Parse("#354238"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = contents };
        }
        public void Update(TaskUsageSeries task, IReadOnlyList<UsageRateTick> history, DateTimeOffset end, double maximum, bool expired)
        {
            IsSubagent = task.IsSubagent;
            _title.Text = string.IsNullOrWhiteSpace(task.Title) ? "任务 " + Id[..Math.Min(8, Id.Length)] : task.Title;
            _tags.Text = (task.IsSubagent ? "子任务" : "主任务") + (task.IsArchived ? " · 已归档" : "");
            var latest = expired ? null : history.LastOrDefault(tick => tick.TickStart == end.AddSeconds(-1));
            _input.Text = "输入 " + WorkloadNumber(latest, rates => rates.InputTokens);
            _cache.Text = "Cache hit " + WorkloadNumber(latest, rates => rates.CachedInputTokens);
            _output.Text = "输出 " + WorkloadNumber(latest, rates => rates.OutputTokens);
            _chart.History = history; _chart.EndTime = end; _chart.Maximum = maximum; _chart.Opacity = expired ? .4 : 1;
            Hint(Container, _title.Text + "\n" + _tags.Text + "\n" + RateHint(latest) + "\n\n"
                + TickHint(expired ? null : task.History.LastOrDefault()) + "\n\n" + WorkloadHint);
        }
    }
}
