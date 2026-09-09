using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexHud.Core;

namespace CodexHud;

public partial class MainWindow
{
    private async Task RunBillingUiChecks(List<object> results, string directory)
    {
        var originalQuota = _quotaData;
        var originalKey = _billingWindowKey;
        var originalSelection = _settings.SelectedQuotaKey;
        var originalReportId = _billingReportPeriodId;
        var originalCurrent = _billingCurrentData;
        var originalReport = _billingReportData;
        var originalScale = _settings.Scale;
        var originalCostDisplay = SessionCostCheck.IsChecked;
        _billingFixtureActive = true;
        try
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            string key = "billing-ui/primary/60";
            _billingCycles.Observe(new QuotaSnapshot(now, [new(key, "codex", "报告验收 · 1 小时", "primary", 60, 20, now.AddMinutes(30))],
                SampleHealth.Fresh) { AccountKey = "billing-ui-account" });
            _billingCycles.AddManualReset(key, now.AddMinutes(-10), out _);
            _settings.SelectedQuotaKey = key;
            _billingWindowKey = key;
            _billingReportPeriodId = null;
            var tasks = Enumerable.Range(0, 40).Select(i => new LocalUsageTask("billing-" + i,
                i == 0 ? "周期报告验收 · 主任务和归档子代理，长标题完整显示于悬停详情" : $"任务 {i:00}",
                null, false, false, SampleHealth.Fresh, [])).ToList();
            tasks.Add(new("billing-child", "归档子代理 · 模型与轮次明细", "billing-0", true, true, SampleHealth.Fresh, []));
            var responses = tasks.Select((t, i) => new LocalUsageResponse(t.ThreadId, "r", "turn",
                now.AddMinutes(-5), new TokenCounts(1000L * (i + 1), 800L * (i + 1), 0, 100L * (i + 1), 0, 1100L * (i + 1)),
                "gpt-5.3-codex", "standard", "openai")).ToArray();
            var turns = tasks.Select(t => new LocalTaskTurn(t.ThreadId, "turn", now.AddMinutes(-6),
                now.AddMinutes(-4), now.AddMinutes(-4), ActivityState.Completed, SampleHealth.Fresh)).ToArray();
            var ledger = new LocalUsageLedgerSnapshot(now, tasks.ToDictionary(t => t.ThreadId), responses, turns, SampleHealth.Fresh);
            void Publish()
            {
                var periods = _billingCycles.GetPeriods(key);
                var current = periods.First(p => p.IsCurrent);
                var selected = periods.FirstOrDefault(p => p.Id == _billingReportPeriodId) ?? current;
                _billingCurrentData = PeriodUsageCalculator.Build(ledger, current, now);
                _billingReportData = selected.Id == current.Id ? _billingCurrentData : PeriodUsageCalculator.Build(ledger, selected, now);
                UpdateBilling();
            }
            Publish(); OpenPanel("billing"); UpdateLayout();
            var scopeProbe = _billingCurrentData!;
            var expired = ApplyBillingPeriodState(scopeProbe with { Period = scopeProbe.Period with { EndsAt = now.AddSeconds(-1) } }, null, now);
            var restored = ApplyBillingPeriodState(scopeProbe, null, now);
            var historyProbe = scopeProbe with { Period = scopeProbe.Period with { IsCurrent = false } };
            results.Add(new { scenario = "billing-current-boundary-health", passed = expired?.Health == SampleHealth.Stale
                && expired.Detail!.Contains("周期已结束") && restored?.Health == SampleHealth.Stale
                && ReferenceEquals(ApplyBillingPeriodState(historyProbe, null, now), historyProbe) });
            var tiny = scopeProbe.Totals with { MinimumUsd = .0000001m, MaximumUsd = .0000001m };
            results.Add(new { scenario = "billing-tiny-and-unpriced-price", passed = BillingReportWindow.Price(tiny) == "< $0.0001"
                && BillingReportWindow.Price(tiny with { UnpricedResponses = 1 }).Contains("0.0000001") });
            results.Add(new { scenario = "billing-tab-summary-only", passed = BillingPanel.Visibility == Visibility.Visible
                && TasksPanel.Visibility == Visibility.Collapsed && UsagePanel.Visibility == Visibility.Collapsed
                && !Descendants<ItemsControl>(BillingPanel).Any()
                && BillingTokenText.Text != "—" && BillingCostText.Text.Contains("US$") });
            foreach (double scale in new[] { 1d, 1.5d, 2d })
            {
                _settings.Scale = scale; ApplyAppearance(); UpdateLayout();
                Capture(Path.Combine(directory, $"billing-tab-{scale * 100:0}.png"));
                results.Add(new { scenario = $"billing-tab-scale-{scale}", passed = Math.Abs(ActualWidth - 654 * scale) < 2
                    && BillingTokenText.ActualHeight > 0 && BillingCostText.ActualWidth > 150
                    && ActualHeight <= WindowPlacementService.WorkingHeightDip(this, _settings) });
            }
            _settings.Scale = 1; ApplyAppearance();
            OpenBillingReport(this, new RoutedEventArgs());
            var window = _billingReportWindow!;
            Publish(); await Task.Delay(100); window.UpdateLayout();
            OpenBillingReport(this, new RoutedEventArgs());
            results.Add(new { scenario = "billing-single-report-window", passed = ReferenceEquals(window, _billingReportWindow)
                && System.Windows.Application.Current.Windows.OfType<BillingReportWindow>().Count() == 1
                && window.VisibleRowCount == 40, rows = window.VisibleRowCount, selected = window.SelectedPeriodId, expected = _billingCurrentData?.Period.Id, key = _billingWindowKey, periods = _billingCycles.GetPeriods(_billingWindowKey).Count });
            var currentPeriod = _billingCycles.GetPeriods(key).First(p => p.IsCurrent);
            var historicalPeriod = _billingCycles.GetPeriods(key).First(p => !p.IsCurrent);
            window.BeginCorrectStartForCheck();
            window.SetPeriodForCheck(historicalPeriod.Id);
            window.ConfirmActionForCheck();
            results.Add(new { scenario = "billing-period-change-cancels-old-editor", passed = !window.IsActionEditorOpen
                && _billingCycles.GetPeriods(key).First(p => p.Id == currentPeriod.Id).StartedAt == currentPeriod.StartedAt
                && !((Button)window.FindName("CorrectStartButton")).IsEnabled });
            Publish(); await Task.Delay(80);
            results.Add(new { scenario = "billing-history-selection", passed = window.SelectedPeriodId == historicalPeriod.Id
                && _billingCurrentData?.Period.Id == currentPeriod.Id && _billingReportData?.Period.Id == historicalPeriod.Id });
            window.SetPeriodForCheck(currentPeriod.Id); Publish(); await Task.Delay(80);
            window.ToggleTaskForCheck("billing-0");
            window.SelectTaskForCheck("billing-child:group");
            await Task.Delay(100);
            results.Add(new { scenario = "billing-tree-and-task-details", passed = window.VisibleRowCount == 42
                && window.VisibleRowKeys.Contains("billing-0:self") && window.VisibleRowKeys.Contains("billing-child:group")
                && ((TextBlock)window.FindName("DetailText")).Text.Contains("gpt-5.3-codex") });
            window.SetScrollOffsetForCheck(120);
            window.UpdateLayout();
            var offset = window.ScrollOffsetForCheck;
            ledger = ledger with { ObservedAt = now.AddSeconds(1) };
            Publish(); await Task.Delay(100); window.UpdateLayout();
            results.Add(new { scenario = "billing-refresh-preserves-view", passed = window.ExpandedTaskIds.Contains("billing-0")
                && window.SelectedTaskKey == "billing-child:group" && Math.Abs(window.ScrollOffsetForCheck - offset) < 1 });
            window.SetSearchForCheck("归档子代理"); window.UpdateLayout();
            results.Add(new { scenario = "billing-search-descendant-keeps-parent", passed = window.VisibleRowKeys.Contains("billing-0:group")
                && window.VisibleRowKeys.Contains("billing-child:group") && window.VisibleRowCount < 5 });
            window.SetSearchForCheck(""); window.SetSortForCheck(1); window.UpdateLayout();
            results.Add(new { scenario = "billing-sort-token", passed = window.VisibleRowCount >= 40 });
            window.SetSortForCheck(0); window.SetScrollOffsetForCheck(0);
            window.SelectTaskForCheck("billing-0:group");
            foreach (double width in new[] { 1100d, 820d })
            {
                window.Width = width; window.Height = 720; window.UpdateLayout();
                foreach (double dpiScale in new[] { 1d, 1.5d, 2d })
                {
                    CaptureBillingReport(window, Path.Combine(directory, $"billing-report-{width:0}-{dpiScale * 100:0}.png"), dpiScale);
                    var list = (ListBox)window.FindName("TaskRows");
                    results.Add(new { scenario = $"billing-report-layout-{width}-{dpiScale}", passed = list.ActualWidth >= 740
                        && list.ActualHeight >= 80 && ((TextBlock)window.FindName("TotalValue")).ActualWidth > 100 });
                }
            }
            window.Width = 1100;
            Hide();
            var oldText = ((TextBlock)window.FindName("UpdatedValue")).Text;
            ledger = ledger with { ObservedAt = now.AddSeconds(5) }; Publish();
            results.Add(new { scenario = "billing-report-updates-with-hud-hidden", passed = window.IsVisible && !IsVisible
                && ((TextBlock)window.FindName("UpdatedValue")).Text != oldText });
            RestoreFromTray();
            SessionCostCheck.IsChecked = false;
            results.Add(new { scenario = "billing-collection-independent-of-cost-display", passed = _sessionCostEnabled
                && _billingCurrentData?.Totals.Counts.TotalTokens > 0 && _billingReportWindow?.IsVisible == true });
            window.BeginCorrectStartForCheck(); window.SetActionTimeForCheck(now.AddMinutes(-11)); window.ConfirmActionForCheck();
            Publish();
            results.Add(new { scenario = "billing-correct-start-through-gui", passed = _billingCycles.GetPeriods(key)[0].StartedAt == now.AddMinutes(-11)
                && _billingCycles.GetPeriods(key)[0].IsCorrected });
            window.BeginManualResetForCheck(); window.SetActionTimeForCheck(now.AddMinutes(-2)); window.ConfirmActionForCheck();
            Publish();
            results.Add(new { scenario = "billing-manual-reset-through-gui", passed = _billingCycles.GetPeriods(key).Count == 3
                && _billingCycles.GetPeriods(key)[0].StartedAt == now.AddMinutes(-2) });
            foreach (var state in new[] { SampleHealth.Loading, SampleHealth.Partial, SampleHealth.Stale })
            {
                ledger = ledger with { Health = state }; Publish();
                results.Add(new { scenario = $"billing-state-{state}", passed = BillingStatusText.Text.Contains(BillingHealth(state)) });
            }
            var firstWindow = window;
            window.Close();
            OpenBillingReport(this, new RoutedEventArgs()); Publish();
            results.Add(new { scenario = "billing-close-reopen", passed = _billingReportWindow != null
                && !ReferenceEquals(firstWindow, _billingReportWindow) && _billingReportWindow.IsVisible });
            _billingReportWindow?.Close();
            results.Add(new { scenario = "billing-window-close-clears-reference", passed = _billingReportWindow == null });
        }
        catch (Exception ex)
        {
            results.Add(new { scenario = "billing-ui-unexpected-error", passed = false, error = ex.ToString() });
        }
        finally
        {
            _billingReportWindow?.Close(); _billingReportWindow = null;
            SessionCostCheck.IsChecked = originalCostDisplay;
            if (originalQuota != null) _billingCycles.Observe(originalQuota with { ObservedAt = DateTimeOffset.UtcNow });
            _settings.SelectedQuotaKey = originalSelection;
            _billingWindowKey = originalKey; _billingReportPeriodId = originalReportId;
            _billingCurrentData = originalCurrent; _billingReportData = originalReport;
            _billingFixtureActive = false; _settings.Scale = originalScale;
            ApplyAppearance(); UpdateBilling(); SignalBillingRefresh();
        }
    }

    private static void CaptureBillingReport(Window window, string path, double scale)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale),
            (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
