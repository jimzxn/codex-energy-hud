using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodexHud.Core;

namespace CodexHud.Mac;

internal sealed partial class MainWindow
{
    private readonly StackPanel _billingPanel = new() { Spacing = 10 };
    private readonly ListBox _billingPeriodList = new() { MaxHeight = 170, Background = Brushes.Transparent };
    private readonly TextBlock _billingSelection = Text("本周期", 13), _billingRange = Text("等待额度周期", 11);
    private readonly TextBlock _billingTokens = Text("Token —", 17), _billingCost = Text("API 等效 —", 15);
    private readonly TextBlock _billingStatus = Text("周期未就绪", 10), _billingEmpty = Text("等待额度观测建立周期记录", 11);
    private IReadOnlyList<UsagePeriod> _billingListed = [];
    private BillingReportWindow? _billingReportWindow;
    private bool _billingUpdating;
    private int _billingListRevision;

    private void BuildBilling()
    {
        _billingPanel.Children.Add(Text("周期用量与 API 等效费用", 14));
        _billingPanel.Children.Add(Text("本机全部普通任务按所选额度周期汇总。费用按公开 API 价格换算，不代表订阅实际扣款或该额度池独占用量。", 11));
        _billingPeriodList.SelectionChanged += (_, _) =>
        {
            if (_billingUpdating || _billingPeriodList.SelectedItem is not ListBoxItem { Tag: UsagePeriod period }) return;
            _controller.SelectBillingPeriod(period.Id);
            UpdateBilling();
        };
        _billingPanel.Children.Add(_billingPeriodList);
        _billingPanel.Children.Add(_billingEmpty);
        var summary = Button(new StackPanel { Spacing = 7,
            Children = { _billingSelection, _billingRange, _billingTokens, _billingCost, _billingStatus, Text("点击查看任务、模型与周期起点校正 →", 11) } }, OpenBillingReport);
        summary.Background = Brush.Parse("#1C261D");
        summary.Padding = new Thickness(12);
        summary.HorizontalAlignment = HorizontalAlignment.Stretch;
        _billingPanel.Children.Add(summary);
    }

    private void UpdateBilling()
    {
        if (_exiting) return;
        var periods = _controller.BillingPeriods;
        var selectedId = _controller.SelectedBillingPeriodId;
        var period = periods.FirstOrDefault(p => p.Id == selectedId) ?? periods.FirstOrDefault(p => p.IsCurrent);
        var report = _controller.BillingReport;
        var selected = report?.Period.Id == period?.Id ? report
            : _controller.BillingCurrent is { } current && current.Period.Id == period?.Id ? current : null;
        _billingUpdating = true;
        try
        {
            if (!_billingListed.SequenceEqual(periods))
            {
                var revision = ++_billingListRevision;
                var offset = _billingPeriodList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset ?? default;
                _billingListed = periods;
                int historyIndex = 0;
                _billingPeriodList.ItemsSource = periods.Select(p => new ListBoxItem { Tag = p,
                    Content = new StackPanel { Spacing = 3, Children = {
                        Text(p.IsCurrent ? "本周期" : ++historyIndex == 1 ? "上个周期" : "历史周期", 12),
                        Text($"{p.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} — {p.EndsAt?.ToLocalTime():MM-dd HH:mm}", 11),
                        Text(BillingReportWindow.PeriodSource(p), 10) } } }).ToArray();
                Dispatcher.UIThread.Post(() => {
                    if (revision == _billingListRevision && _billingPeriodList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
                        scroll.Offset = offset;
                });
            }
            _billingPeriodList.SelectedItem = _billingPeriodList.Items.OfType<ListBoxItem>()
                .FirstOrDefault(row => row.Tag is UsagePeriod candidate && candidate.Id == period?.Id);
            if (_billingReportWindow is { } window)
            {
                window.UpdatePeriods(periods, period?.Id);
                window.UpdateReport(selected);
            }
        }
        finally { _billingUpdating = false; }
        _billingEmpty.IsVisible = periods.Count <= 1;
        _billingEmpty.Text = periods.Count == 0 ? "等待额度观测建立周期记录" : "暂无更早周期记录";
        _billingSelection.Text = period?.IsCurrent == true ? "本周期" : period is null ? "周期用量" : "所选历史周期";
        _billingRange.Text = period is null ? "等待所选额度周期"
            : $"{period.Label} · {period.StartedAt.ToLocalTime():MM-dd HH:mm} — {period.EndsAt?.ToLocalTime():MM-dd HH:mm}";
        _billingTokens.Text = "Token " + (selected is null ? "—" : Count(selected.Totals.Counts.TotalTokens));
        _billingCost.Text = "API 等效 " + (selected is null ? "—" : BillingReportWindow.Price(selected.Totals));
        _billingStatus.Text = selected is null ? period is null ? "周期未就绪" : "历史补齐中"
            : $"{BillingReportWindow.Health(selected.Health)} · 更新 {selected.ObservedAt.ToLocalTime():HH:mm:ss}";
        if (period is not null) _billingStatus.Text += " · " + BillingReportWindow.PeriodSource(period);
        if (period?.IsCurrent == true && period.EndsAt <= DateTimeOffset.UtcNow) _billingStatus.Text = "周期已结束 · 等待新周期";
        Hint(_billingCost, selected is null ? "等待读取本地会话费用记录。" : BillingReportWindow.PriceHint(selected.Totals));
        Hint(_billingStatus, (selected?.Detail ?? "") + "\n" + _controller.BillingDetail);
    }

    private void OpenBillingReport()
    {
        if (_billingReportWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Show(); existing.Activate(); UpdateBilling(); return;
        }
        var window = new BillingReportWindow();
        _billingReportWindow = window;
        window.PeriodSelected += id => { if (!_billingUpdating) { _controller.SelectBillingPeriod(id); UpdateBilling(); } };
        window.ManualResetRequested += at =>
        {
            if (!_controller.AddManualReset(at, out var error)) window.ShowActionError(error ?? "无法记录重置。");
            else UpdateBilling();
        };
        window.CorrectStartRequested += (id, at) =>
        {
            if (!_controller.CorrectBillingStart(id, at, out var error)) window.ShowActionError(error ?? "无法校正周期。");
            else UpdateBilling();
        };
        window.Closed += (_, _) => { if (ReferenceEquals(_billingReportWindow, window)) _billingReportWindow = null; };
        UpdateBilling();
        window.Show();
    }

    private void CloseBillingReport() { _billingReportWindow?.Close(); _billingReportWindow = null; }
}
