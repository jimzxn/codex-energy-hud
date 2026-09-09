using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CodexHud.Core;

namespace CodexHud;

public partial class MainWindow
{
    private readonly SessionCostProvider _sessionCosts;
    private readonly object _sessionCostGate = new();
    private readonly SemaphoreSlim _sessionCostWake = new(0, 1);
    private CancellationTokenSource? _sessionCostRead;
    private SessionCostSnapshot? _sessionCostData;
    private volatile bool _sessionCostEnabled;
    private volatile bool _sessionCostFixtureActive;

    private async Task SessionCostLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                CancellationTokenSource? read = null;
                lock (_sessionCostGate)
                {
                    if (_sessionCostEnabled)
                        _sessionCostRead = read = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                }
                if (read != null)
                {
                    try
                    {
                        var activityIds = Volatile.Read(ref _activityData)?.Tasks.Select(t => t.Id) ?? [];
                        var workloadIds = Volatile.Read(ref _workloadData)?.Tasks
                            .Where(t => t.History.Any(tick => tick.Counts?.TotalTokens > 0)).Select(t => t.ThreadId) ?? [];
                        _sessionCosts.SetTrackedThreads(activityIds.Concat(workloadIds).Distinct(StringComparer.Ordinal));
                        var snapshot = await _sessionCosts.ReadAsync(read.Token);
                        if (!_sessionCostFixtureActive) { Volatile.Write(ref _sessionCostData, snapshot); SignalBillingRefresh(); }
                    }
                    catch (OperationCanceledException) when (read.IsCancellationRequested) { }
                    catch
                    {
                        var previous = Volatile.Read(ref _sessionCostData);
                        if (!_sessionCostFixtureActive)
                            Volatile.Write(ref _sessionCostData, previous == null
                                ? new SessionCostSnapshot(DateTimeOffset.UtcNow,
                                    new Dictionary<string, SessionCostEstimate>(), SampleHealth.Unavailable, "费用采集暂不可用。")
                                : previous with { Health = SampleHealth.Stale, Detail = "费用采集暂不可用，保留上次累计估算。" });
                    }
                    finally
                    {
                        lock (_sessionCostGate)
                        {
                            if (ReferenceEquals(_sessionCostRead, read)) _sessionCostRead = null;
                            read.Dispose();
                        }
                    }
                    // Collection continues in the tray; rendering resumes only when visible.
                    if (!_hidden && !_stop.IsCancellationRequested)
                        await Dispatcher.InvokeAsync(UpdateSessionCosts);
                }
                await _sessionCostWake.WaitAsync(TimeSpan.FromSeconds(5), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void ChangeSessionCost(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.ShowSessionCost = SessionCostCheck.IsChecked == true;
        lock (_sessionCostGate)
        {
            _sessionCostEnabled = true;
            // The checkbox only controls the two legacy cost rows; period collection continues.
        }
        if (_sessionCostWake.CurrentCount == 0) _sessionCostWake.Release();
        UpdateSessionCosts();
        SaveSettings();
    }

    private void UpdateSessionCosts()
    {
        if (!_ready || _hidden) return;
        var snapshot = Volatile.Read(ref _sessionCostData);
        var now = DateTimeOffset.UtcNow;
        foreach (var row in _taskRows)
            row.UpdateCost(PresentCost(snapshot, row.Id, _settings.ShowSessionCost, now));
        foreach (var row in _workloadRows)
            row.UpdateCost(PresentCost(snapshot, row.Id, _settings.ShowSessionCost, now, row.IsSubagent));
    }

    private sealed record CostPresentation(string Text, string Hint, Visibility Visibility)
    {
        public static CostPresentation Hidden { get; } = new("", "", Visibility.Collapsed);
    }

    private static CostPresentation PresentCost(SessionCostSnapshot? snapshot, string threadId,
        bool enabled, DateTimeOffset now, bool isSubagent = false)
    {
        if (!enabled) return CostPresentation.Hidden;
        var common = "按 OpenAI 公开 API Token 价格换算，不代表订阅实际扣款。\n"
            + "会话创建以来的本机响应记录；主任务包含已确认的子代理，子代理行显示自身。\n"
            + $"价格表 {ApiPricingCatalog.Version} · 核验 {ApiPricingCatalog.VerifiedOn:yyyy-MM-dd}\n"
            + ApiPricingCatalog.Sources[0];
        if (snapshot == null || !snapshot.Tasks.TryGetValue(threadId, out var estimate))
            return new(snapshot?.Health is SampleHealth.Stale or SampleHealth.Unavailable
                ? "累计 — · 暂不可用" : "累计 — · 等待费用记录",
                common + "\n" + (snapshot?.Detail ?? "等待读取会话历史。"), Visibility.Visible);

        isSubagent |= estimate.IsSubagent;
        var low = isSubagent ? estimate.SelfUsd : estimate.TotalUsd;
        var high = isSubagent ? estimate.SelfUpperUsd : estimate.TotalUpperUsd;
        var stale = snapshot.Health is SampleHealth.Stale or SampleHealth.Unavailable
            || estimate.Health is SampleHealth.Stale or SampleHealth.Unavailable
            || now - snapshot.ObservedAt > TimeSpan.FromSeconds(20);
        var partial = estimate.Health == SampleHealth.Partial || estimate.UnpricedResponses > 0 || high != low;
        var loading = estimate.Health == SampleHealth.Loading;
        // Response counts are aggregated; a priced descendant cannot establish the child row own amount.
        var hasAmount = estimate.PricedResponses > 0 && (!isSubagent || estimate.DescendantCount == 0)
            || low > 0 || high > 0 || estimate.Health == SampleHealth.Fresh;
        var scope = isSubagent ? "自身" : "含子代理";
        var value = hasAmount ? "≈ " + CostRange(low, high) : "—";
        var state = stale ? " · 已过期" : loading ? " · 历史补齐中" : partial ? " · 部分估算" : "";
        var hint = common + "\n\n"
            + $"自身：{CostRange(estimate.SelfUsd, estimate.SelfUpperUsd)}\n"
            + $"子代理（{estimate.DescendantCount} 项）：{CostRange(estimate.DescendantsUsd, estimate.DescendantsUpperUsd)}\n"
            + $"合计：{CostRange(estimate.TotalUsd, estimate.TotalUpperUsd)}\n"
            + (isSubagent ? "此行显示自身；以上合计另含其子代理。\n" : "此行显示合计；已确认的后代递归汇总。\n")
            + $"已计价响应 {estimate.PricedResponses:N0} · 未计价响应 {estimate.UnpricedResponses:N0} · 未计价 Token {estimate.UnpricedTokens:N0}\n"
            + $"最近用量 {estimate.LastUsageAt?.ToLocalTime():MM-dd HH:mm:ss} · 采集 {snapshot.ObservedAt.ToLocalTime():HH:mm:ss}";
        if (!hasAmount) hint += "\n尚无可确认的计价金额；金额栏不表示零费用。";
        if (partial) hint += "\n部分估算：仅包含已计价记录，区间反映缺失字段。";
        if (stale) hint += "\n采集过期，保留上次累计值。";
        if (!string.IsNullOrWhiteSpace(snapshot.Detail)) hint += "\n" + snapshot.Detail;
        if (estimate.Notes.Count > 0) hint += "\n" + string.Join("\n", estimate.Notes.Distinct(StringComparer.Ordinal));
        return new($"累计 {value} · {scope}{state}", hint, Visibility.Visible);
    }

    private static string CostRange(decimal minimum, decimal maximum)
    {
        maximum = Math.Max(minimum, maximum);
        if (minimum == maximum) return CostUsd(minimum);
        return CostUsd(minimum, true) + "–" + CostUsd(maximum, true);
    }

    private static string CostUsd(decimal amount, bool range = false)
    {
        if (amount is > 0 and < .0001m) return "< US$0.0001";
        var format = amount is > 0 and < .01m || range ? "#,0.######" : "#,0.00";
        return "US$" + amount.ToString(format, CultureInfo.InvariantCulture);
    }

    private void RunSessionCostUiChecks(List<object> results, string directory)
    {
        _sessionCostFixtureActive = true;
        _workloadFixtureActive = true;
        var original = _sessionCostData;
        var originalActivity = _activityData;
        var originalWorkload = _workloadData;
        var originalScale = _settings.Scale;
        var originalEnabled = _settings.ShowSessionCost;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var tasks = Enumerable.Range(0, 16).Select(i => new TaskActivity("cost-fixture-" + i,
                i == 0 ? "费用估算验证 · 主会话累计和已归档子代理费用" : $"费用验证 {i + 1:00}",
                ActivityState.Completed, now, now.AddMinutes(-1), "费用 UI 夹具", now)).ToArray();
            var estimates = tasks.Select((t, i) => new SessionCostEstimate(t.Id)
            {
                SelfUsd = i == 1 ? .00012m : i == 2 ? 123456789.12m : 1.23m,
                SelfUpperUsd = i == 1 ? .00012m : i == 2 ? 123456789.12m : 1.23m,
                DescendantsUsd = 11.11m, DescendantsUpperUsd = 11.11m,
                PricedResponses = 12, DescendantCount = 2, IsSubagent = i == 1,
                Health = SampleHealth.Fresh, LastUsageAt = now,
                Notes = ["未记录服务层级；按 Standard API 价格估算"]
            }).ToDictionary(t => t.ThreadId, StringComparer.Ordinal);
            estimates[tasks[3].Id] = estimates[tasks[3].Id] with
            { Health = SampleHealth.Partial, SelfUpperUsd = 2.34m, UnpricedResponses = 1, UnpricedTokens = 5678 };
            estimates[tasks[4].Id] = new(tasks[4].Id) { Health = SampleHealth.Partial, UnpricedResponses = 1, UnpricedTokens = 5 };
            _sessionCostData = new(now, estimates, SampleHealth.Fresh);
            _activityData = new(now, true, tasks, SampleHealth.Fresh);
            var tick = new UsageTick(DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()).AddSeconds(-1),
                new TokenCounts(1, 0, 0, 1, 0, 2), SampleHealth.Fresh);
            _workloadData = new(now, [tick], tasks.Select((t, i) => new TaskUsageSeries(t.Id, t.Title, i == 1, i == 1, [tick])).ToArray(), SampleHealth.Fresh);
            SessionCostCheck.IsChecked = true;
            _settings.ShowSessionCost = true;
            UpdateActivity(); OpenPanel("usage"); UpdateWorkload(); UpdateSessionCosts();
            var legacyStore = new SettingsStore(Path.Combine(directory, "legacy-cost-settings"));
            Directory.CreateDirectory(legacyStore.DirectoryPath);
            File.WriteAllText(legacyStore.FilePath, "{\"Scale\":1.5}");
            results.Add(new { scenario = "cost-default-enabled-and-compatible-settings", passed = new HudSettings().ShowSessionCost
                && legacyStore.Load().ShowSessionCost && legacyStore.Load().Scale == 1.5
                && _store.Load().ShowSessionCost && SessionCostCheck.IsChecked == true });
            var main = _taskRows[0];
            var child = _taskRows[1];
            results.Add(new { scenario = "cost-shared-lists-main-and-subagent-scope", passed = main.CostText == "累计 ≈ US$12.34 · 含子代理"
                && child.CostText == "累计 ≈ US$0.00012 · 自身"
                && _workloadRows[0].CostText == main.CostText && _workloadRows[1].CostText == child.CostText
                && child.CostHint.Contains("合计：US$11.11") && main.CostHint.Contains("不代表订阅实际扣款") });
            results.Add(new { scenario = "cost-partial-range-and-unknown", passed = _taskRows[3].CostText.Contains("US$12.34–US$13.45")
                && _taskRows[3].CostText.Contains("部分估算") && _taskRows[3].CostHint.Contains("5,678")
                && _taskRows[4].CostText.Contains("累计 —") && !_taskRows[4].CostText.Contains("US$0") });
            var unknownChild = new SessionCostEstimate("unknown-child") { IsSubagent = true, Health = SampleHealth.Partial,
                SelfUsd = 0, SelfUpperUsd = 0, DescendantsUsd = 1, DescendantsUpperUsd = 1,
                DescendantCount = 1, PricedResponses = 1, UnpricedResponses = 1 };
            var unknownChildDisplay = PresentCost(new(now, new Dictionary<string, SessionCostEstimate>
                { [unknownChild.ThreadId] = unknownChild }, SampleHealth.Partial), unknownChild.ThreadId, true, now);
            results.Add(new { scenario = "cost-child-own-unknown-is-not-priced-descendant-zero", passed =
                unknownChildDisplay.Text.Contains("累计 —") && !unknownChildDisplay.Text.Contains("US$0") });
            results.Add(new { scenario = "cost-small-and-large-format", passed = CostUsd(.0000001m) == "< US$0.0001"
                && CostUsd(.005m) == "US$0.005" && CostUsd(0) == "US$0.00"
                && _taskRows[2].CostText.Contains("US$123,456,800.23") });
            foreach (var panel in new[] { "tasks", "usage" })
            {
                OpenPanel(panel); UpdateLayout();
                Descendants<ScrollViewer>(panel == "tasks" ? TaskList : UsageTaskList).First().ScrollToTop();
                DetailsScroll.ScrollToTop();
                foreach (var scale in new[] { 1d, 1.5d, 2d })
                {
                    _settings.Scale = scale; ApplyAppearance(); UpdateLayout();
                    var list = panel == "tasks" ? TaskList : UsageTaskList;
                    var costBlocks = Descendants<TextBlock>(list).Where(t => t.Text.StartsWith("累计 ≈ US$", StringComparison.Ordinal)).ToArray();
                    results.Add(new { scenario = $"cost-layout-{panel}-{scale}", passed = costBlocks.Length > 0
                        && costBlocks.All(b => b.ActualWidth > 300 && b.ActualHeight > 0)
                        && Math.Abs(ActualWidth - 654 * scale) < 2
                        && ActualHeight <= WindowPlacementService.WorkingHeightDip(this, _settings) });
                    Capture(Path.Combine(directory, $"cost-{panel}-{scale * 100:0}.png"));
                }
                _settings.Scale = 1; ApplyAppearance(); UpdateLayout();
                var selected = panel == "tasks" ? (object)_taskRows[5] : _workloadRows[5];
                var listForScroll = panel == "tasks" ? TaskList : UsageTaskList;
                listForScroll.SelectedItem = selected;
                listForScroll.ScrollIntoView(selected); UpdateLayout();
                var scroll = Descendants<ScrollViewer>(listForScroll).First();
                scroll.ScrollToVerticalOffset(panel == "tasks" ? 80 : 4); UpdateLayout();
                var offset = scroll.VerticalOffset;
                var previousText = panel == "tasks" ? _taskRows[5].CostText : _workloadRows[5].CostText;
                var updated = _sessionCostData.Tasks.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                var changed = updated[tasks[5].Id];
                updated[tasks[5].Id] = changed with { SelfUsd = changed.SelfUsd + 1, SelfUpperUsd = changed.SelfUpperUsd + 1 };
                _sessionCostData = _sessionCostData with { ObservedAt = DateTimeOffset.UtcNow, Tasks = updated };
                UpdateSessionCosts(); UpdateLayout();
                results.Add(new { scenario = $"cost-refresh-preserves-identity-and-scroll-{panel}", passed =
                    ReferenceEquals(listForScroll.SelectedItem, selected) && Math.Abs(scroll.VerticalOffset - offset) < 1
                    && previousText != (panel == "tasks" ? _taskRows[5].CostText : _workloadRows[5].CostText) });
            }
            var beforeHidden = main.CostText;
            Hide();
            _sessionCostData = _sessionCostData with { Health = SampleHealth.Stale };
            UpdateSessionCosts();
            var skippedHiddenRendering = main.CostText == beforeHidden;
            RestoreFromTray();
            results.Add(new { scenario = "cost-hidden-defers-render-until-restore", passed = skippedHiddenRendering
                && _sessionCostEnabled && main.CostText.Contains("已过期") && IsVisible });
            UpdateSessionCosts();
            results.Add(new { scenario = "cost-stale-preserves-amount", passed = main.CostText.Contains("US$12.34")
                && main.CostText.Contains("已过期") });
            var retained = _sessionCostData;
            SessionCostCheck.IsChecked = false;
            results.Add(new { scenario = "cost-off-immediate-persisted-no-reset", passed = _sessionCostEnabled
                && !_store.Load().ShowSessionCost && _taskRows.All(r => r.CostVisibility == Visibility.Collapsed)
                && _workloadRows.All(r => r.CostVisibility == Visibility.Collapsed) && ReferenceEquals(_sessionCostData, retained) });
            SessionCostCheck.IsChecked = true;
            results.Add(new { scenario = "cost-on-immediate-persisted-retains-amount", passed = _sessionCostEnabled
                && _store.Load().ShowSessionCost && main.CostVisibility == Visibility.Visible && main.CostText.Contains("US$12.34") });
            OpenPanel("settings"); UpdateLayout();
            Capture(Path.Combine(directory, "cost-setting.png"));
        }
        finally
        {
            _sessionCostData = original;
            _activityData = originalActivity;
            _workloadData = originalWorkload;
            _settings.Scale = originalScale;
            SessionCostCheck.IsChecked = originalEnabled;
            _settings.ShowSessionCost = originalEnabled;
            _sessionCostFixtureActive = false;
            _workloadFixtureActive = false;
            ApplyAppearance(); UpdateActivity(); UpdateWorkload(); UpdateSessionCosts();
        }
    }
}