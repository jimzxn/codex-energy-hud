using System.IO;
using System.Windows;
using CodexHud.Core;

namespace CodexHud;

public partial class MainWindow
{
    private readonly BillingCycleTracker _billingCycles;
    private readonly SemaphoreSlim _billingWake = new(0, 1);
    private BillingReportWindow? _billingReportWindow;
    private string? _billingWindowKey, _billingReportPeriodId;
    private PeriodUsageSnapshot? _billingCurrentData, _billingReportData;
    private volatile bool _billingFixtureActive;

    private void ShowBilling(object sender, RoutedEventArgs e) => TogglePanel("billing");

    private void SignalBillingRefresh()
    {
        if (_stop.IsCancellationRequested) return;
        try { _billingWake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }

    private async Task BillingLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (!_billingFixtureActive)
                    {
                        var cost = Volatile.Read(ref _sessionCostData);
                        var ledger = cost?.Ledger;
                        var now = DateTimeOffset.UtcNow;
                        if (ledger != null && (cost!.Health is SampleHealth.Stale or SampleHealth.Unavailable
                            || now - ledger.ObservedAt > TimeSpan.FromSeconds(20)))
                            ledger = ledger with { Health = SampleHealth.Stale, Detail = "采集已过期，保留此前可验证记录。" };
                        var periods = _billingCycles.GetPeriods(Volatile.Read(ref _billingWindowKey));
                        var current = periods.FirstOrDefault(p => p.IsCurrent) ?? periods.FirstOrDefault();
                        var reportId = Volatile.Read(ref _billingReportPeriodId);
                        var selected = periods.FirstOrDefault(p => p.Id == reportId) ?? current;
                        var activity = Volatile.Read(ref _activityData);
                        var currentData = ledger != null && current != null
                            ? PeriodUsageCalculator.Build(ledger, current, now, activity) : null;
                        var reportData = selected?.Id == current?.Id ? currentData
                            : ledger != null && selected != null ? PeriodUsageCalculator.Build(ledger, selected, now, activity) : null;
                        var quota = Volatile.Read(ref _quotaData);
                        currentData = ApplyBillingPeriodState(currentData, quota, now);
                        reportData = ApplyBillingPeriodState(reportData, quota, now);
                        if (!_billingFixtureActive)
                        {
                            Volatile.Write(ref _billingCurrentData, currentData);
                            Volatile.Write(ref _billingReportData, reportData);
                            await Dispatcher.InvokeAsync(UpdateBilling);
                        }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (!_billingFixtureActive)
                    {
                        var previous = Volatile.Read(ref _billingCurrentData);
                        if (previous != null) Volatile.Write(ref _billingCurrentData,
                            previous with { Health = SampleHealth.Stale, Detail = "周期汇总暂不可用，保留上次结果。" });
                        var report = Volatile.Read(ref _billingReportData);
                        if (report != null) Volatile.Write(ref _billingReportData,
                            report with { Health = SampleHealth.Stale, Detail = "周期汇总暂不可用，保留上次结果。" });
                        await Dispatcher.InvokeAsync(UpdateBilling);
                    }
                }
                await _billingWake.WaitAsync(TimeSpan.FromSeconds(5), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private static PeriodUsageSnapshot? ApplyBillingPeriodState(PeriodUsageSnapshot? snapshot, QuotaSnapshot? quota, DateTimeOffset now)
    {
        if (snapshot == null || !snapshot.Period.IsCurrent) return snapshot;
        string? reason = snapshot.Period.EndsAt is { } end && end <= now
            ? "周期已结束，等待确认新的额度周期；保留上期结果。"
            : quota?.AccountKey == null || quota.AccountKey != snapshot.Period.AccountKey
                ? "等待当前账户的额度观测确认；保留已保存的周期。"
                : quota.Health is SampleHealth.Stale or SampleHealth.Unavailable || now - quota.ObservedAt > TimeSpan.FromSeconds(150)
                    ? "额度观测已过期，周期边界待确认。"
                    : !quota.Windows.Any(w => w.Key == snapshot.Period.WindowKey) ? "所选额度窗口暂不可用，保留已有周期。" : null;
        return reason == null ? snapshot : snapshot with { Health = SampleHealth.Stale, Detail = reason + " " + snapshot.Detail };
    }

    private void UpdateBilling()
    {
        if (!_ready || _exiting) return;
        var periods = _billingCycles.GetPeriods(_billingWindowKey);
        var current = Volatile.Read(ref _billingCurrentData);
        if (!_hidden)
        {
            var period = current?.Period ?? periods.FirstOrDefault(p => p.IsCurrent) ?? periods.FirstOrDefault();
            BillingPeriodText.Text = period == null ? "等待所选额度周期"
                : $"{period.StartedAt.ToLocalTime():MM-dd HH:mm} — {period.EndsAt?.ToLocalTime():MM-dd HH:mm}";
            BillingTokenText.Text = current?.Totals.Counts.TotalTokens is { } tokens ? FormatEstimatedTokens(tokens) : "—";
            BillingCostText.Text = current?.Totals.MinimumUsd is { } low
                ? current.Totals.UnpricedResponses > 0
                    ? "≥ US$" + low.ToString(low < .0001m ? "0.############################" : "#,0.######", System.Globalization.CultureInfo.InvariantCulture)
                    : "≈ " + CostRange(low, current.Totals.MaximumUsd ?? low) : "—";
            BillingStatusText.Text = current == null ? (period == null ? "周期未就绪" : "历史补齐中")
                : $"{BillingHealth(current.Health)} · 更新 {current.ObservedAt.ToLocalTime():HH:mm:ss}";
            if (period?.IsCurrent == true && period.EndsAt <= DateTimeOffset.UtcNow) BillingStatusText.Text = "周期已结束 · 等待新周期";
            if (period?.IsPending == true) BillingStatusText.Text += " · 重置待确认";
            else if (period?.IsEstimated == true) BillingStatusText.Text += " · 起点估计";
            BillingSummaryCard.ToolTip = (current?.Detail ?? "统计所选周期内的本机普通任务。")
                + (current == null ? "" : $"\n未计价响应 {current.Totals.UnpricedResponses:N0} · 未计价 Token {current.Totals.UnpricedTokens:N0}")
                + "\n" + (_billingCycles.Detail ?? "") + "\n点击查看任务明细、历史周期及重置时间校正。";
        }
        if (_billingReportWindow is { } window)
        {
            window.UpdatePeriods(periods);
            var report = Volatile.Read(ref _billingReportData);
            if (report?.Period.Id == window.SelectedPeriodId) window.UpdateReport(report);
            else window.UpdateReport(null);
        }
    }

    private static string BillingHealth(SampleHealth health) => health switch
    {
        SampleHealth.Fresh => "已更新", SampleHealth.Loading => "历史补齐中",
        SampleHealth.Partial => "部分数据", SampleHealth.Stale => "已过期", _ => "暂不可用"
    };

    private void OpenBillingReport(object sender, RoutedEventArgs e)
    {
        if (_billingReportWindow != null)
        {
            if (_billingReportWindow.WindowState == WindowState.Minimized) _billingReportWindow.WindowState = WindowState.Normal;
            _billingReportWindow.Show(); _billingReportWindow.Activate(); UpdateBilling(); return;
        }
        var window = new BillingReportWindow();
        _billingReportWindow = window;
        window.PeriodSelected += id => { Volatile.Write(ref _billingReportPeriodId, id); SignalBillingRefresh(); };
        window.ManualResetRequested += at =>
        {
            if (_billingWindowKey is not { } key) { window.ShowActionError("请先选择有效额度周期。"); return; }
            if (!_billingCycles.AddManualReset(key, at, out var error)) window.ShowActionError(error ?? "无法记录重置。");
            else { Volatile.Write(ref _billingReportPeriodId, null); SignalBillingRefresh(); UpdateBilling(); }
        };
        window.CorrectStartRequested += (id, at) =>
        {
            if (!_billingCycles.CorrectStart(id, at, out var error)) window.ShowActionError(error ?? "无法校正周期。");
            else { SignalBillingRefresh(); UpdateBilling(); }
        };
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_billingReportWindow, window)) return;
            _billingReportWindow = null; Volatile.Write(ref _billingReportPeriodId, null); SignalBillingRefresh();
        };
        window.Show();
        UpdateBilling(); SignalBillingRefresh();
    }
}
