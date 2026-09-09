using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using CodexHud.Core;

namespace CodexHud.Mac;

/// <summary>Providers run off the UI thread. Published state and estimator rendering are serialized on it.</summary>
internal sealed class HudController : IAsyncDisposable
{
    private readonly IQuotaProvider _quota;
    private readonly IHardwareProvider _hardware;
    private readonly IActivityProvider _activity;
    private readonly DesktopActivityProvider _desktop = new();
    private readonly UsageLedgerProvider _ledger;
    private readonly QuotaTokenEstimator _estimator;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _usageGate = new(1, 1);
    private readonly object _lifecycle = new();
    private Task? _disposeTask;
    private Task[] _loops = [];
    private volatile bool _hidden, _appPresent;
    private int _forceQuota = 1;
    private string? _sampledEpoch;
    public QuotaSnapshot? Quota { get; private set; }
    public HardwareSnapshot? Hardware { get; private set; }
    public ActivitySnapshot? Activity { get; private set; }
    public UsageLedgerSnapshot? Usage { get; private set; }
    public event Action? Changed;

    public HudController(HudSettings settings, SettingsStore store)
    {
        var environmentHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var home = CodexLocator.ResolveHome(string.IsNullOrWhiteSpace(environmentHome) ? settings.CodexHome : environmentHome);
        settings.CodexHome = home;
        _quota = new QuotaProvider(string.IsNullOrWhiteSpace(settings.CodexExecutable) ? null : settings.CodexExecutable, home);
        _hardware = new MacHardwareProvider(() => _quota.OwnedProcessId);
        _activity = new ActivityProvider(home);
        // macOS volumes can be case sensitive: do not uppercase the directory identity.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home)))[..12];
        _estimator = new QuotaTokenEstimator(Path.Combine(store.DirectoryPath, $"token-estimates-{key}.json"));
        _ledger = new UsageLedgerProvider(home, _estimator.RestoredLedgerCheckpoint);
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_disposeTask is not null || _loops.Length != 0) return;
            _loops = [Task.Run(HardwareLoop), Task.Run(ActivityLoop), Task.Run(UsageLoop), Task.Run(QuotaLoop)];
        }
    }
    public void SetHidden(bool hidden) { _hidden = hidden; if (!hidden) RefreshQuota(); }
    public void RefreshQuota() => Interlocked.Exchange(ref _forceQuota, 1);
    private async Task Publish(Action action)
    {
        if (_stop.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => { if (!_stop.IsCancellationRequested) { action(); Changed?.Invoke(); } });
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
                    await Publish(() => _estimator.Invalidate("休眠或采样中断，等待重新配对"));
                    RefreshQuota();
                }
                lastRead = now;
                var data = await _hardware.ReadAsync(_stop.Token);
                _appPresent = data.CodexPresent;
                await Publish(() => Hardware = data);
                await Task.Delay(TimeSpan.FromSeconds(_hidden ? 5 : 1), _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch
            {
                _appPresent = false;
                await Publish(() =>
                {
                    if (Hardware is not { } previous) return;
                    static MetricSample Old(MetricSample m) => m with { Health = SampleHealth.Stale, Detail = "采集暂不可用" };
                    Hardware = previous with { Health = SampleHealth.Stale, CodexPresent = false, CodexProcessCount = 0,
                        SystemCpu = Old(previous.SystemCpu), CodexCpu = Old(previous.CodexCpu),
                        SystemMemoryUsed = Old(previous.SystemMemoryUsed), CodexMemory = Old(previous.CodexMemory),
                        SystemDiskReadBytesPerSecond = Old(previous.SystemDiskReadBytesPerSecond),
                        SystemDiskWriteBytesPerSecond = Old(previous.SystemDiskWriteBytesPerSecond) };
                });
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
                await Publish(() => Activity = data.Health == SampleHealth.Unavailable && Activity is { Tasks.Count: > 0 } old
                    ? old with { AppPresent = data.AppPresent, Health = SampleHealth.Stale, Message = data.Message } : data);
                await Task.Delay(5000, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch
            {
                await Publish(() => { if (Activity is { } old) Activity = old with { Health = SampleHealth.Stale, Message = "记录读取中断；时长截至证据。" }; });
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
            catch
            {
                await Publish(() =>
                {
                    _estimator.Invalidate("Token 采集暂不可用");
                    if (Usage is { } old) Usage = old with { Health = SampleHealth.Stale, Detail = "Token 采集暂不可用" };
                });
                if (!await Retry()) break;
            }
        }
    }

    private async Task QuotaLoop()
    {
        var lastAttempt = DateTimeOffset.MinValue;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (Interlocked.Exchange(ref _forceQuota, 0) == 1 || DateTimeOffset.UtcNow - lastAttempt >= TimeSpan.FromSeconds(_hidden ? 120 : 60))
                {
                    lastAttempt = DateTimeOffset.UtcNow;
                    await ReadUsageAsync();
                    var data = await _quota.ReadAsync(_stop.Token);
                    var usage = await ReadUsageAsync(captureCheckpoint: true);
                    await Publish(() => { Quota = data; _estimator.Observe(data, usage); _sampledEpoch = usage.Epoch; });
                }
                await Task.Delay(500, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch
            {
                await Publish(() =>
                {
                    _estimator.Invalidate("额度采样中断");
                    if (Quota is { } old) Quota = old with { Health = SampleHealth.Stale, Message = "额度采集暂不可用" };
                    if (Usage is { } usage) Usage = usage with { Health = SampleHealth.Stale, Detail = "同步采样中断，等待重新读取" };
                });
                if (!await Retry()) break;
            }
        }
    }

    private async Task<UsageLedgerSnapshot> ReadUsageAsync(bool captureCheckpoint = false)
    {
        // Keep each ledger read and publication together. The periodic reader must not publish
        // an older snapshot after a quota-pairing read has already published a newer epoch.
        await _usageGate.WaitAsync(_stop.Token);
        try
        {
            var usage = captureCheckpoint
                ? await _ledger.ReadCheckpointAsync(_stop.Token)
                : await _ledger.ReadAsync(_stop.Token);
            await Publish(() => Usage = usage);
            return usage;
        }
        finally { _usageGate.Release(); }
    }

    public QuotaTokenEstimate Estimate(QuotaWindow? selected)
    {
        var estimate = _estimator.Get(selected, DateTimeOffset.UtcNow);
        if (estimate.State is EstimateState.Unavailable or EstimateState.Stale) return estimate;
        if (Usage is { Health: not SampleHealth.Fresh } usage)
            return estimate with { State = EstimateState.Incomplete, RemainingTokens = null, LowerTokens = null, UpperTokens = null,
                RecoverySamples = 0, RecoveryRequired = estimate.Segments > 0 ? 2 : estimate.RecoveryRequired,
                ProgressReason = usage.Detail ?? "本机 Token 采样暂停，等待重新对齐", Detail = usage.Detail ?? "Token 数据不完整" };
        if (_sampledEpoch is not null && Usage?.Epoch != _sampledEpoch)
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
        // An observer/UI callback can fault a loop. Such a fault must never leave the owned
        // quota app-server alive or skip native-handle disposal during application shutdown.
        try { await Task.WhenAll(_loops).ConfigureAwait(false); }
        catch { }
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
                    finally { _usageGate.Dispose(); _stop.Dispose(); }
                }
            }
        }
    }
}
