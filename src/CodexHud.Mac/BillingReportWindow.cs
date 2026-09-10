using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodexHud.Core;

namespace CodexHud.Mac;

/// <summary>Presents shared immutable period aggregates; never reads or scans session files.</summary>
internal sealed class BillingReportWindow : Window
{
    private readonly ComboBox _periodPicker = new() { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _sort = new() { ItemsSource = new[] { "按费用", "按 Token", "按用时", "按标题" }, SelectedIndex = 0, MinWidth = 115 };
    private readonly TextBox _search = new() { Watermark = "搜索任务标题或 ID", MinWidth = 210 };
    private readonly TextBlock _range = Label("等待周期边界", 13), _totals = Label("Token — · API 等效 —", 22);
    private readonly TextBlock _coverage = Label("等待本地记录", 11), _status = Label("读取中", 11);
    private readonly TextBlock _footer = Label("", 11), _empty = Label("等待本地用量记录", 12);
    private readonly TextBlock _detailTitle = Label("选择任务查看模型与轮次", 15), _detailText = Label("", 11);
    private readonly ListBox _taskList = new() { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer _detailScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly StackPanel _editor = new() { Spacing = 7, IsVisible = false };
    private readonly TextBlock _editTitle = Label("校正周期起点", 14), _editError = Label("", 11);
    private readonly TextBox _editTime = new() { MinWidth = 260 };
    private readonly Button _correctButton;
    private readonly Dictionary<string, ViewState> _views = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private IReadOnlyList<UsagePeriod> _periods = [];
    private PeriodUsageSnapshot? _snapshot;
    private string? _viewPeriodId, _selectedTaskKey, _editingPeriodId;
    private bool _updating, _rendering, _correction, _actionFailed;
    private int _renderRevision;
    public event Action<string>? PeriodSelected;
    public event Action<DateTimeOffset>? ManualResetRequested;
    public event Action<string, DateTimeOffset>? CorrectStartRequested;
    private string? SelectedPeriodId => (_periodPicker.SelectedItem as PeriodOption)?.Id;

    public BillingReportWindow()
    {
        Title = "Codex HUD · 周期用量报告";
        Width = 1040; Height = 760; MinWidth = 720; MinHeight = 520;
        Background = Brush.Parse("#111912"); Foreground = Brush.Parse("#D9E4CD");
        FontFamily = new FontFamily("PingFang SC, Menlo");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"), Margin = new Thickness(18) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { Label("额度周期", 13), _periodPicker, ActionButton("补记重置", () => OpenEditor(false)) } };
        _correctButton = ActionButton("校正起点", () => OpenEditor(true));
        toolbar.Children.Add(_correctButton);
        root.Children.Add(toolbar);
        var summary = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 12), Children = { _range, _totals, _coverage, _status } };
        Grid.SetRow(summary, 1); root.Children.Add(summary);
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        filters.Children.Add(_search); Grid.SetColumn(_sort, 1); filters.Children.Add(_sort);
        Grid.SetRow(filters, 2); root.Children.Add(filters);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*") };
        var tasks = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(0, 0, 12, 0) };
        tasks.Children.Add(_taskList); Grid.SetRow(_empty, 1); tasks.Children.Add(_empty);
        body.Children.Add(tasks);
        _detailScroll.Content = new StackPanel { Spacing = 9, Children = { _detailTitle, _detailText } };
        Grid.SetColumn(_detailScroll, 1); body.Children.Add(_detailScroll);
        Grid.SetRow(body, 3); root.Children.Add(body);
        _editor.Children.Add(_editTitle);
        _editor.Children.Add(Label("仅修正本机统计边界；不会兑换或消耗重置卡。时间须包含时区：yyyy-MM-dd HH:mm:ss +09:00", 11));
        _editor.Children.Add(_editTime); _editor.Children.Add(_editError);
        _editor.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { ActionButton("保存本地边界", SaveEdit), ActionButton("取消", CloseEditor) } });
        Grid.SetRow(_editor, 4); root.Children.Add(_editor);
        _footer.Margin = new Thickness(0, 12, 0, 0); Grid.SetRow(_footer, 5); root.Children.Add(_footer);
        ScrollViewer.SetHorizontalScrollBarVisibility(_taskList, ScrollBarVisibility.Disabled);
        Content = root;
        _periodPicker.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            CloseEditor(); UpdateReport(null);
            if (SelectedPeriodId is { } id) PeriodSelected?.Invoke(id);
        };
        _search.TextChanged += (_, _) => { if (!_updating) RenderRows(); };
        _sort.SelectionChanged += (_, _) => { if (!_updating) RenderRows(); };
        _taskList.SelectionChanged += (_, _) =>
        {
            if (_rendering) return;
            _selectedTaskKey = (_taskList.SelectedItem as ListBoxItem)?.Tag is ReportRow row ? row.Key : null;
            _detailScroll.Offset = default;
            UpdateDetails();
        };
        _taskList.KeyDown += (_, e) =>
        {
            if (_taskList.SelectedItem is not ListBoxItem { Tag: ReportRow row } || row.Self || row.Task.Children.Count == 0) return;
            if (e.Key == Avalonia.Input.Key.Right && !_expanded.Contains(row.Task.ThreadId)
                || e.Key == Avalonia.Input.Key.Left && _expanded.Contains(row.Task.ThreadId))
            { Toggle(row.Task.ThreadId); e.Handled = true; }
        };
        _editTime.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { SaveEdit(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape) { CloseEditor(); e.Handled = true; }
        };
        Closed += (_, _) => ++_renderRevision;
    }

    public void UpdatePeriods(IReadOnlyList<UsagePeriod> periods, string? selectedId)
    {
        bool changed = !_periods.SequenceEqual(periods);
        var oldId = SelectedPeriodId;
        _periods = periods;
        _updating = true;
        try
        {
            if (changed) _periodPicker.ItemsSource = periods.Select(p => new PeriodOption(p.Id,
                $"{(p.IsCurrent ? "当前 · " : "")}{p.Label} · {p.StartedAt.ToLocalTime():MM-dd HH:mm}{(p.IsPending ? " · 待确认" : "")}")).ToArray();
            _periodPicker.SelectedItem = _periodPicker.Items.OfType<PeriodOption>().FirstOrDefault(p => p.Id == selectedId)
                ?? _periodPicker.Items.OfType<PeriodOption>().FirstOrDefault();
            _periodPicker.IsEnabled = periods.Count > 0;
        }
        finally { _updating = false; }
        if (oldId != SelectedPeriodId) { CloseEditor(); UpdateReport(null); }
    }

    public void UpdateReport(PeriodUsageSnapshot? snapshot)
    {
        if (snapshot != null && snapshot.Period.Id != SelectedPeriodId) return;
        var nextPeriodId = snapshot?.Period.Id ?? SelectedPeriodId;
        if (nextPeriodId != _viewPeriodId)
        {
            SaveView(); _viewPeriodId = nextPeriodId; RestoreView();
        }
        bool unchanged = ReferenceEquals(_snapshot, snapshot);
        _snapshot = snapshot;
        var period = snapshot?.Period ?? _periods.FirstOrDefault(p => p.Id == SelectedPeriodId);
        _range.Text = period is null ? "等待周期边界" : $"{Date(period.StartedAt)} — {Date(period.EndsAt)}";
        _totals.Text = snapshot is null ? "Token — · API 等效 —" : $"Token {Number(snapshot.Totals.Counts.TotalTokens)} · {Price(snapshot.Totals)}";
        _coverage.Text = snapshot is null ? "等待本地记录" : $"{snapshot.Totals.PricedResponses:N0} 条已计价 · {snapshot.Totals.UnpricedResponses:N0} 条未计价 · 用时 {Duration(snapshot.Totals)}";
        _status.Text = snapshot is null ? "历史补齐中" : $"{Health(snapshot.Health)} · 更新 {snapshot.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}";
        if (period != null) _status.Text += " · " + PeriodSource(period);
        _correctButton.IsEnabled = snapshot != null;
        ToolTip.SetTip(_totals, snapshot is null ? "等待响应记录" : PriceHint(snapshot.Totals));
        _footer.Text = "本机全部普通任务 · 按所选额度窗口分段 · 不代表该额度池独占用量。API 等效费用不是实际订阅扣款。"
            + (snapshot?.Detail is { Length: > 0 } detail ? " " + detail : "");
        if (!unchanged) RenderRows();
    }

    private void RenderRows()
    {
        var revision = ++_renderRevision;
        var offset = ListScroll?.Offset ?? default;
        if (_viewPeriodId != null && _views.TryGetValue(_viewPeriodId, out var view) && view.RestoreOffset)
        {
            offset = view.Offset;
            if (_snapshot != null) view.RestoreOffset = false;
        }
        var rows = new List<ReportRow>();
        var query = (_search.Text ?? "").Trim();
        if (_snapshot != null)
            foreach (var task in Ordered(_snapshot.Tasks).Where(t => MatchesTree(t, query))) Append(task, 0, query, rows);
        _rendering = true;
        try
        {
            _taskList.ItemsSource = rows.Select(BuildRow).ToArray();
            _taskList.SelectedItem = _taskList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag is ReportRow row && row.Key == _selectedTaskKey);
        }
        finally { _rendering = false; }
        _empty.IsVisible = rows.Count == 0;
        _empty.Text = query.Length > 0 ? "没有匹配的任务" : _snapshot is null ? "等待本地用量记录"
            : _snapshot.Health == SampleHealth.Loading ? "历史补齐中，已有记录会陆续显示" : "本周期尚无可归属记录";
        UpdateDetails();
        Dispatcher.UIThread.Post(() => { if (revision == _renderRevision && ListScroll is { } scroll) scroll.Offset = offset; });
    }

    private ListBoxItem BuildRow(ReportRow row)
    {
        var task = row.Task;
        var title = Label(row.Self ? "自身" : task.Title, 12);
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        if (!row.Self && task.Children.Count > 0)
            heading.Children.Add(ActionButton(row.Expanded ? "▾" : "▸", () => Toggle(task.ThreadId)));
        Grid.SetColumn(title, 1); heading.Children.Add(title);
        var scope = row.Self ? "仅自身" : task.IsSubagent ? "子代理" : "主任务";
        if (!row.Self && task.Children.Count > 0) scope += " · 含子代理";
        if (task.Archived) scope += " · 已归档";
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(Math.Min(row.Depth, 6) * 12, 4, 0, 5), Children = {
            heading, Label(scope, 10),
            Label($"Token {Number(row.Totals.Counts.TotalTokens)} · {Price(row.Totals)} · {Duration(row.Totals)}", 11),
            Label($"输入 {Number(row.Totals.Counts.InputTokens)} / 缓存 {Number(row.Totals.Counts.CachedInputTokens)} / 输出 {Number(row.Totals.Counts.OutputTokens)}", 10) } };
        ToolTip.SetTip(panel, task.ThreadId + "\n" + PriceHint(row.Totals));
        return new ListBoxItem { Tag = row, Content = panel, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }
    private ScrollViewer? ListScroll => _taskList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    private void Toggle(string id) { if (!_expanded.Remove(id)) _expanded.Add(id); RenderRows(); }
    private IEnumerable<PeriodTaskUsage> Ordered(IEnumerable<PeriodTaskUsage> tasks) => _sort.SelectedIndex switch
    {
        1 => tasks.OrderByDescending(t => t.Group.Counts.TotalTokens ?? -1).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
        2 => tasks.OrderByDescending(t => t.Group.Duration?.Ticks ?? -1).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
        3 => tasks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
        _ => tasks.OrderByDescending(t => t.Group.MaximumUsd ?? t.Group.MinimumUsd ?? -1).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
    };
    private static bool Matches(PeriodTaskUsage task, string query) => query.Length == 0
        || task.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || task.ThreadId.Contains(query, StringComparison.OrdinalIgnoreCase);
    private static bool MatchesTree(PeriodTaskUsage task, string query) => Matches(task, query) || task.Children.Any(t => MatchesTree(t, query));
    private void Append(PeriodTaskUsage task, int depth, string query, List<ReportRow> rows)
    {
        bool expanded = _expanded.Contains(task.ThreadId) || query.Length > 0 && !Matches(task, query);
        rows.Add(new(task, false, depth, expanded));
        if (task.Children.Count == 0 || !expanded) return;
        rows.Add(new(task, true, depth + 1, false));
        foreach (var child in Ordered(task.Children))
            if (Matches(task, query) || MatchesTree(child, query)) Append(child, depth + 1, query, rows);
    }

    private void UpdateDetails()
    {
        if (_taskList.SelectedItem is not ListBoxItem { Tag: ReportRow row })
        {
            _detailTitle.Text = "选择任务查看模型与轮次";
            _detailText.Text = "主任务合计包含已确认子代理；并行用时取并集。缓存输入包含在输入中，推理输出包含在输出中，不重复相加。缺失记录不当作零。";
            return;
        }
        var offset = _detailScroll.Offset;
        _detailTitle.Text = row.Task.Title + (row.Self ? " · 仅自身" : "");
        var text = new StringBuilder();
        text.AppendLine($"任务 {row.Task.ThreadId} · {Health(row.Totals.Health)}");
        text.AppendLine($"总 Token {Number(row.Totals.Counts.TotalTokens)} · 用时 {Duration(row.Totals)} · {Price(row.Totals)}");
        text.AppendLine(PriceHint(row.Totals));
        text.AppendLine("父任务合计包含子代理；用时排除轮次间闲置，组内并行区间不重复累计。");
        if (row.Totals.UnknownTurns > 0) text.AppendLine($"{row.Totals.UnknownTurns:N0} 个轮次时间不完整；显示已确认用时小计。");
        var tasks = row.Self ? new[] { row.Task } : Descendants(row.Task).ToArray();
        foreach (var task in tasks)
        {
            text.AppendLine($"\n{task.Title} · {(task.IsSubagent ? "子代理" : "自身")}");
            text.AppendLine("模型 / 自身记录：");
            if (task.Models.Count == 0) text.AppendLine("尚无模型明细");
            foreach (var model in task.Models.OrderByDescending(m => m.Totals.MaximumUsd ?? m.Totals.MinimumUsd ?? -1))
                text.AppendLine($"{model.Model} · Token {Number(model.Totals.Counts.TotalTokens)} · 输入 {Number(model.Totals.Counts.InputTokens)} / 缓存 {Number(model.Totals.Counts.CachedInputTokens)} / 输出 {Number(model.Totals.Counts.OutputTokens)} · {Price(model.Totals)}");
            text.AppendLine($"轮次 / 自身记录（{task.Turns.Count:N0}）：");
            foreach (var turn in task.Turns.OrderBy(t => t.StartedAt ?? t.EvidenceAt))
                text.AppendLine($"{turn.TurnId} · {Date(turn.StartedAt)} → {(turn.EndedAt is { } end ? Date(end) : "未结束；末次证据 " + Date(turn.EvidenceAt))} · {Health(turn.Health)}");
            if (task.Turns.Count == 0) text.AppendLine("暂无可确认归属的轮次时间");
            foreach (var note in task.Notes.Distinct()) text.AppendLine("说明：" + note);
        }
        _detailText.Text = text.ToString().TrimEnd();
        _detailScroll.Offset = offset;
    }
    private static IEnumerable<PeriodTaskUsage> Descendants(PeriodTaskUsage task)
    {
        yield return task;
        foreach (var child in task.Children)
            foreach (var nested in Descendants(child)) yield return nested;
    }
    private void SaveView()
    {
        if (_viewPeriodId is null) return;
        var offset = _views.TryGetValue(_viewPeriodId, out var previous) && previous.RestoreOffset
            ? previous.Offset : ListScroll?.Offset ?? default;
        _views[_viewPeriodId] = new ViewState { Expanded = _expanded.ToArray(), Selected = _selectedTaskKey,
            Search = _search.Text ?? "", Sort = _sort.SelectedIndex, Offset = offset };
    }
    private void RestoreView()
    {
        _expanded.Clear(); _selectedTaskKey = null; _updating = true;
        try
        {
            if (_viewPeriodId != null && _views.TryGetValue(_viewPeriodId, out var state))
            {
                _expanded.UnionWith(state.Expanded); _selectedTaskKey = state.Selected;
                _search.Text = state.Search; _sort.SelectedIndex = state.Sort; state.RestoreOffset = true;
            }
            else
            {
                _search.Text = ""; _sort.SelectedIndex = 0;
                if (_viewPeriodId != null) _views[_viewPeriodId] = new ViewState { RestoreOffset = true };
            }
        }
        finally { _updating = false; }
    }

    private void OpenEditor(bool correction)
    {
        if (correction && (_snapshot == null || _snapshot.Period.Id != SelectedPeriodId)) return;
        _correction = correction;
        _editingPeriodId = correction ? _snapshot!.Period.Id : SelectedPeriodId;
        _editTitle.Text = correction ? "校正周期起点" : "补记一次重置";
        _editTime.Text = (correction ? _snapshot!.Period.StartedAt : DateTimeOffset.Now).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        _editError.Text = ""; _editor.IsVisible = true; _editTime.Focus(); _editTime.SelectAll();
    }
    private void CloseEditor() { _editor.IsVisible = false; _editingPeriodId = null; }
    public void ShowActionError(string message) { _actionFailed = true; _editError.Text = message; }
    private void SaveEdit()
    {
        if (!_editor.IsVisible) return;
        if (_editingPeriodId != SelectedPeriodId || _correction && _snapshot?.Period.Id != _editingPeriodId)
        { CloseEditor(); return; }
        if (!DateTimeOffset.TryParseExact((_editTime.Text ?? "").Trim(), "yyyy-MM-dd HH:mm:ss zzz",
            CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var timestamp))
        { ShowActionError("请输入有效时间与时区，例如 2026-09-08 12:30:00 +09:00。"); return; }
        if (timestamp > DateTimeOffset.Now) { ShowActionError("统计起点不能晚于当前时间。"); return; }
        _actionFailed = false; _editError.Text = "";
        try
        {
            if (_correction && _editingPeriodId is { } id)
            {
                if (CorrectStartRequested == null) ShowActionError("周期编辑暂不可用。");
                else CorrectStartRequested.Invoke(id, timestamp);
            }
            else
            {
                if (ManualResetRequested == null) ShowActionError("补记重置暂不可用。");
                else ManualResetRequested.Invoke(timestamp);
            }
        }
        catch (Exception error) { ShowActionError(error.Message); }
        if (!_actionFailed) CloseEditor();
    }

    internal static string Number(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
    internal static string Price(PeriodUsageTotals totals)
    {
        if (totals.MinimumUsd is not { } minimum) return "未计价";
        var maximum = Math.Max(minimum, totals.MaximumUsd ?? minimum);
        static string Amount(decimal value) => value is > 0 and < .0001m
            ? value.ToString("0.############################", CultureInfo.InvariantCulture)
            : value.ToString("#,0.######", CultureInfo.InvariantCulture);
        if (totals.UnpricedResponses > 0) return "≥ US$" + Amount(minimum);
        if (minimum == maximum && minimum is > 0 and < .0001m) return "< US$0.0001";
        return maximum == minimum ? "US$" + Amount(minimum) : "US$" + Amount(minimum) + "–US$" + Amount(maximum);
    }
    internal static string PriceHint(PeriodUsageTotals totals) => $"API 等效估算 {Price(totals)}；{totals.PricedResponses:N0} 条响应已计价，{totals.UnpricedResponses:N0} 条响应 / {totals.UnpricedTokens:N0} Token 未计价。"
        + (totals.MaximumUsd != totals.MinimumUsd ? "金额范围反映服务层级或缓存字段的不确定性。" : "")
        + $" 缺失记录不当作零。价格表 {ApiPricingCatalog.Version}，核验 {ApiPricingCatalog.VerifiedOn:yyyy-MM-dd}。不是实际订阅扣款。";
    private static string Date(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "未知";
    private static string Duration(PeriodUsageTotals totals)
    {
        if (totals.Duration is not { } duration) return "未知";
        var text = duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m"
            : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s" : $"{Math.Max(0, (int)duration.TotalSeconds)}s";
        return totals.UnknownTurns > 0 ? "≥ " + text : text;
    }
    internal static string Health(SampleHealth health) => health switch
    {
        SampleHealth.Loading => "历史补齐中", SampleHealth.Fresh => "已更新", SampleHealth.Partial => "部分记录",
        SampleHealth.Stale => "证据过期", _ => "数据不可用"
    };
    internal static string PeriodSource(UsagePeriod period)
    {
        string reason = period.Reason switch { "Natural" => "自然到期", "Card" => "重置卡重置", "Recovery" => "额度恢复", "Manual" => "人工补记", _ => "窗口推算" };
        if (period.IsCorrected) reason += " · 已人工校正";
        else if (period.IsEstimated) reason += " · 起点为估计";
        if (period.IsPending) reason += " · 额度恢复待确认";
        return reason;
    }
    private static TextBlock Label(string text, double size) => new() { Text = text, FontSize = size,
        Foreground = Brush.Parse("#CDD9C6"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, Foreground = Brush.Parse("#B7D776"), Padding = new Thickness(7, 4) };
        button.Click += (_, e) => { action(); e.Handled = true; }; return button;
    }
    private sealed record PeriodOption(string Id, string Title) { public override string ToString() => Title; }
    private sealed record ReportRow(PeriodTaskUsage Task, bool Self, int Depth, bool Expanded)
    {
        public string Key => Task.ThreadId + (Self ? ":self" : ":group");
        public PeriodUsageTotals Totals => Self ? Task.Self : Task.Group;
    }
    private sealed class ViewState
    {
        public string[] Expanded { get; init; } = [];
        public string? Selected { get; init; }
        public string Search { get; init; } = "";
        public int Sort { get; init; }
        public Vector Offset { get; init; }
        public bool RestoreOffset { get; set; }
    }
}
