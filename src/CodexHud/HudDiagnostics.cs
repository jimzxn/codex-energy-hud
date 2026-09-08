using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexHud;

// Opt-in resource receipts contain no conversation text, credentials, or quota payloads.
internal sealed class HudDiagnostics : IAsyncDisposable
{
    private const double SampleSeconds = 10, MaximumGapSeconds = 30, ClockToleranceSeconds = 2;
    private readonly string _directory;
    private readonly Func<int?> _quotaPid;
    private readonly Func<object> _health;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<ProcessKey, TrackedProcess> _tracked = [];
    private Task? _task;
    private int _disposed;
    internal string? LastError { get; private set; }

    internal HudDiagnostics(string directory, Func<int?> quotaPid, Func<object> health)
    {
        _directory = Path.GetFullPath(directory); _quotaPid = quotaPid; _health = health;
    }
    internal void Start(int seconds)
    {
        if (_task is not null || Volatile.Read(ref _disposed) != 0) return;
        _task = Task.Run(() => Run(seconds <= 0 ? 7200 : seconds));
    }
    private async Task Run(int requestedSeconds)
    {
        string runId = Guid.NewGuid().ToString("N"), status = "running";
        var clock = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        DateTimeOffset? previousWall = null;
        double previousMonotonic = 0;
        ulong? previousAwake = null;
        double coverage = 0, weightedCpu = 0, peakMemory = 0, peakCpu = 0, skippedSeconds = 0;
        int samples = 0, validIntervals = 0, skippedIntervals = 0, maxHandles = 0, readErrors = 0;
        bool quotaObserved = false;
        var skipReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var recentSkips = new Queue<object>();
        StreamWriter? writer = null;
        object Receipt() => new
        {
            runId, status, startedAt = started, updatedAt = DateTimeOffset.UtcNow,
            requestedSeconds, wallElapsedSeconds = (DateTimeOffset.UtcNow - started).TotalSeconds,
            actualSeconds = clock.Elapsed.TotalSeconds, effectiveCoverageSeconds = coverage,
            completed = status == "completed" && coverage >= requestedSeconds,
            samples, validIntervals, skippedIntervals, skippedSeconds, skipReasons,
            recentSkippedIntervals = recentSkips.ToArray(), maximumSamplingGapSeconds = MaximumGapSeconds,
            averageCpuPercent = coverage > 0 ? weightedCpu / coverage : (double?)null,
            peakCpuPercent = peakCpu, peakPrivateBytesMb = peakMemory, maxWidgetHandles = maxHandles,
            quotaProcessObserved = quotaObserved, processReadErrors = readErrors,
            processTreeDefinition = "Widget plus creation-time-verified descendants; known descendants remain tracked after their parent exits.",
            cpuDefinition = "Sampled process CPU deltas divided by logical CPU count and valid monotonic elapsed time. First sample is baseline only.",
            shortLivedProcessesMayBeMissed = true,
            memoryDefinition = "Sum of process private committed bytes; not resident working set.",
            cpuTargetPercent = .5,
            cpuTargetMet = status == "completed" && coverage >= requestedSeconds && validIntervals > 0 && weightedCpu / coverage < .5,
            cpuTargetInterpretation = "Sampled process-tree estimate; descendants born and exited between discovery samples may be absent.",
            error = LastError
        };
        try
        {
            Directory.CreateDirectory(_directory);
            writer = new StreamWriter(Path.Combine(_directory, "resources.jsonl"), false) { AutoFlush = true };
            WriteReceipt(Receipt()); // A crash leaves a running receipt, never a false completion.
            while (!_stop.IsCancellationRequested)
            {
                var warnings = new List<string>();
                var resource = SampleProcesses(previousWall, warnings);
                var wall = DateTimeOffset.UtcNow;
                double monotonic = clock.Elapsed.TotalSeconds, elapsed = monotonic - previousMonotonic;
                ulong? awake = ReadAwakeTicks();
                double? wallElapsed = previousWall is { } oldWall ? (wall - oldWall).TotalSeconds : null;
                double? awakeElapsed = awake is { } current && previousAwake is { } old && current >= old
                    ? (current - old) / 10_000_000d : null;
                string? skip = IntervalSkipReason(previousWall is not null, elapsed, wallElapsed, awakeElapsed, resource.Complete);
                bool valid = skip is null;
                double? cpu = previousWall is not null && elapsed > 0
                    ? 100 * resource.CpuDeltaSeconds / elapsed / Environment.ProcessorCount : null;
                if (valid && cpu is { } percent)
                {
                    coverage += elapsed; weightedCpu += percent * elapsed;
                    peakCpu = Math.Max(peakCpu, percent); validIntervals++;
                }
                else if (skip != "baseline")
                {
                    skippedIntervals++; skippedSeconds += Math.Max(elapsed, wallElapsed.GetValueOrDefault());
                    skipReasons[skip!] = skipReasons.GetValueOrDefault(skip!) + 1;
                    recentSkips.Enqueue(new { from = previousWall, to = wall, monotonicSeconds = elapsed,
                        wallSeconds = wallElapsed, awakeSeconds = awakeElapsed, reason = skip });
                    while (recentSkips.Count > 128) recentSkips.Dequeue();
                }
                peakMemory = Math.Max(peakMemory, resource.PrivateBytes / 1048576d);
                maxHandles = Math.Max(maxHandles, resource.WidgetHandles);
                readErrors += resource.Errors; quotaObserved |= resource.QuotaObserved;
                object? health = null;
                try { health = _health(); }
                catch (Exception ex) { warnings.Add("health:" + ex.GetType().Name); }
                writer.WriteLine(JsonSerializer.Serialize(new
                {
                    runId, at = wall, elapsedSeconds = monotonic, effectiveCoverageSeconds = coverage,
                    intervalSeconds = elapsed, wallIntervalSeconds = wallElapsed, awakeIntervalSeconds = awakeElapsed,
                    validInterval = valid, skippedReason = skip, cpuPercent = valid ? cpu : null,
                    privateBytesMb = resource.PrivateBytes / 1048576d, processCount = resource.LiveCount,
                    handles = resource.WidgetHandles, resource.QuotaObserved, resource.Complete, warnings, health
                }));
                samples++; previousWall = wall; previousMonotonic = monotonic; previousAwake = awake;
                if (coverage >= requestedSeconds) { status = "completed"; break; }
                WriteReceipt(Receipt());
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(.05, Math.Min(SampleSeconds, requestedSeconds - coverage))), _stop.Token);
            }
            if (status != "completed") status = "interrupted";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { status = "interrupted"; }
        catch (Exception ex) { status = "failed"; LastError = ex.GetType().Name; }
        finally
        {
            try { writer?.Dispose(); }
            catch (Exception ex) { LastError ??= ex.GetType().Name; status = "failed"; }
            foreach (var item in _tracked.Values) item.Process.Dispose();
            _tracked.Clear();
            try { WriteReceipt(Receipt()); }
            catch (Exception ex) { LastError ??= ex.GetType().Name; }
        }
    }

    internal static string? IntervalSkipReason(bool hasBaseline, double monotonicSeconds,
        double? wallSeconds, double? awakeSeconds, bool completeProcessSample)
    {
        if (!hasBaseline) return "baseline";
        if (!double.IsFinite(monotonicSeconds) || monotonicSeconds <= 0 || wallSeconds is not { } wall || !double.IsFinite(wall))
            return "invalidClock";
        if (Math.Abs(wall - monotonicSeconds) > ClockToleranceSeconds) return "wallClockDiscontinuity";
        // QPC can include sleep on Windows; unbiased interrupt time excludes it.
        if (awakeSeconds is { } awake && monotonicSeconds - awake > ClockToleranceSeconds) return "systemSuspended";
        if (monotonicSeconds > MaximumGapSeconds || wall > MaximumGapSeconds) return "samplingGap";
        if (!completeProcessSample) return "incompleteProcessSample";
        return null;
    }

    private ResourceSample SampleProcesses(DateTimeOffset? intervalStart, List<string> warnings)
    {
        int errors = 0; bool complete = true;
        var entries = new Dictionary<int, int>();
        var native = CreateToolhelp32Snapshot(2, 0);
        if (native == new IntPtr(-1)) { complete = false; errors++; warnings.Add("processSnapshotUnavailable"); }
        else
        {
            try
            {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                if (!Process32First(native, ref entry)) { complete = false; errors++; warnings.Add("processSnapshotEmpty"); }
                else do { entries[(int)entry.ProcessId] = (int)entry.ParentProcessId; } while (Process32Next(native, ref entry));
            }
            finally { CloseHandle(native); }
        }
        var owned = new Dictionary<int, long>();
        foreach (var pair in _tracked)
        {
            try { if (!pair.Value.Process.HasExited) owned[pair.Key.Pid] = pair.Key.Created; }
            catch (InvalidOperationException) { }
        }
        long? Track(int pid, long? parentCreated)
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(pid);
                long created = process.StartTime.ToUniversalTime().Ticks;
                if (parentCreated.HasValue && created < parentCreated.Value) { process.Dispose(); return null; }
                var key = new ProcessKey(pid, created);
                if (_tracked.ContainsKey(key)) { process.Dispose(); return created; }
                double total = process.TotalProcessorTime.TotalSeconds; // Pins the process identity via its handle.
                bool includeInitial = intervalStart.HasValue && created >= intervalStart.Value.UtcTicks;
                if (intervalStart.HasValue && !includeInitial) { complete = false; warnings.Add("lateProcessDiscovery"); }
                _tracked.Add(key, new(process, total, includeInitial));
                return created;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                process?.Dispose(); complete = false; errors++; warnings.Add("processDiscovery:" + ex.GetType().Name);
                return null;
            }
        }
        if (Track(Environment.ProcessId, null) is { } selfCreated) owned[Environment.ProcessId] = selfCreated;
        var children = entries.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToArray());
        var queue = new Queue<int>(owned.Keys);
        while (queue.TryDequeue(out var parent))
        {
            if (!children.TryGetValue(parent, out var descendants)) continue;
            foreach (int pid in descendants)
            {
                if (owned.ContainsKey(pid)) continue;
                if (Track(pid, owned[parent]) is not { } created) continue;
                owned[pid] = created; queue.Enqueue(pid);
            }
        }
        int? quotaPid = null;
        try { quotaPid = _quotaPid(); }
        catch (Exception ex) { complete = false; warnings.Add("quotaIdentity:" + ex.GetType().Name); }
        bool quotaObserved = quotaPid is { } quota && owned.ContainsKey(quota);
        if (quotaPid.HasValue && !quotaObserved) { complete = false; warnings.Add("quotaRootNotObserved"); }
        double cpuDelta = 0; long privateBytes = 0; int liveCount = 0, handles = 0;
        foreach (var (key, tracked) in _tracked.ToArray())
        {
            try
            {
                var process = tracked.Process;
                double total = process.TotalProcessorTime.TotalSeconds, difference = total - tracked.LastCpu;
                if (difference < 0) { complete = false; warnings.Add("cpuCounterReset"); }
                else cpuDelta += difference + (tracked.IncludeInitialCpu ? tracked.LastCpu : 0);
                tracked.LastCpu = total; tracked.IncludeInitialCpu = false;
                if (process.HasExited) { process.Dispose(); _tracked.Remove(key); continue; }
                process.Refresh(); privateBytes += process.PrivateMemorySize64; liveCount++;
                if (key.Pid == Environment.ProcessId) handles = process.HandleCount;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                complete = false; errors++; warnings.Add("processRead:" + ex.GetType().Name);
                tracked.Process.Dispose(); _tracked.Remove(key);
            }
        }
        return new(cpuDelta, privateBytes, liveCount, handles, complete, quotaObserved, errors);
    }

    private void WriteReceipt(object receipt)
    {
        string destination = Path.Combine(_directory, "soak-result.json"), temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, destination, true);
    }
    private static ulong? ReadAwakeTicks() => QueryUnbiasedInterruptTime(out ulong value) ? value : null;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { if (_task is not null) await _task.ConfigureAwait(false); }
        catch (Exception ex) { LastError ??= ex.GetType().Name; }
        finally
        {
            foreach (var item in _tracked.Values) item.Process.Dispose();
            _tracked.Clear(); _stop.Dispose();
        }
    }
    private readonly record struct ProcessKey(int Pid, long Created);
    private sealed class TrackedProcess(Process process, double cpu, bool includeInitial)
    {
        internal Process Process { get; } = process;
        internal double LastCpu { get; set; } = cpu;
        internal bool IncludeInitialCpu { get; set; } = includeInitial;
    }
    private sealed record ResourceSample(double CpuDeltaSeconds, long PrivateBytes, int LiveCount,
        int WidgetHandles, bool Complete, bool QuotaObserved, int Errors);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId; public UIntPtr DefaultHeapId; public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
