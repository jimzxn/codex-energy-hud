using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexHud.Core.Native;
using Microsoft.Win32.SafeHandles;
using static CodexHud.Core.Native.WindowsHardwareNative;

namespace CodexHud.Core;

/// <summary>Read-only, process-local Windows hardware sampling. Every value retains its own freshness.</summary>
public sealed class HardwareProvider : IHardwareProvider
{
    private readonly object _gate = new();
    private readonly Func<int?>? _excludedQuotaPid;
    private readonly Dictionary<ProcessIdentity, ProcessState> _processes = [];
    private HardwareSnapshot? _last;
    private SystemTimes? _cpuBaseline;
    private DateTimeOffset? _processSampleAt;
    private long? _lastReadTimestamp;
    private PdhGpuSampler? _gpuSampler;
    private readonly Func<IDiskSampler> _diskSamplerFactory;
    private IDiskSampler? _diskSampler;
    private DateTimeOffset _diskRetryAt;
    private GpuAdapter? _adapter;
    private DateTimeOffset _gpuRetryAt;
    private DateTimeOffset _adapterRefreshAt;
    private bool _disposed;

    public HardwareProvider(Func<int?>? excludedQuotaPid = null) : this(excludedQuotaPid, () => new PdhDiskSampler()) { }

    internal HardwareProvider(Func<int?>? excludedQuotaPid, Func<IDiskSampler> diskSamplerFactory)
    {
        _excludedQuotaPid = excludedQuotaPid;
        _diskSamplerFactory = diskSamplerFactory;
    }

    public Task<HardwareSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return ReadCore();
        }
    }, cancellationToken);

    public void ResetBaseline()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ResetBaselineCore();
        }
    }

    private void ResetBaselineCore()
    {
        _cpuBaseline = null;
        _processSampleAt = null;
        _lastReadTimestamp = null;
        foreach (var state in _processes.Values) state.CpuTime = null;
        _gpuSampler?.Dispose();
        _gpuSampler = null;
        _gpuRetryAt = default;
        _diskSampler?.Dispose();
        _diskSampler = null;
        _diskRetryAt = default;
    }

    private HardwareSnapshot ReadCore()
    {
        var now = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsWindows()) return MissingSnapshot(now, "硬件采集需要 Windows");
        var timestamp = Stopwatch.GetTimestamp();
        if (_lastReadTimestamp.HasValue && Stopwatch.GetElapsedTime(_lastReadTimestamp.Value, timestamp) > TimeSpan.FromSeconds(15))
            ResetBaselineCore(); // a paused timer or resume must not be interpreted as a current 1-second sample
        _lastReadTimestamp = timestamp;

        long? totalCpuDelta = null;
        MetricSample systemCpu;
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw LastError("CPU");
            var current = new SystemTimes(idle.Ticks, checked(kernel.Ticks + user.Ticks));
            if (_cpuBaseline is { } previous && current.Total > previous.Total && current.Idle >= previous.Idle)
            {
                totalCpuDelta = current.Total - previous.Total;
                systemCpu = Fresh(HardwareAlgorithms.CpuPercent(totalCpuDelta.Value - (current.Idle - previous.Idle), totalCpuDelta.Value), now,
                    "整机 CPU 时间占比");
            }
            else systemCpu = Failure(_last?.SystemCpu, "等待第二次 CPU 采样", true);
            _cpuBaseline = current;
        }
        catch (Exception ex)
        {
            _cpuBaseline = null;
            systemCpu = Failure(_last?.SystemCpu, ShortError(ex));
        }

        MetricSample systemMemoryUsed, systemMemoryTotal;
        try
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory)) throw LastError("系统内存");
            systemMemoryUsed = Fresh(memory.TotalPhysical - memory.AvailablePhysical, now, "系统已用物理内存");
            systemMemoryTotal = Fresh(memory.TotalPhysical, now, "物理内存总量");
        }
        catch (Exception ex)
        {
            systemMemoryUsed = Failure(_last?.SystemMemoryUsed, ShortError(ex));
            systemMemoryTotal = Failure(_last?.SystemMemoryTotal, ShortError(ex));
        }

        ProcessGroup? group = null;
        MetricSample codexCpu, codexMemory;
        try
        {
            group = ReadProcessGroup(now);
            var detail = group.Partial ? $"已采样 {group.Count} 个 Codex 进程；部分进程不可读" : $"Codex 进程组，{group.Count} 个进程";
            var health = group.Partial ? SampleHealth.Partial : SampleHealth.Fresh;
            if (group.Count == 0 && !group.Partial)
                codexCpu = Fresh(0, now, "Codex 未运行");
            else if (totalCpuDelta.HasValue && group.CpuDelta.HasValue)
                codexCpu = new MetricSample(HardwareAlgorithms.CpuPercent(group.CpuDelta.Value, totalCpuDelta.Value), now,
                    group.CpuPartial || group.Partial ? SampleHealth.Partial : SampleHealth.Fresh, detail + "；按整机 CPU 容量归一化");
            else codexCpu = Failure(_last?.CodexCpu, "等待 Codex 进程 CPU 基线", true);

            codexMemory = group.MemoryBytes.HasValue
                ? new MetricSample(group.MemoryBytes.Value, now,
                    group.MemoryPartial || group.Partial ? SampleHealth.Partial : health, detail + "；私有工作集")
                : Failure(_last?.CodexMemory, "Codex 私有工作集不可读");
        }
        catch (Exception ex)
        {
            foreach (var state in _processes.Values) state.CpuTime = null;
            _processSampleAt = null;
            codexCpu = Failure(_last?.CodexCpu, ShortError(ex));
            codexMemory = Failure(_last?.CodexMemory, ShortError(ex));
        }

        var gpu = ReadGpu(now, group);
        var disk = ReadDisk(now);
        var metrics = new[] { systemCpu, codexCpu, gpu.System, gpu.Codex, systemMemoryUsed, systemMemoryTotal,
            codexMemory, gpu.MemoryUsed, gpu.MemoryTotal, disk.Read, disk.Write };
        var overall = metrics.Any(metric => metric.Health is SampleHealth.Partial or SampleHealth.Stale or SampleHealth.Unavailable)
            ? SampleHealth.Partial : metrics.Any(metric => metric.Health == SampleHealth.Loading) ? SampleHealth.Loading : SampleHealth.Fresh;
        var messages = metrics.Where(metric => metric.Health is SampleHealth.Partial or SampleHealth.Stale or SampleHealth.Unavailable)
            .Select(metric => metric.Detail).Where(message => !string.IsNullOrEmpty(message)).Distinct().Take(3);
        _last = new HardwareSnapshot(now, systemCpu, codexCpu, gpu.System, gpu.Codex, systemMemoryUsed, systemMemoryTotal,
            codexMemory, gpu.MemoryUsed, gpu.MemoryTotal, _adapter?.Name ?? _last?.GpuName ?? "GPU 不可用",
            group?.Present ?? _last?.CodexPresent ?? false, group?.Count ?? _last?.CodexProcessCount ?? 0,
            overall, string.Join("；", messages))
        {
            SystemDiskReadBytesPerSecond = disk.Read,
            SystemDiskWriteBytesPerSecond = disk.Write,
        };
        return _last;
    }

    private ProcessGroup ReadProcessGroup(DateTimeOffset now)
    {
        var entries = EnumerateProcesses().Where(entry => entry.Pid is > 0 and <= int.MaxValue)
            .ToDictionary(entry => (int)entry.Pid);
        var parents = entries.ToDictionary(pair => pair.Key, pair => unchecked((int)pair.Value.ParentPid));
        var exclusions = new List<int> { Environment.ProcessId };
        if (_excludedQuotaPid?.Invoke() is > 0 and var quotaPid) exclusions.Add(quotaPid);
        var excluded = HardwareAlgorithms.ExcludedTree(parents, exclusions);
        var observations = new Dictionary<int, ProcessObservation?>();
        var group = new Dictionary<int, ProcessObservation>();
        var failed = new HashSet<int>();
        var candidates = new Queue<ProcessObservation>();
        var children = entries.Values.GroupBy(entry => (int)entry.ParentPid)
            .ToDictionary(items => items.Key, items => items.Select(entry => (int)entry.Pid).ToArray());

        ProcessObservation? Observe(int pid)
        {
            if (observations.TryGetValue(pid, out var existing)) return existing;
            var handle = OpenReadableProcess(pid);
            if (handle is null) { observations[pid] = null; return null; }
            if (!GetProcessTimes(handle, out var creation, out _, out var kernel, out var user))
            {
                handle.Dispose(); observations[pid] = null; return null;
            }
            var observation = new ProcessObservation(handle, new ProcessIdentity(pid, creation.Ticks), checked(kernel.Ticks + user.Ticks));
            observations[pid] = observation;
            return observation;
        }

        void Enqueue(ProcessObservation process)
        {
            if (!excluded.Contains(process.Identity.Pid) && group.TryAdd(process.Identity.Pid, process)) candidates.Enqueue(process);
        }

        try
        {
            // Retain identity-verified descendants if their original parent terminated between samples.
            foreach (var identity in _processes.Keys)
            {
                if (excluded.Contains(identity.Pid) || !entries.ContainsKey(identity.Pid)) continue;
                var observed = Observe(identity.Pid);
                if (observed?.Identity == identity) Enqueue(observed);
                else if (observed is null) failed.Add(identity.Pid);
            }
            foreach (var entry in entries.Values)
            {
                var pid = (int)entry.Pid;
                if (excluded.Contains(pid) || (!entry.ExeFile.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                    && !entry.ExeFile.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))) continue;
                var process = Observe(pid);
                if (process is null) continue; // unrelated applications may also use these names
                if (HardwareAlgorithms.IsApplicationImage(ReadImagePath(process.Handle!))) Enqueue(process);
            }
            while (candidates.TryDequeue(out var parent))
            {
                if (!children.TryGetValue(parent.Identity.Pid, out var descendants)) continue;
                foreach (var pid in descendants)
                {
                    if (excluded.Contains(pid) || group.ContainsKey(pid)) continue;
                    var child = Observe(pid);
                    if (child is null) { failed.Add(pid); continue; }
                    // A reused parent PID must not attach a process that predates the actual parent.
                    if (child.Identity.CreationTime >= parent.Identity.CreationTime) Enqueue(child);
                }
            }

            long cpuDelta = 0;
            var cpuMeasured = 0;
            var cpuPartial = false;
            double memoryBytes = 0;
            var memoryMeasured = 0;
            var memoryPartial = false;
            foreach (var process in group.Values)
            {
                if (_processes.TryGetValue(process.Identity, out var previous) && previous.CpuTime.HasValue)
                {
                    var delta = process.CpuTime - previous.CpuTime.Value;
                    if (delta >= 0) { cpuDelta = checked(cpuDelta + delta); cpuMeasured++; }
                    else cpuPartial = true;
                }
                else if (_processSampleAt.HasValue && process.Identity.CreationTime >= _processSampleAt.Value.UtcDateTime.ToFileTimeUtc())
                {
                    cpuDelta = checked(cpuDelta + process.CpuTime);
                    cpuMeasured++;
                }
                else cpuPartial = true;

                var memory = new ProcessMemoryCountersEx2 { Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
                if (GetProcessMemoryInfo(process.Handle!, ref memory, memory.Size))
                {
                    memoryBytes += memory.PrivateWorkingSet.ToUInt64();
                    memoryMeasured++;
                }
                else memoryPartial = true;
            }

            // Handles survive one sampling interval so a known short-lived process can supply its final CPU time.
            foreach (var (identity, previous) in _processes)
            {
                if (excluded.Contains(identity.Pid) || group.Values.Any(process => process.Identity == identity) || !previous.CpuTime.HasValue) continue;
                if (GetProcessTimes(previous.Handle, out _, out var exit, out var kernel, out var user) && exit.Ticks != 0)
                {
                    var delta = checked(kernel.Ticks + user.Ticks) - previous.CpuTime.Value;
                    if (delta >= 0) { cpuDelta = checked(cpuDelta + delta); cpuMeasured++; }
                }
            }

            foreach (var previous in _processes.Values) previous.Dispose();
            _processes.Clear();
            foreach (var process in group.Values)
                _processes.Add(process.Identity, new ProcessState(process.TakeHandle(), process.CpuTime));
            _processSampleAt = now;
            failed.ExceptWith(group.Keys);
            var noProcesses = group.Count == 0 && failed.Count == 0;
            return new ProcessGroup(group.Keys.ToHashSet(), group.Count, group.Count > 0 || failed.Count > 0,
                failed.Count > 0, cpuMeasured > 0 || noProcesses ? cpuDelta : null, cpuPartial,
                memoryMeasured > 0 || noProcesses ? memoryBytes : null, memoryPartial);
        }
        finally { foreach (var process in observations.Values) process?.Dispose(); }
    }

    private GpuGroup ReadGpu(DateTimeOffset now, ProcessGroup? processes)
    {
        var system = Failure(_last?.SystemGpu, "GPU 采样不可用");
        var codex = Failure(_last?.CodexGpu, "Codex GPU 采样不可用");
        var memory = Failure(_last?.GpuMemoryUsed, "显存采样不可用");
        var total = Failure(_last?.GpuMemoryTotal, "显存容量不可用");
        try
        {
            if (now >= _adapterRefreshAt)
            {
                var adapter = DxgiAdapterCatalog.FindPreferred();
                if (adapter is not null)
                {
                    if (_adapter?.LuidToken != adapter.LuidToken)
                    {
                        _gpuSampler?.Dispose();
                        _gpuSampler = null;
                        _gpuRetryAt = default;
                    }
                    _adapter = adapter;
                }
                _adapterRefreshAt = now.AddSeconds(adapter is null ? 15 : 60);
                if (adapter is null) throw new InvalidOperationException("没有可读取的硬件 GPU 适配器");
            }
            if (_adapter is null) return new GpuGroup(system, codex, memory, total);
            total = Fresh(_adapter.DedicatedMemory, now, "DXGI 专用显存容量");
            if (_gpuSampler is null && now >= _gpuRetryAt) _gpuSampler = new PdhGpuSampler();
            if (_gpuSampler is null) return new GpuGroup(system, codex, memory, total);
            var sample = _gpuSampler.Read();
            if (sample.WarmingUp)
            {
                system = Failure(_last?.SystemGpu, "等待第二次 GPU 采样", true);
                codex = Failure(_last?.CodexGpu, "等待第二次 GPU 采样", true);
            }
            else if (sample.Engines.Values is { } engines)
            {
                var values = new List<(GpuEngineIdentity, double)>(engines.Count);
                foreach (var engine in engines)
                    if (HardwareAlgorithms.TryParseGpuInstance(engine.Instance, out var identity)) values.Add((identity, engine.Value));
                var aggregate = HardwareAlgorithms.AggregateGpu(values, _adapter.LuidToken, processes?.Pids ?? []);
                if (aggregate.Matched > 0)
                {
                    var health = sample.Engines.Partial ? SampleHealth.Partial : SampleHealth.Fresh;
                    system = new MetricSample(aggregate.System, now, health, "PDH：该显卡最忙引擎占用");
                    codex = processes is null
                        ? Failure(_last?.CodexGpu, "进程组暂不可读，无法归属 GPU 活动")
                        : new MetricSample(aggregate.Codex, now, processes.Partial ? SampleHealth.Partial : health,
                            "PDH：Codex 进程组在该显卡最忙引擎的占用" + (processes.Partial ? "；部分进程不可读" : ""));
                }
                else
                {
                    system = Failure(_last?.SystemGpu, "所选显卡尚无可用引擎样本");
                    codex = Failure(_last?.CodexGpu, "所选显卡尚无可用引擎样本");
                }
            }
            else
            {
                system = Failure(_last?.SystemGpu, sample.Engines.Error ?? "GPU 样本不可用");
                codex = Failure(_last?.CodexGpu, sample.Engines.Error ?? "GPU 样本不可用");
            }
            if (sample.Memory.Values is { } memoryValues)
            {
                var matching = memoryValues.Where(item => item.Instance.StartsWith(_adapter.LuidToken + "_", StringComparison.OrdinalIgnoreCase)
                    || item.Instance.Equals(_adapter.LuidToken, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matching.Length > 0)
                    memory = new MetricSample(matching.Sum(item => item.Value), now,
                        sample.Memory.Partial ? SampleHealth.Partial : SampleHealth.Fresh, "PDH：所选显卡专用显存已用");
                else memory = Failure(_last?.GpuMemoryUsed, "所选显卡的显存样本暂不可用");
            }
            else memory = Failure(_last?.GpuMemoryUsed, sample.Memory.Error ?? "显存样本不可用");
        }
        catch (Exception ex)
        {
            _gpuSampler?.Dispose();
            _gpuSampler = null;
            _gpuRetryAt = now.AddSeconds(15);
            system = Failure(_last?.SystemGpu, ShortError(ex));
            codex = Failure(_last?.CodexGpu, ShortError(ex));
            memory = Failure(_last?.GpuMemoryUsed, ShortError(ex));
        }
        return new GpuGroup(system, codex, memory, total);
    }

    private (MetricSample Read, MetricSample Write) ReadDisk(DateTimeOffset now)
    {
        var read = Failure(_last?.SystemDiskReadBytesPerSecond, "整机磁盘读取采样暂不可用；等待重试");
        var write = Failure(_last?.SystemDiskWriteBytesPerSecond, "整机磁盘写入采样暂不可用；等待重试");
        try
        {
            if (_diskSampler is null && now >= _diskRetryAt) _diskSampler = _diskSamplerFactory();
            if (_diskSampler is null) return (read, write);
            var sample = _diskSampler.Read();
            if (sample.WarmingUp)
                return (Failure(_last?.SystemDiskReadBytesPerSecond, "等待第二次磁盘采样", true),
                    Failure(_last?.SystemDiskWriteBytesPerSecond, "等待第二次磁盘采样", true));

            read = sample.Read.BytesPerSecond is { } readRate
                ? Fresh(readRate, now, "PDH：整机物理磁盘读取字节/秒（所有物理磁盘合计）")
                : Failure(_last?.SystemDiskReadBytesPerSecond, sample.Read.Error ?? "磁盘读取采样不可用");
            write = sample.Write.BytesPerSecond is { } writeRate
                ? Fresh(writeRate, now, "PDH：整机物理磁盘写入字节/秒（所有物理磁盘合计）")
                : Failure(_last?.SystemDiskWriteBytesPerSecond, sample.Write.Error ?? "磁盘写入采样不可用");

            // A counter can disappear independently of its query (for example after a device change).
            // Keep the other valid direction, then reopen the pair after the same bounded retry delay.
            if (sample.Read.BytesPerSecond is null || sample.Write.BytesPerSecond is null)
            {
                _diskSampler.Dispose();
                _diskSampler = null;
                _diskRetryAt = now.AddSeconds(15);
            }
        }
        catch (Exception error)
        {
            _diskSampler?.Dispose();
            _diskSampler = null;
            _diskRetryAt = now.AddSeconds(15);
            read = Failure(_last?.SystemDiskReadBytesPerSecond, ShortError(error));
            write = Failure(_last?.SystemDiskWriteBytesPerSecond, ShortError(error));
        }
        return (read, write);
    }

    private static MetricSample Fresh(double value, DateTimeOffset now, string? detail = null) => new(value, now, SampleHealth.Fresh, detail);
    private static MetricSample Failure(MetricSample? previous, string message, bool loading = false) => HardwareAlgorithms.RetainFailure(previous, message, loading);
    private static Win32Exception LastError(string operation) => new(Marshal.GetLastWin32Error(), operation + " 读取失败");
    private static string ShortError(Exception error) => error is Win32Exception native
        ? $"{native.Message} (0x{native.NativeErrorCode:X})" : error.Message.Length <= 160 ? error.Message : error.Message[..160];

    private static HardwareSnapshot MissingSnapshot(DateTimeOffset now, string detail)
    {
        var missing = MetricSample.Missing(detail);
        return new HardwareSnapshot(now, missing, missing, missing, missing, missing, missing, missing, missing, missing,
            "GPU 不可用", false, 0, SampleHealth.Unavailable, detail)
        {
            SystemDiskReadBytesPerSecond = missing,
            SystemDiskWriteBytesPerSecond = missing,
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _gpuSampler?.Dispose();
            _gpuSampler = null;
            _diskSampler?.Dispose();
            _diskSampler = null;
            foreach (var process in _processes.Values) process.Dispose();
            _processes.Clear();
        }
    }

    private readonly record struct SystemTimes(long Idle, long Total);
    private sealed record ProcessGroup(HashSet<int> Pids, int Count, bool Present, bool Partial, long? CpuDelta,
        bool CpuPartial, double? MemoryBytes, bool MemoryPartial);
    private sealed record GpuGroup(MetricSample System, MetricSample Codex, MetricSample MemoryUsed, MetricSample MemoryTotal);

    private sealed class ProcessState(SafeProcessHandle handle, long? cpuTime) : IDisposable
    {
        internal SafeProcessHandle Handle { get; } = handle;
        internal long? CpuTime { get; set; } = cpuTime;
        public void Dispose() => Handle.Dispose();
    }

    private sealed class ProcessObservation(SafeProcessHandle handle, ProcessIdentity identity, long cpuTime) : IDisposable
    {
        internal SafeProcessHandle? Handle { get; private set; } = handle;
        internal ProcessIdentity Identity { get; } = identity;
        internal long CpuTime { get; } = cpuTime;
        internal SafeProcessHandle TakeHandle()
        {
            var result = Handle ?? throw new InvalidOperationException("Process handle already transferred");
            Handle = null;
            return result;
        }
        public void Dispose() { Handle?.Dispose(); Handle = null; }
    }
}
