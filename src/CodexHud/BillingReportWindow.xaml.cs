using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexHud.Core;

namespace CodexHud;

/// <summary>Read-only presentation of the shared period ledger; never opens or scans a log.</summary>
public partial class BillingReportWindow : Window
{
    private IReadOnlyList<UsagePeriod> _periods = [];
    private PeriodUsageSnapshot? _snapshot;
    private readonly Dictionary<string, ViewState> _views = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private List<ReportRow> _rows = [];
    private string? _viewPeriodId, _selectedTaskKey, _editingPeriodId;
    private bool _updating, _rendering, _correction, _actionFailed;
    private int _renderRevision;

    public event Action<string>? PeriodSelected;
    public event Action<DateTimeOffset>? ManualResetRequested;
    public event Action<string, DateTimeOffset>? CorrectStartRequested;
    public string? SelectedPeriodId => (PeriodPicker.SelectedItem as PeriodOption)?.Id;

    public BillingReportWindow()
    {
        InitializeComponent();
        Closed += (_, _) => ++_renderRevision;
    }

    public void UpdatePeriods(IReadOnlyList<UsagePeriod> periods)
    {
        Dispatcher.VerifyAccess();
        var oldId = SelectedPeriodId;
        bool followCurrent = oldId == null || _periods.FirstOrDefault(p => p.Id == oldId)?.IsCurrent == true;
        var preferredId = followCurrent ? periods.FirstOrDefault(p => p.IsCurrent)?.Id ?? oldId : oldId;
        _periods = periods;
        var options = periods.OrderByDescending(p => p.IsCurrent).ThenByDescending(p => p.StartedAt)
            .Select(p => new PeriodOption(p.Id, $"{(p.IsCurrent ? "当前 · " : "")}{p.Label} · {p.StartedAt.ToLocalTime():MM-dd HH:mm}{(p.IsPending ? " · 待确认" : "")}"))
            .ToArray();
        _updating = true;
        PeriodPicker.ItemsSource = options;
        PeriodPicker.SelectedItem = options.FirstOrDefault(p => p.Id == preferredId) ?? options.FirstOrDefault();
        PeriodPicker.IsEnabled = options.Length > 0;
        _updating = false;
        if (SelectedPeriodId != oldId)
        {
            if (EditOverlay.Visibility == Visibility.Visible) CloseEditor();
            UpdateReport(null);
            if (SelectedPeriodId is { } id) PeriodSelected?.Invoke(id);
        }
        else CorrectStartButton.IsEnabled = _snapshot != null && _snapshot.Period.Id == SelectedPeriodId;
    }

    public void UpdateReport(PeriodUsageSnapshot? snapshot)
    {
        Dispatcher.VerifyAccess();
        if (snapshot != null && SelectedPeriodId is { } selected && selected != snapshot.Period.Id) return;
        // A null snapshot means asynchronous loading, not that the selected period disappeared.
        var nextPeriodId = snapshot?.Period.Id ?? SelectedPeriodId;
        if (nextPeriodId != _viewPeriodId)
        {
            SaveViewState();
            _viewPeriodId = nextPeriodId;
            RestoreViewState();
        }
        else if (snapshot == null && _snapshot != null)
        {
            SaveViewState();
            if (_viewPeriodId != null && _views.TryGetValue(_viewPeriodId, out var state)) state.RestoreScroll = true;
        }
        _snapshot = snapshot;
        if (snapshot == null)
        {
            TotalValue.Text = PriceValue.Text = "—";
            CoverageValue.Text = "等待本地记录";
            var period = _periods.FirstOrDefault(p => p.Id == SelectedPeriodId);
            PeriodRangeValue.Text = period == null ? "等待周期边界" : $"{Date(period.StartedAt)} — {Date(period.EndsAt)}";
            StatusValue.Text = period == null ? "读取中" : $"历史补齐中 · {PeriodSource(period)}";
            UpdatedValue.Text = "";
        }
        else
        {
            var totals = snapshot.Totals;
            TotalValue.Text = Number(totals.Counts.TotalTokens);
            TotalValue.ToolTip = $"输入 {Number(totals.Counts.InputTokens)} + 输出 {Number(totals.Counts.OutputTokens)}；缓存和推理不重复相加";
            PriceValue.Text = Price(totals);
            PriceValue.ToolTip = PriceHint(totals);
            CoverageValue.Text = $"{totals.PricedResponses:N0} 条已计价 · {totals.UnpricedResponses:N0} 条未计价";
            CoverageValue.ToolTip = PriceHint(totals);
            PeriodRangeValue.Text = $"{Date(snapshot.Period.StartedAt)} — {Date(snapshot.Period.EndsAt)}";
            var flags = PeriodSource(snapshot.Period);
            StatusValue.Text = $"{Health(snapshot.Health)} · {flags}";
            StatusValue.ToolTip = snapshot.Detail ?? flags;
            UpdatedValue.Text = $"更新于 {snapshot.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}";
        }
        CorrectStartButton.IsEnabled = snapshot != null;
        FooterValue.Text = "本机全部普通任务 · 周期按所选额度窗口分段 · 不代表该额度池的独占用量"
            + (string.IsNullOrWhiteSpace(snapshot?.Detail) ? "" : $" · {snapshot.Detail}");
        RenderRows();
    }

    public void ShowActionError(string message)
    {
        _actionFailed = true;
        if (EditOverlay.Visibility == Visibility.Visible) EditError.Text = message;
        else FooterValue.Text = message;
    }

    private void PeriodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (EditOverlay.Visibility == Visibility.Visible) CloseEditor();
        UpdateReport(null);
        if (SelectedPeriodId is { } id) PeriodSelected?.Invoke(id);
    }

    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating && TaskRows != null) RenderRows();
    }

    private void SortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && TaskRows != null) RenderRows();
    }

    private void RenderRows()
    {
        var revision = ++_renderRevision;
        var scroll = FindVisual<ScrollViewer>(TaskRows);
        double vertical = scroll?.VerticalOffset ?? 0;
        if (_viewPeriodId != null && _views.TryGetValue(_viewPeriodId, out var stored) && stored.RestoreScroll)
        {
            vertical = stored.ScrollOffset;
            if (_snapshot != null) stored.RestoreScroll = false;
        }
        var search = SearchBox.Text.Trim();
        _rows = [];
        if (_snapshot != null)
            foreach (var task in Ordered(_snapshot.Tasks).Where(t => MatchesTree(t, search)))
                AppendTask(task, 0, search);
        _rendering = true;
        TaskRows.ItemsSource = _rows;
        TaskRows.SelectedItem = _rows.FirstOrDefault(r => r.Key == _selectedTaskKey);
        _rendering = false;
        EmptyValue.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyValue.Text = search.Length > 0 ? "没有匹配的任务" : _snapshot?.Health == SampleHealth.Loading
            ? "历史补齐中，已采集的数据会自动显示" : _snapshot == null ? "等待本地用量记录" : "本周期尚无可归属的任务记录";
        UpdateDetails();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (revision == _renderRevision) FindVisual<ScrollViewer>(TaskRows)?.ScrollToVerticalOffset(vertical);
        }));
    }

    private IEnumerable<PeriodTaskUsage> Ordered(IEnumerable<PeriodTaskUsage> tasks) => SortPicker.SelectedIndex switch
    {
        1 => tasks.OrderByDescending(t => t.Group.Counts.TotalTokens ?? -1).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
        2 => tasks.OrderByDescending(t => t.Group.Duration?.Ticks ?? -1).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
        3 => tasks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
        _ => tasks.OrderByDescending(t => t.Group.MaximumUsd ?? t.Group.MinimumUsd ?? -1).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
    };

    private static bool Matches(PeriodTaskUsage task, string search) => search.Length == 0
        || task.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || task.ThreadId.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesTree(PeriodTaskUsage task, string search) => Matches(task, search)
        || task.Children.Any(t => MatchesTree(t, search));

    private void AppendTask(PeriodTaskUsage task, int depth, string search)
    {
        bool expanded = _expanded.Contains(task.ThreadId);
        // A matching descendant must be reachable while filtering, without overwriting saved expansion.
        bool revealMatch = search.Length > 0 && !Matches(task, search);
        _rows.Add(new(task, false, depth, expanded || revealMatch));
        if (task.Children.Count == 0 || (!expanded && !revealMatch)) return;
        _rows.Add(new(task, true, depth + 1, false));
        foreach (var child in Ordered(task.Children))
            if (Matches(task, search) || MatchesTree(child, search)) AppendTask(child, depth + 1, search);
    }

    private void ToggleTask(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReportRow row }) Toggle(row);
        e.Handled = true;
    }

    private void Toggle(ReportRow row)
    {
        if (row.IsSelf || row.Task.Children.Count == 0) return;
        if (!_expanded.Remove(row.Task.ThreadId)) _expanded.Add(row.Task.ThreadId);
        RenderRows();
    }

    private void TaskKeyDown(object sender, KeyEventArgs e)
    {
        if (TaskRows.SelectedItem is not ReportRow row || row.IsSelf || row.Task.Children.Count == 0) return;
        if (e.Key == Key.Right && !_expanded.Contains(row.Task.ThreadId)
            || e.Key == Key.Left && _expanded.Contains(row.Task.ThreadId))
        {
            Toggle(row);
            e.Handled = true;
        }
    }

    private void TaskSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;
        _selectedTaskKey = (TaskRows.SelectedItem as ReportRow)?.Key;
        DetailScroll.ScrollToHome();
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        double offset = DetailScroll.VerticalOffset;
        if (TaskRows.SelectedItem is not ReportRow row)
        {
            DetailTitle.Text = "选择任务查看模型、轮次与计价说明";
            DetailText.Text = "父任务合计包含子代理；父子并行用时取并集。缓存命中包含在输入中。费用为本机 API 等效估算，不是实际订阅扣费。";
            return;
        }
        DetailTitle.Text = row.IsSelf ? $"{row.Task.Title} · 仅自身" : row.Task.Title;
        var text = new StringBuilder();
        text.AppendLine($"任务 {row.Task.ThreadId} · {(row.IsSelf || row.Task.Children.Count == 0 ? "自身记录" : "含子代理合计")} · {Health(row.Totals.Health)}");
        text.AppendLine($"总 Token {Number(row.Totals.Counts.TotalTokens)} · 累计用时 {Duration(row.Totals)} · {Price(row.Totals)} USD");
        text.AppendLine(PriceHint(row.Totals));
        text.AppendLine("API 等效费用估算，非订阅账单；时长排除轮次间闲置，组内并行区间不重复累计。");
        if (row.Totals.UnknownTurns > 0) text.AppendLine($"{row.Totals.UnknownTurns:N0} 个轮次缺少完整时间记录，显示已确认用时小计。");
        var tasks = row.IsSelf ? [row.Task] : Descendants(row.Task).ToArray();
        foreach (var task in tasks)
        {
            if (tasks.Length > 1) text.AppendLine($"\n{task.Title} · {(task.IsSubagent ? "子代理" : "自身")}");
            text.AppendLine("模型 / 自身记录：");
            if (task.Models.Count == 0) text.AppendLine("  尚无模型明细");
            foreach (var model in task.Models.OrderByDescending(m => m.Totals.MaximumUsd ?? m.Totals.MinimumUsd ?? -1))
                text.AppendLine($"  {model.Model} · Token {Number(model.Totals.Counts.TotalTokens)} · 输入 {Number(model.Totals.Counts.InputTokens)} / 缓存 {Number(model.Totals.Counts.CachedInputTokens)} / 输出 {Number(model.Totals.Counts.OutputTokens)} · {Price(model.Totals)} USD");
            text.AppendLine($"轮次 / 自身记录（{task.Turns.Count:N0}）：");
            if (task.Turns.Count == 0) text.AppendLine("  暂无可确认归属的轮次时间");
            foreach (var turn in task.Turns.OrderBy(t => t.StartedAt ?? t.EvidenceAt))
            {
                string end = turn.EndedAt is { } ended ? Date(ended)
                    : $"未结束；末次证据 {Date(turn.EvidenceAt)}";
                text.AppendLine($"  {turn.TurnId} · {Date(turn.StartedAt)} → {end} · {Health(turn.Health)}");
            }
            foreach (var note in task.Notes.Distinct()) text.AppendLine($"说明：{note}");
        }
        DetailText.Text = text.ToString().TrimEnd();
        DetailScroll.ScrollToVerticalOffset(offset);
    }

    private static IEnumerable<PeriodTaskUsage> Descendants(PeriodTaskUsage task)
    {
        yield return task;
        foreach (var child in task.Children)
            foreach (var nested in Descendants(child)) yield return nested;
    }

    private void SaveViewState()
    {
        if (_viewPeriodId == null) return;
        var pendingOffset = _views.TryGetValue(_viewPeriodId, out var previous) && previous.RestoreScroll ? previous.ScrollOffset : (double?)null;
        _views[_viewPeriodId] = new ViewState
        {
            Expanded = _expanded.ToArray(), Selected = _selectedTaskKey,
            Search = SearchBox.Text, Sort = SortPicker.SelectedIndex,
            ScrollOffset = pendingOffset ?? FindVisual<ScrollViewer>(TaskRows)?.VerticalOffset ?? 0
        };
    }

    private void RestoreViewState()
    {
        _expanded.Clear();
        _selectedTaskKey = null;
        _updating = true;
        if (_viewPeriodId != null && _views.TryGetValue(_viewPeriodId, out var state))
        {
            _expanded.UnionWith(state.Expanded);
            _selectedTaskKey = state.Selected;
            SearchBox.Text = state.Search;
            SortPicker.SelectedIndex = state.Sort;
            state.RestoreScroll = true;
        }
        else
        {
            SearchBox.Text = "";
            SortPicker.SelectedIndex = 0;
            if (_viewPeriodId != null) _views[_viewPeriodId] = new ViewState { RestoreScroll = true };
        }
        _updating = false;
    }

    private void AddReset(object sender, RoutedEventArgs e) => OpenEditor(false);
    private void CorrectStart(object sender, RoutedEventArgs e) => OpenEditor(true);

    private void OpenEditor(bool correction)
    {
        if (correction && (_snapshot == null || _snapshot.Period.Id != SelectedPeriodId)) return;
        _correction = correction;
        _editingPeriodId = correction ? _snapshot!.Period.Id : null;
        EditTitle.Text = correction ? "校正周期起点" : "补记一次重置";
        EditTimestamp.Text = (correction ? _snapshot!.Period.StartedAt : DateTimeOffset.Now)
            .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        EditError.Text = "";
        EditOverlay.Visibility = Visibility.Visible;
        EditTimestamp.Focus();
        EditTimestamp.SelectAll();
    }

    private void CancelEdit(object sender, RoutedEventArgs e) => CloseEditor();
    private void CloseEditor()
    {
        EditOverlay.Visibility = Visibility.Collapsed;
        _editingPeriodId = null;
        TaskRows.Focus();
    }

    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseEditor(); e.Handled = true; }
        else if (e.Key == Key.Enter) { SaveEdit(sender, e); e.Handled = true; }
    }

    private void SaveEdit(object sender, RoutedEventArgs e)
    {
        if (EditOverlay.Visibility != Visibility.Visible) return;
        if (_correction && (_editingPeriodId == null || _editingPeriodId != SelectedPeriodId
            || _snapshot?.Period.Id != _editingPeriodId))
        {
            CloseEditor();
            return;
        }
        if (!DateTimeOffset.TryParseExact(EditTimestamp.Text.Trim(), "yyyy-MM-dd HH:mm:ss zzz",
            CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var timestamp))
        {
            EditError.Text = "请输入有效时间与时区，例如 2026-09-08 12:30:00 +09:00。";
            return;
        }
        if (timestamp > DateTimeOffset.Now)
        {
            EditError.Text = "统计起点不能晚于当前时间。";
            return;
        }
        EditError.Text = "";
        _actionFailed = false;
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

    internal static string Number(long? value) => value?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
    internal static string Price(PeriodUsageTotals totals)
    {
        if (totals.MinimumUsd is not { } minimum) return "未计价";
        var maximum = totals.MaximumUsd ?? minimum;
        static string Amount(decimal value) => value is > 0 and < .0001m
            ? value.ToString("0.############################", CultureInfo.InvariantCulture)
            : value.ToString("N4", CultureInfo.InvariantCulture);
        if (totals.UnpricedResponses > 0) return "≥ $" + Amount(minimum);
        if (minimum == maximum && minimum is > 0 and < .0001m) return "< $0.0001";
        return maximum == minimum ? "$" + Amount(minimum) : "$" + Amount(minimum) + "–$" + Amount(maximum);
    }
    private static string PriceHint(PeriodUsageTotals totals) => $"API 等效估算 {Price(totals)} USD；{totals.PricedResponses:N0} 条响应已计价，{totals.UnpricedResponses:N0} 条响应 / {totals.UnpricedTokens:N0} Token 未计价。"
        + (totals.MaximumUsd != totals.MinimumUsd ? "金额范围反映服务层级或缓存字段的不确定性。" : "")
        + " 缺失记录不当作零。";
    private static string Date(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "未知";
    private static string Duration(PeriodUsageTotals totals)
    {
        if (totals.Duration is not { } duration) return "未知";
        var text = duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m"
            : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s" : $"{Math.Max(0, (int)duration.TotalSeconds)}s";
        return totals.UnknownTurns > 0 ? $"≥ {text}" : text;
    }
    private static string Health(SampleHealth health) => health switch
    {
        SampleHealth.Loading => "历史补齐中", SampleHealth.Fresh => "已更新",
        SampleHealth.Partial => "部分记录", SampleHealth.Stale => "证据过期", _ => "数据不可用"
    };
    private static string PeriodSource(UsagePeriod period)
    {
        string reason = period.Reason switch
        {
            "Natural" => "自然到期", "Card" => "重置卡重置", "Recovery" => "额度恢复",
            "Manual" => "人工补记", _ => "窗口推算"
        };
        if (period.IsCorrected) reason += " · 已人工校正";
        else if (period.IsEstimated) reason += " · 起点为估计";
        if (period.IsPending) reason += " · 额度恢复待确认";
        return reason;
    }
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }

    // In-process UI checks use the same control event paths as user interaction.
    internal int VisibleRowCount => _rows.Count;
    internal IReadOnlyList<string> VisibleRowKeys => _rows.Select(r => r.Key).ToArray();
    internal IReadOnlyList<string> ExpandedTaskIds => _expanded.ToArray();
    internal string? SelectedTaskKey => (TaskRows.SelectedItem as ReportRow)?.Key;
    internal void SetSearchForCheck(string search) => SearchBox.Text = search;
    internal void SetPeriodForCheck(string id) => PeriodPicker.SelectedItem = PeriodPicker.Items.OfType<PeriodOption>().FirstOrDefault(p => p.Id == id);
    internal void SetSortForCheck(int index) => SortPicker.SelectedIndex = index;
    internal void ToggleTaskForCheck(string threadId)
    {
        if (_rows.FirstOrDefault(r => r.Task.ThreadId == threadId && !r.IsSelf) is { } row) Toggle(row);
    }
    internal void SelectTaskForCheck(string key) => TaskRows.SelectedItem = _rows.FirstOrDefault(r => r.Key == key);
    internal void BeginManualResetForCheck() => AddReset(this, new RoutedEventArgs());
    internal void BeginCorrectStartForCheck() => CorrectStart(this, new RoutedEventArgs());
    internal void SetActionTimeForCheck(DateTimeOffset timestamp) => EditTimestamp.Text = timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    internal void ConfirmActionForCheck() => SaveEdit(this, new RoutedEventArgs());
    internal string ActionErrorText => EditError.Text;
    internal bool IsActionEditorOpen => EditOverlay.Visibility == Visibility.Visible;
    internal void SetReportScaleForCheck(double scale) => ReportScale.ScaleX = ReportScale.ScaleY = scale;
    internal double ScrollOffsetForCheck => FindVisual<ScrollViewer>(TaskRows)?.VerticalOffset ?? 0;
    internal void SetScrollOffsetForCheck(double offset) => FindVisual<ScrollViewer>(TaskRows)?.ScrollToVerticalOffset(offset);

    private sealed record PeriodOption(string Id, string Title);
    private sealed class ViewState
    {
        public string[] Expanded { get; init; } = [];
        public string? Selected { get; init; }
        public string Search { get; init; } = "";
        public int Sort { get; init; }
        public double ScrollOffset { get; init; }
        public bool RestoreScroll { get; set; }
    }
    private sealed class ReportRow(PeriodTaskUsage task, bool isSelf, int depth, bool expanded)
    {
        public PeriodTaskUsage Task { get; } = task;
        public bool IsSelf { get; } = isSelf;
        public PeriodUsageTotals Totals => IsSelf ? Task.Self : Task.Group;
        public string Key => Task.ThreadId + (IsSelf ? ":self" : ":group");
        public string Title => IsSelf ? "自身" : Task.Title;
        public string Tag => IsSelf ? "仅此任务" : $"{(Task.IsSubagent ? "子代理" : "主任务")}{(Task.Children.Count > 0 ? " · 含子代理" : "")}{(Task.Archived ? " · 已归档" : "")}";
        public Thickness Indent => new(Math.Min(depth, 5) * 12, 0, 0, 0);
        public FontWeight Weight => !IsSelf && Task.Children.Count > 0 ? FontWeights.SemiBold : FontWeights.Normal;
        public Visibility ExpandVisibility => !IsSelf && Task.Children.Count > 0 ? Visibility.Visible : Visibility.Hidden;
        public string ExpandGlyph => expanded ? "▾" : "▸";
        public string ExpandLabel => $"{(expanded ? "收起" : "展开")} {Task.Title}";
        public string TotalText => Number(Totals.Counts.TotalTokens);
        public string InputText => Number(Totals.Counts.InputTokens);
        public string CacheText => Number(Totals.Counts.CachedInputTokens);
        public string OutputText => Number(Totals.Counts.OutputTokens);
        public string PriceText => Price(Totals);
        public string DurationText => Duration(Totals);
        public string Hint => $"{Task.Title}\n{Task.ThreadId}\n总 Token {TotalText} · 输入 {InputText} · 缓存 {CacheText} · 输出 {OutputText}\n累计用时 {DurationText} · {Health(Totals.Health)}\n{PriceHint(Totals)}";
    }
}
