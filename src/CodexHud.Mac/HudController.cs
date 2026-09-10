using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using CodexHud.Core;

namespace CodexHud.Mac;

/// <summary>Independent providers and period aggregation run off the UI thread; snapshots are immutable.</summary>
internal sealed class HudController : IAsyncDisposable
{
    private readonly IQuotaProvider _quota;
    private readonly IHardwareProvider _hardware;
    private readonly IActivityProvider _activity;
    private readonly DesktopActivityProvider _desktop = new();
    private readonly UsageLedgerProvider _ledger;
    private readonly WorkloadUsageProvider _workload;
    private readonly SessionCostProvider _sessionCosts;
    private readonly BillingCycleTracker _billingCycles;
    private readonly QuotaTokenEstimator _estimator;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _usageGate = new(1, 1), _billingWake = new(0, 1);
    private readonly object _lifecycle = new();
    private Task? _disposeTask;
    private Task[] _loops = [];
    private volatile bool _hidden, _appPresent;
    private int _forceQuota = 1;
    private string? _sampledEpoch, _billingWindowKey, _billingPeriodId;
    private QuotaSnapshot? _quotaData;
    private HardwareSnapshot? _hardwareData;
    private ActivitySnapshot? _activityData;
    private UsageLedgerSnapshot? _usageData;
    private WorkloadUsageSnapshot? _workloadData;
    private SessionCostSnapshot? _sessionCostData;
    private BillingState _billingData = new([], null, null);
    private long _billingRevision;
    private sealed record BillingState(IReadOnlyList<UsagePeriod> Periods, PeriodUsageSnapshot? Current, PeriodUsageSnapshot? Report);
    public QuotaSnapshot? Quota => Volatile.Read(ref _quotaData);
    public HardwareSnapshot? Hardware => Volatile.Read(ref _hardwareData);
    public ActivitySnapshot? Activity => Volatile.Read(ref _activityData);
    public UsageLedgerSnapshot? Usage => Volatile.Read(ref _usageData);
    public WorkloadUsageSnapshot? Workload => Volatile.Read(ref _workloadData);
    public SessionCostSnapshot? SessionCosts => Volatile.Read(ref _sessionCostData);
    public IReadOnlyList<UsagePeriod> BillingPeriods => Volatile.Read(ref _billingData).Periods;
    public PeriodUsageSnapshot? BillingCurrent => Volatile.Read(ref _billingData).Current;
    public PeriodUsageSnapshot? BillingReport => Volatile.Read(ref _billingData).Report;
    public string? BillingDetail => _billingCycles.Detail;
    public string? SelectedBillingPeriodId => Volatile.Read(ref _billingPeriodId)
        ?? BillingPeriods.FirstOrDefault(p => p.IsCurrent)?.Id;
    public event Action? Changed;

    public HudController(HudSettings settings, SettingsStore store)
    {
        var environmentHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var home = CodexLocator.ResolveHome(string.IsNullOrWhiteSpace(environmentHome) ? settings.CodexHome : environmentHome);
        settings.CodexHome = home;
        _quota = new QuotaProvider(string.IsNullOrWhiteSpace(settings.CodexExecutable) ? null : settings.CodexExecutable, home);
        _hardware = new MacHardwareProvider(() => _quota.OwnedProcessId);
        _activity = new ActivityProvider(home);
        // macOS volumes can be case sensitive: preserve the directory identity.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home)))[..12];
        _estimator = new QuotaTokenEstimator(Path.Combine(store.DirectoryPath, $"token-estimates-{key}.json"));
        _ledger = new UsageLedgerProvider(home, _estimator.RestoredLedgerCheckpoint);
        _workload = new WorkloadUsageProvider(home);
        _sessionCosts = new SessionCostProvider(home, Path.Combine(store.DirectoryPath, $"session-costs-{key}.json"));
        _billingCycles = new BillingCycleTracker(Path.Combine(store.DirectoryPath, $"billing-cycles-{key}.json"));
        _billingWindowKey = settings.SelectedQuotaKey;
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_disposeTask is not null || _loops.Length != 0) return;
            _loops = [Task.Run(HardwareLoop), Task.Run(ActivityLoop), Task.Run(UsageLoop), Task.Run(QuotaLoop),
                Task.Run(WorkloadLoop), Task.Run(SessionCostLoop), Task.Run(BillingLoop)];
        }
    }
    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        if (!hidden) { RefreshQuota(); SignalBillingRefresh(); }
    }
    public void RefreshQuota() => Interlocked.Exchange(ref _forceQuota, 1);
    private async Task NotifyAsync(bool whileHidden = false)
    {
        // Fast samplers never enqueue hidden callbacks. Activity notices and an open report may refresh at 5 seconds.
        if (_hidden && !whileHidden || _stop.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if ((!_hidden || whileHidden) && !_stop.IsCancellationRequested) Changed?.Invoke();
        });
    }

    private async Task HardwareLoop()
    {
        var lastRead = DateTimeOffset.UtcNow;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now < lastRead || now - lastRead > TimeSpan.FromSeconds(30))
                {
                    _hardware.ResetBaseline();
                    _estimator.Invalidate("休眠或采样中断，等待重新配对");
                    RefreshQuota();
                }
                lastRead = now;
                var data = await _hardware.ReadAsync(_stop.Token);
                _appPresent = data.CodexPresent;
                Volatile.Write(ref _hardwareData, data);
                await NotifyAsync();
                await Task.Delay(TimeSpan.FromSeconds(_hidden ? 5 : 1), _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch
            {
                _appPresent = false;
                if (Hardware is { } previous)
                {
                    static MetricSample Old(MetricSample m) => m with { Health = SampleHealth.Stale, Detail = "采集暂不可用" };
                    Volatile.Write(ref _hardwareData, previous with { Health = SampleHealth.Stale, CodexPresent = false, CodexProcessCount = 0,
                        SystemCpu = Old(previous.SystemCpu), CodexCpu = Old(previous.CodexCpu),
                        SystemMemoryUsed = Old(previous.SystemMemoryUsed), CodexMemory = Old(previous.CodexMemory),
                        SystemDiskReadBytesPerSecond = Old(previous.SystemDiskReadBytesPerSecond),
                        SystemDiskWriteBytesPerSecond = Old(previous.SystemDiskWriteBytesPerSecond) });
                }
                await NotifyAsync();
                if (!await Retry()) break;
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
                var data = await _desktop.EnrichAsync(local, _stop.Token);
                Volatile.Write(ref _activityData, data.Health == SampleHealth.Unavailable && Activity is { Tasks.Count: > 0 } old
                    ? old with { AppPresent = data.AppPresent, Health = SampleHealth.Stale, Message = data.Message } : data);
                SignalBillingRefresh();
                await NotifyAsync(whileHidden: true);
                await Task.Delay(5000, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch
            {
                if (Activity is { } old) Volatile.Write(ref _activityData, old with { Health = SampleHealth.Stale, Message = "记录读取中断；时长截至证据。" });
                await NotifyAsync(whileHidden: true);
                if (!await Retry()) break;
            }
        }
    }

    private async Task UsageLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await ReadUsageAsync();
                await Task.Delay(TimeSpan.FromSeconds(_hidden ? 15 : 5), _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch { InvalidateUsage(); await NotifyAsync(); if (!await Retry()) break; }
        }
    }

    private void InvalidateUsage()
    {
        _estimator.Invalidate("Token 采集暂不可用");
        if (Usage is { } old) Volatile.Write(ref _usageData, old with { Health = SampleHealth.Stale, Detail = "Token 采集暂不可用" });
    }

    private async Task QuotaLoop()
    {
        var lastAttempt = DateTimeOffset.MinValue;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (Interlocked.Exchange(ref _forceQuota, 0) == 1
                    || _billingCycles.RecheckAt is { } recheck && now >= recheck
                    || now - lastAttempt >= TimeSpan.FromSeconds(_hidden ? 120 : 60))
                {
                    lastAttempt = now;
                    try { await ReadUsageAsync(); }
                    catch (Exception) when (!_stop.IsCancellationRequested) { InvalidateUsage(); }
                    var data = await _quota.ReadAsync(_stop.Token);
                    Volatile.Write(ref _quotaData, data);
                    // Billing boundaries use the complete real quota observation, including ResetCredits,
                    // independently of calibration health or token-history availability.
                    _billingCycles.Observe(data);
                    SignalBillingRefresh();
                    try
                    {
                        var usage = await ReadUsageAsync(captureCheckpoint: true);
                        _estimator.Observe(data, usage);
                        Volatile.Write(ref _sampledEpoch, usage.Epoch);
                    }
                    catch (Exception) when (!_stop.IsCancellationRequested) { InvalidateUsage(); }
                    await NotifyAsync();
                }
                await Task.Delay(500, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch
            {
                _estimator.Invalidate("额度采样中断");
                if (Quota is { } old)
                {
                    var stale = old with { Health = SampleHealth.Stale, Message = "额度采集暂不可用" };
                    Volatile.Write(ref _quotaData, stale);
                    _billingCycles.Observe(stale);
                }
                SignalBillingRefresh();
                await NotifyAsync();
                if (!await Retry()) break;
            }
        }
    }

    private async Task<UsageLedgerSnapshot> ReadUsageAsync(bool captureCheckpoint = false)
    {
        // The ledger reader and quota reader share a single cursor but never the workload cursor.
        await _usageGate.WaitAsync(_stop.Token);
        try
        {
            var usage = captureCheckpoint
                ? await _ledger.ReadCheckpointAsync(_stop.Token)
                : await _ledger.ReadAsync(_stop.Token);
            Volatile.Write(ref _usageData, usage);
            await NotifyAsync();
            return usage;
        }
        finally { _usageGate.Release(); }
    }

    private async Task WorkloadLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                Volatile.Write(ref _workloadData, await _workload.ReadAsync(_stop.Token));
                await NotifyAsync();
                if (!await timer.WaitForNextTickAsync(_stop.Token)) break;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch
            {
                var previous = Workload;
                Volatile.Write(ref _workloadData, previous is null
                    ? new WorkloadUsageSnapshot(DateTimeOffset.UtcNow, [], [], SampleHealth.Unavailable, "Token 速率暂不可用")
                    : previous with { Health = SampleHealth.Stale, Detail = "采集暂停，保留此前速率；缺失秒不当作零。" });
                await NotifyAsync();
                if (!await Retry()) break;
            }
        }
    }

    private async Task SessionCostLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var activityIds = Activity?.Tasks.Select(t => t.Id) ?? [];
                var workloadIds = Workload?.Tasks.Where(t => t.History.Any(tick => tick.Counts?.TotalTokens > 0))
                    .Select(t => t.ThreadId) ?? [];
                _sessionCosts.SetTrackedThreads(activityIds.Concat(workloadIds).Distinct(StringComparer.Ordinal));
                Volatile.Write(ref _sessionCostData, await _sessionCosts.ReadAsync(_stop.Token));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch
            {
                var previous = SessionCosts;
                Volatile.Write(ref _sessionCostData, previous is null
                    ? new SessionCostSnapshot(DateTimeOffset.UtcNow, new Dictionary<string, SessionCostEstimate>(), SampleHealth.Unavailable, "费用采集暂不可用。")
                    : previous with { Health = SampleHealth.Stale, Detail = "费用采集暂不可用，保留上次累计估算。" });
            }
            SignalBillingRefresh();
            await NotifyAsync();
            try { await Task.Delay(5000, _stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
        }
    }

    public void SelectQuota(string? key)
    {
        if (Volatile.Read(ref _billingWindowKey) == key) return;
        Volatile.Write(ref _billingWindowKey, key);
        Volatile.Write(ref _billingPeriodId, null);
        Interlocked.Increment(ref _billingRevision);
        Volatile.Write(ref _billingData, new BillingState([], null, null));
        SignalBillingRefresh();
    }
    public void SelectBillingPeriod(string id)
    {
        var period = _billingCycles.GetPeriods(Volatile.Read(ref _billingWindowKey)).FirstOrDefault(p => p.Id == id);
        if (period is null) return;
        Volatile.Write(ref _billingPeriodId, period.IsCurrent ? null : id);
        Interlocked.Increment(ref _billingRevision);
        SignalBillingRefresh();
    }
    public bool AddManualReset(DateTimeOffset at, out string? error)
    {
        if (Volatile.Read(ref _billingWindowKey) is not { } key)
        { error = "请先选择有效额度周期。"; return false; }
        if (!_billingCycles.AddManualReset(key, at, out error)) return false;
        Volatile.Write(ref _billingPeriodId, null);
        Interlocked.Increment(ref _billingRevision);
        SignalBillingRefresh(); RefreshQuota(); return true;
    }
    public bool CorrectBillingStart(string id, DateTimeOffset at, out string? error)
    {
        var key = Volatile.Read(ref _billingWindowKey);
        if (key is null || !_billingCycles.GetPeriods(key).Any(period => period.Id == id))
        { error = "所选额度周期已改变，请重新打开起点编辑。"; return false; }
        if (!_billingCycles.CorrectStart(id, at, out error)) return false;
        Interlocked.Increment(ref _billingRevision);
        SignalBillingRefresh(); RefreshQuota(); return true;
    }
    private void SignalBillingRefresh()
    {
        if (_stop.IsCancellationRequested) return;
        try { _billingWake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task BillingLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var revision = Interlocked.Read(ref _billingRevision);
                var key = Volatile.Read(ref _billingWindowKey);
                IReadOnlyList<UsagePeriod> periods = key is null ? [] : _billingCycles.GetPeriods(key);
                var current = periods.FirstOrDefault(p => p.IsCurrent) ?? periods.FirstOrDefault();
                var selected = periods.FirstOrDefault(p => p.Id == Volatile.Read(ref _billingPeriodId)) ?? current;
                var cost = SessionCosts;
                var ledger = cost?.Ledger;
                var now = DateTimeOffset.UtcNow;
                if (ledger != null && (cost!.Health is SampleHealth.Stale or SampleHealth.Unavailable
                    || now - ledger.ObservedAt > TimeSpan.FromSeconds(20)))
                    ledger = ledger with { Health = SampleHealth.Stale, Detail = "采集已过期，保留此前可验证记录。" };
                var activity = Activity;
                var currentData = ledger != null && current != null ? PeriodUsageCalculator.Build(ledger, current, now, activity) : null;
                var reportData = selected?.Id == current?.Id ? currentData
                    : ledger != null && selected != null ? PeriodUsageCalculator.Build(ledger, selected, now, activity) : null;
                var quota = Quota;
                currentData = ApplyBillingState(currentData, quota, now);
                reportData = ApplyBillingState(reportData, quota, now);
                if (revision == Interlocked.Read(ref _billingRevision))
                {
                    Volatile.Write(ref _billingData, new BillingState(periods, currentData, reportData));
                    await NotifyAsync(whileHidden: true);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch
            {
                var previous = Volatile.Read(ref _billingData);
                static PeriodUsageSnapshot? Stale(PeriodUsageSnapshot? value) => value is null ? null
                    : value with { Health = SampleHealth.Stale, Detail = "周期汇总暂不可用，保留上次结果。" };
                Volatile.Write(ref _billingData, previous with { Current = Stale(previous.Current), Report = Stale(previous.Report) });
                await NotifyAsync(whileHidden: true);
            }
            try { await _billingWake.WaitAsync(TimeSpan.FromSeconds(5), _stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
        }
    }
    private static PeriodUsageSnapshot? ApplyBillingState(PeriodUsageSnapshot? snapshot, QuotaSnapshot? quota, DateTimeOffset now)
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

    public QuotaTokenEstimate Estimate(QuotaWindow? selected, string? selectedKey = null)
    {
        var estimate = selected is null ? _estimator.GetSavedHistory(selectedKey, DateTimeOffset.UtcNow)
            : _estimator.Get(selected, DateTimeOffset.UtcNow);
        if (selected is null || estimate.State is EstimateState.Unavailable or EstimateState.Stale) return estimate;
        if (Usage is { Health: not SampleHealth.Fresh } usage)
            return estimate with { State = EstimateState.Incomplete, RemainingTokens = null, LowerTokens = null, UpperTokens = null,
                RecoverySamples = 0, RecoveryRequired = estimate.Segments > 0 ? 2 : estimate.RecoveryRequired,
                ProgressReason = usage.Detail ?? "本机 Token 采样暂停，等待重新对齐", Detail = usage.Detail ?? "Token 数据不完整" };
        if (Volatile.Read(ref _sampledEpoch) is { } epoch && Usage?.Epoch != epoch)
            return estimate with { State = EstimateState.Calibrating, RemainingTokens = null, LowerTokens = null, UpperTokens = null,
                RecoverySamples = 0, RecoveryRequired = estimate.Segments > 0 ? 2 : estimate.RecoveryRequired,
                ProgressReason = "Token 采样已重建，等待连续健康配对",
                Detail = "Token 采样已重新建立；保留累计进度，等待两次时间不同的健康配对" };
        return estimate;
    }

    private async Task<bool> Retry()
    {
        try { await Task.Delay(5000, _stop.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        _stop.Cancel();
        try { await Task.WhenAll(_loops).ConfigureAwait(false); } catch { }
        try { await _desktop.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { await _quota.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { _hardware.Dispose(); }
                finally
                {
                    try { _activity.Dispose(); }
                    finally
                    {
                        try { _sessionCosts.Dispose(); }
                        finally { _usageGate.Dispose(); _billingWake.Dispose(); _stop.Dispose(); }
                    }
                }
            }
        }
    }
}