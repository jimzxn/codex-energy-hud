using System.Diagnostics;
using CodexHud.Core.Native;
using static CodexHud.Core.Native.MacHardwareNative;

namespace CodexHud.Core;

/// <summary>Read-only macOS hardware sampling, with explicit metric availability and freshness.</summary>
public sealed class MacHardwareProvider : IHardwareProvider
{
    private readonly object _gate = new();
    private readonly Func<int?>? _excludedQuotaPid;
    private readonly Dictionary<ProcessIdentity, ulong?> _processes = [];
    private HardwareSnapshot? _last;
    private Host? _host;
    private CpuTicks? _cpuBaseline;
    private long? _lastReadTimestamp;
    private long? _processTimestamp;
    private long? _processStartedAfter;
    private MacDiskSampler.Snapshot? _diskBaseline;
    private long? _diskTimestamp;
    private bool _disposed;

    public MacHardwareProvider(Func<int?>? excludedQuotaPid = null) => _excludedQuotaPid = excludedQuotaPid;

    public Task<HardwareSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try { return ReadCore(cancellationToken); }
            catch (OperationCanceledException)
            {
                // A canceled partial pass cannot become the baseline for a full subsequent interval.
                ResetBaselineCore();
                throw;
            }
        }
    }, cancellationToken);

    public void ResetBaseline()
    {
        lock (_gate)
        {
            if (!_disposed) ResetBaselineCore();
        }
    }

    private void ResetBaselineCore()
    {
        _cpuBaseline = null;
        _lastReadTimestamp = null;
        _processTimestamp = null;
        _processStartedAfter = null;
        _diskBaseline = null;
        _diskTimestamp = null;
        // Keep identity-verified membership for descendants whose original parent has exited.
        foreach (var identity in _processes.Keys.ToArray()) _processes[identity] = null;
    }

    private HardwareSnapshot ReadCore(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsMacOS()) return MissingSnapshot(now, "macOS 硬件采集仅适用于 macOS");
        var timestamp = Stopwatch.GetTimestamp();
        if ((_lastReadTimestamp.HasValue && Stopwatch.GetElapsedTime(_lastReadTimestamp.Value, timestamp) > TimeSpan.FromSeconds(15)) || (_last is not null && now - _last.ObservedAt > TimeSpan.FromSeconds(15)))
            ResetBaselineCore();
        _lastReadTimestamp = timestamp;

        MetricSample systemCpu;
        try
        {
            _host ??= new Host();
            var current = _host.ReadCpu();
            if (_cpuBaseline is { } previous)
            {
                // HOST_CPU_LOAD_INFO has uint32 counters; modular subtraction permits one wrap.
                var user = unchecked(current.User - previous.User);
                var system = unchecked(current.System - previous.System);
                var idle = unchecked(current.Idle - previous.Idle);
                var nice = unchecked(current.Nice - previous.Nice);
                var busy = (ulong)user + system + nice;
                var total = busy + idle;
                systemCpu = total > 0
                    ? Fresh(100d * busy / total, now, "Mach：整机 CPU 时间占比")
                    : Failure(_last?.SystemCpu, "等待下一次 CPU 计数更新", true);
            }
            else systemCpu = Failure(_last?.SystemCpu, "等待第二次 CPU 采样", true);
            _cpuBaseline = current;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _cpuBaseline = null;
            systemCpu = Failure(_last?.SystemCpu, ShortError(error));
        }

        token.ThrowIfCancellationRequested();
        MetricSample memoryUsed, memoryTotal;
        try
        {
            _host ??= new Host();
            var memory = _host.ReadMemory();
            memoryUsed = Fresh(memory.Used, now, "已占用物理页：总量减空闲页，含文件缓存；非内存压力指标");
            memoryTotal = Fresh(memory.Total, now, "sysctl：物理内存总量");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            memoryUsed = Failure(_last?.SystemMemoryUsed, ShortError(error));
            memoryTotal = Failure(_last?.SystemMemoryTotal, ShortError(error));
        }

        ProcessGroup? group = null;
        MetricSample codexCpu, codexMemory;
        try
        {
            group = ReadProcessGroup(now, token);
            var detail = $"当前用户 Codex 进程组：{group.Count} 个可读进程";
            if (group.Partial) detail += "；部分进程或应用归属不可读";
            codexCpu = group.CpuPercent.HasValue
                ? new MetricSample(group.CpuPercent.Value, now, group.CpuPartial || group.Partial ? SampleHealth.Partial : SampleHealth.Fresh,
                    detail + "；按整机逻辑 CPU 容量归一化；采样间完全退出的进程可能遗漏")
                : Failure(_last?.CodexCpu, group.Partial ? "Codex CPU 进程采样不完整" : "等待 Codex CPU 基线", !group.Partial);
            codexMemory = group.MemoryBytes.HasValue
                ? new MetricSample(group.MemoryBytes.Value, now, group.Partial ? SampleHealth.Partial : SampleHealth.Fresh,
                    detail + "；驻留内存 RSS 合计，共享页可能重复计入")
                : Failure(_last?.CodexMemory, "Codex 进程内存暂不可读");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _processTimestamp = null;
            _processStartedAfter = null;
            foreach (var identity in _processes.Keys.ToArray()) _processes[identity] = null;
            codexCpu = Failure(_last?.CodexCpu, ShortError(error));
            codexMemory = Failure(_last?.CodexMemory, ShortError(error));
        }

        token.ThrowIfCancellationRequested();
        var disk = ReadDisk(now, token);
        var gpu = MetricSample.Missing("此 macOS 实现尚未接入可信的整机或逐进程 GPU 利用率采样");
        var vram = MetricSample.Missing("显存计数不可用；统一内存不作为专用显存显示");
        var metrics = new[] { systemCpu, codexCpu, memoryUsed, memoryTotal, codexMemory, disk.Read, disk.Write, gpu, vram };
        var message = string.Join("；", metrics.Where(metric => metric.Health is SampleHealth.Partial or SampleHealth.Stale or SampleHealth.Unavailable)
            .Select(metric => metric.Detail).Where(detail => !string.IsNullOrEmpty(detail)).Distinct().Take(4));
        _last = new HardwareSnapshot(now, systemCpu, codexCpu, gpu, gpu, memoryUsed, memoryTotal, codexMemory,
            vram, vram, "macOS GPU", group?.Present ?? _last?.CodexPresent ?? false,
            group?.Count ?? _last?.CodexProcessCount ?? 0, SampleHealth.Partial, message)
        {
            SystemDiskReadBytesPerSecond = disk.Read,
            SystemDiskWriteBytesPerSecond = disk.Write,
        };
        return _last;
    }

    private ProcessGroup ReadProcessGroup(DateTimeOffset now, CancellationToken token)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var (table, unreadable) = ReadProcessTable(token);
        var entries = table.ToDictionary(entry => checked((int)entry.Pid));
        var parents = entries.ToDictionary(pair => pair.Key, pair => checked((int)pair.Value.ParentPid));
        var exclusionRoots = new List<int> { Environment.ProcessId };
        if (_excludedQuotaPid?.Invoke() is > 0 and var quotaPid) exclusionRoots.Add(quotaPid);
        var excluded = HardwareAlgorithms.ExcludedTree(parents, exclusionRoots);
        var children = parents.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToArray());
        var observations = new Dictionary<int, ProcessObservation?>();
        var group = new Dictionary<ProcessIdentity, ProcessObservation>();
        var queue = new Queue<ProcessObservation>();
        var failed = new HashSet<ProcessIdentity>();
        var discoveryIncomplete = unreadable.Count > 0;

        ProcessObservation? Observe(int pid)
        {
            token.ThrowIfCancellationRequested();
            if (!observations.TryGetValue(pid, out var observed)) observations[pid] = observed = ReadProcess(pid);
            return observed;
        }

        void Enqueue(ProcessObservation observed)
        {
            if (!excluded.Contains(observed.Identity.Pid) && group.TryAdd(observed.Identity, observed)) queue.Enqueue(observed);
        }

        foreach (var identity in _processes.Keys)
        {
            if (excluded.Contains(identity.Pid)) continue;
            if (unreadable.Contains(identity.Pid)) { failed.Add(identity); continue; }
            if (!entries.ContainsKey(identity.Pid)) continue;
            var observed = Observe(identity.Pid);
            if (observed?.Identity == identity) Enqueue(observed);
            else if (observed is null) failed.Add(identity);
        }

        foreach (var entry in entries.Values)
        {
            token.ThrowIfCancellationRequested();
            var pid = checked((int)entry.Pid);
            if (excluded.Contains(pid) || !IsRootName(entry.Name)) continue;
            var observed = Observe(pid);
            var path = ReadProcessPath(pid);
            if (path is null) { discoveryIncomplete = true; continue; }
            if (!IsApplicationImage(path)) continue;
            if (observed is null) { discoveryIncomplete = true; continue; }
            // Confirm the image belongs to the same creation identity after reading its path.
            var confirmed = ReadProcess(pid);
            if (confirmed?.Identity != observed.Identity) { discoveryIncomplete = true; continue; }
            Enqueue(confirmed);
        }

        while (queue.TryDequeue(out var parent))
        {
            if (!children.TryGetValue(parent.Identity.Pid, out var descendants)) continue;
            foreach (var pid in descendants)
            {
                if (excluded.Contains(pid)) continue;
                var child = Observe(pid);
                if (child is null) { discoveryIncomplete = true; continue; }
                // Both parent relation and creation order are checked to reject a recycled PID edge.
                if (child.ParentPid == parent.Identity.Pid && child.Identity.CreationTime >= parent.Identity.CreationTime)
                    Enqueue(child);
            }
        }

        var partial = failed.Count > 0 || discoveryIncomplete;
        var cpuPartial = partial;
        var cpuMeasured = 0;
        double cpuTicks = 0, memory = 0;
        foreach (var (identity, observed) in group)
        {
            memory += observed.ResidentBytes;
            if (_processes.TryGetValue(identity, out var previous) && previous.HasValue && observed.CpuTime >= previous.Value)
            {
                cpuTicks += observed.CpuTime - previous.Value;
                cpuMeasured++;
            }
            else if (_processStartedAfter.HasValue && identity.CreationTime >= _processStartedAfter.Value)
            {
                cpuTicks += observed.CpuTime;
                cpuMeasured++;
            }
            else cpuPartial = true;
        }

        // libproc does not retain a handle to an exited process, so its final interval is unknowable.
        var exited = _processes.Keys.Any(identity => !excluded.Contains(identity.Pid) && !group.ContainsKey(identity) && !failed.Contains(identity));
        cpuPartial |= exited;
        var empty = group.Count == 0 && !partial;
        double? cpu = empty && !exited ? 0 : null;
        if (cpuMeasured > 0 && _processTimestamp.HasValue)
        {
            var elapsed = Stopwatch.GetElapsedTime(_processTimestamp.Value, timestamp).TotalSeconds;
            if (elapsed > 0)
                cpu = Math.Clamp(100 * cpuTicks * CpuNanosecondsPerTick() / (elapsed * 1_000_000_000 * LogicalCpuCount()), 0, 100);
        }

        _processes.Clear();
        foreach (var (identity, observed) in group) _processes[identity] = observed.CpuTime;
        foreach (var identity in failed) _processes.TryAdd(identity, null);
        _processTimestamp = timestamp;
        _processStartedAfter = checked(now.ToUnixTimeMilliseconds() * 1000);
        return new ProcessGroup(group.Count > 0 || failed.Count > 0 || (discoveryIncomplete && (_last?.CodexPresent ?? false)),
            group.Count, partial, cpu, cpuPartial, group.Count > 0 || empty ? memory : null);
    }

    private (MetricSample Read, MetricSample Write) ReadDisk(DateTimeOffset now, CancellationToken token)
    {
        try
        {
            var timestamp = Stopwatch.GetTimestamp();
            var current = MacDiskSampler.Read(token);
            var previous = _diskBaseline;
            var previousTimestamp = _diskTimestamp;
            _diskBaseline = current;
            _diskTimestamp = timestamp;
            if (previous is null || !previousTimestamp.HasValue || !current.Devices.Keys.ToHashSet().SetEquals(previous.Devices.Keys))
                return (Failure(_last?.SystemDiskReadBytesPerSecond, "等待磁盘基线；设备变化时重新采样", true),
                    Failure(_last?.SystemDiskWriteBytesPerSecond, "等待磁盘基线；设备变化时重新采样", true));
            var seconds = Stopwatch.GetElapsedTime(previousTimestamp.Value, timestamp).TotalSeconds;
            if (seconds <= 0) throw new InvalidOperationException("磁盘采样间隔无效");

            MetricSample Direction(bool reading, MetricSample? last)
            {
                var measured = 0;
                double bytes = 0;
                var partial = current.Partial || previous.Partial;
                foreach (var (id, counters) in current.Devices)
                {
                    var value = reading ? counters.ReadBytes : counters.WriteBytes;
                    var old = reading ? previous.Devices[id].ReadBytes : previous.Devices[id].WriteBytes;
                    if (value.HasValue && old.HasValue && value.Value >= old.Value)
                    {
                        bytes += value.Value - old.Value;
                        measured++;
                    }
                    else partial = true;
                }
                if (measured == 0) return Failure(last, "磁盘计数暂不可读或已重置");
                return new MetricSample(bytes / seconds, now, partial ? SampleHealth.Partial : SampleHealth.Fresh,
                    $"IOKit：{measured} 个可识别物理磁盘{(reading ? "读取" : "写入")}字节/秒" + (partial ? "；部分设备未计入" : ""));
            }
            return (Direction(true, _last?.SystemDiskReadBytesPerSecond), Direction(false, _last?.SystemDiskWriteBytesPerSecond));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _diskBaseline = null;
            _diskTimestamp = null;
            return (Failure(_last?.SystemDiskReadBytesPerSecond, ShortError(error)),
                Failure(_last?.SystemDiskWriteBytesPerSecond, ShortError(error)));
        }
    }

    private static bool IsRootName(string name) => name.Equals("Codex", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase);

    private static bool IsApplicationImage(string path) => path.EndsWith("/Codex.app/Contents/MacOS/Codex", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/Codex.app/Contents/MacOS/ChatGPT", StringComparison.OrdinalIgnoreCase);

    private static MetricSample Fresh(double value, DateTimeOffset now, string detail) => new(value, now, SampleHealth.Fresh, detail);
    private static MetricSample Failure(MetricSample? previous, string detail, bool loading = false) => HardwareAlgorithms.RetainFailure(previous, detail, loading);
    private static string ShortError(Exception error) => error.Message.Length <= 160 ? error.Message : error.Message[..160];

    private static HardwareSnapshot MissingSnapshot(DateTimeOffset now, string detail)
    {
        var missing = MetricSample.Missing(detail);
        return new HardwareSnapshot(now, missing, missing, missing, missing, missing, missing, missing, missing, missing,
            "macOS GPU", false, 0, SampleHealth.Unavailable, detail)
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
            _host?.Dispose();
            _host = null;
            _processes.Clear();
            _diskBaseline = null;
        }
    }

    private sealed record ProcessGroup(bool Present, int Count, bool Partial, double? CpuPercent, bool CpuPartial, double? MemoryBytes);
}
