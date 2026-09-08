using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexHud.Core;
using CodexHud.Core.Native;

public static class DiskTests
{
    public static int Run()
    {
        var failures = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { failures++; Console.WriteLine("FAIL Disk: " + message); }
        }
        Check(PdhDiskSampler.ValidateRate(0, 0, 0).BytesPerSecond == 0, "an observed idle disk reports a valid zero");
        Check(PdhDiskSampler.ValidateRate(0, 1, 34567890).BytesPerSecond == 34567890, "byte rates are not capped at one hundred");
        Check(PdhDiskSampler.ValidateRate(0, 0, -1).BytesPerSecond is null, "negative/reset rate cannot become throughput");
        Check(PdhDiskSampler.ValidateRate(0, 0, double.NaN).BytesPerSecond is null
            && PdhDiskSampler.ValidateRate(0, 0, double.PositiveInfinity).BytesPerSecond is null, "nonfinite rates are unavailable");
        Check(PdhDiskSampler.ValidateRate(0x800007D5, 0, 0).BytesPerSecond is null, "API failure cannot become an idle zero");
        Check(PdhDiskSampler.ValidateRate(0, 0x800007D1, 0).BytesPerSecond is null, "missing instance cannot become an idle zero");

        if (OperatingSystem.IsWindows()) failures += RunProviderRecoveryAsync().GetAwaiter().GetResult();
        if (Environment.GetCommandLineArgs().Contains("--live-disk", StringComparer.OrdinalIgnoreCase))
            failures += RunLiveAsync().GetAwaiter().GetResult();
        Console.WriteLine($"DISK_VALIDATION: {failures} failure(s)");
        return failures;
    }

    private static async Task<int> RunProviderRecoveryAsync()
    {
        var failures = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { failures++; Console.WriteLine("FAIL Disk recovery: " + message); }
        }
        var initial = new FixtureSampler(
            new PdhDiskRead(new(null), new(null), true),
            new PdhDiskRead(new(0), new(4096)),
            new PdhDiskRead(new(null, "read unavailable"), new(8192)));
        var recovered = new FixtureSampler(
            new PdhDiskRead(new(null), new(null), true),
            new PdhDiskRead(new(1024), new(0)),
            new InvalidOperationException("fixture query failure"));
        var afterReset = new FixtureSampler(new PdhDiskRead(new(null), new(null), true), new PdhDiskRead(new(2048), new(4096)));
        var samplers = new Queue<FixtureSampler>(new[] { initial, recovered, afterReset });
        var created = 0;
        using (var provider = new HardwareProvider(null, () => { created++; return samplers.Dequeue(); }))
        {
            var baseline = await provider.ReadAsync();
            Check(baseline.SystemDiskReadBytesPerSecond.Health == SampleHealth.Loading
                && baseline.SystemDiskWriteBytesPerSecond.Value is null, "first disk baseline reports no fabricated zero");
            var measured = await provider.ReadAsync();
            Check(measured.SystemDiskReadBytesPerSecond is { Value: 0, Health: SampleHealth.Fresh }
                && measured.SystemDiskWriteBytesPerSecond is { Value: 4096, Health: SampleHealth.Fresh }, "zero and positive directions share a real sample");
            Check(measured.SystemDiskReadBytesPerSecond.ObservedAt == measured.SystemDiskWriteBytesPerSecond.ObservedAt,
                "read and write retain the same observation time");
            var partial = await provider.ReadAsync();
            Check(partial.SystemDiskReadBytesPerSecond is { Value: 0, Health: SampleHealth.Stale }
                && partial.SystemDiskReadBytesPerSecond.ObservedAt == measured.SystemDiskReadBytesPerSecond.ObservedAt
                && partial.SystemDiskWriteBytesPerSecond is { Value: 8192, Health: SampleHealth.Fresh },
                "a failed read direction retains its old value and timestamp while write remains current");
            Check(partial.Health == SampleHealth.Partial && initial.Disposed, "disk failure contributes to snapshot health and releases the bad query");
            var cooldown = await provider.ReadAsync();
            Check(created == 1 && cooldown.SystemDiskWriteBytesPerSecond.Health == SampleHealth.Stale,
                "retry cooldown prevents reopening a failed query on every hardware tick");

            // Move only the test provider's deadline; this covers the production retry branch without a unit-test sleep.
            SetRetryDue(provider);
            var newBaseline = await provider.ReadAsync();
            Check(created == 2 && newBaseline.SystemDiskReadBytesPerSecond.Health == SampleHealth.Stale,
                "retry creates a new query and retains readings until its second sample");
            var fresh = await provider.ReadAsync();
            Check(fresh.SystemDiskReadBytesPerSecond is { Value: 1024, Health: SampleHealth.Fresh }
                && fresh.SystemDiskWriteBytesPerSecond is { Value: 0, Health: SampleHealth.Fresh }, "read and write recover after retry");
            var failed = await provider.ReadAsync();
            Check(failed.SystemDiskReadBytesPerSecond.Health == SampleHealth.Stale
                && failed.SystemDiskReadBytesPerSecond.ObservedAt == fresh.SystemDiskReadBytesPerSecond.ObservedAt
                && failed.SystemDiskWriteBytesPerSecond.ObservedAt == fresh.SystemDiskWriteBytesPerSecond.ObservedAt
                && recovered.Disposed, "query-level failure retains both directions and closes its handle");
            provider.ResetBaseline();
            var resetBaseline = await provider.ReadAsync();
            Check(created == 3 && resetBaseline.SystemDiskReadBytesPerSecond.Health == SampleHealth.Stale,
                "resume/reset bypasses failed-query cooldown and establishes a fresh baseline");
            _ = await provider.ReadAsync();
        }
        Check(afterReset.Disposed, "provider disposal closes the current disk query");
        return failures;
    }

    private static async Task<int> RunLiveAsync()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP Disk live: Windows required"); return 0; }
        var failures = 0;
        var checks = new List<object>();
        void Check(bool condition, string message)
        {
            checks.Add(new { passed = condition, check = message });
            if (!condition) { failures++; Console.WriteLine("FAIL Disk live: " + message); }
        }
        var report = new Dictionary<string, object?>
        {
            ["startedAtUtc"] = DateTimeOffset.UtcNow,
            ["source"] = "PDH English PhysicalDisk(_Total), all physical disks; not logical volumes or process I/O",
            ["readCounter"] = PdhDiskSampler.ReadCounterPath,
            ["writeCounter"] = PdhDiskSampler.WriteCounterPath,
        };
        SafeHandle? query = null;
        using (var provider = new HardwareProvider())
        {
            var first = await provider.ReadAsync();
            Check(first.SystemDiskReadBytesPerSecond.Health == SampleHealth.Loading
                && first.SystemDiskWriteBytesPerSecond.Health == SampleHealth.Loading, "native rate counters establish a baseline");
            await Task.Delay(1100);
            var second = await provider.ReadAsync();
            Check(IsFreshRate(second.SystemDiskReadBytesPerSecond) && IsFreshRate(second.SystemDiskWriteBytesPerSecond),
                "both native physical-disk rates are finite, nonnegative and fresh");
            report["measured"] = SafeDiskSnapshot(second);
            var sampler = GetSampler(provider);
            Check(sampler is not null, "provider owns a disk sampler");
            sampler?.Dispose(); // only closes this test process's query; does not modify drivers or other applications
            var failure = await provider.ReadAsync();
            Check(failure.SystemDiskReadBytesPerSecond.Health == SampleHealth.Stale
                && failure.SystemDiskReadBytesPerSecond.ObservedAt == second.SystemDiskReadBytesPerSecond.ObservedAt
                && failure.SystemDiskWriteBytesPerSecond.Health == SampleHealth.Stale,
                "native query fault retains disk observations and their original timestamps");
            Check(failure.SystemCpu.Health == SampleHealth.Fresh && failure.SystemMemoryUsed.Health == SampleHealth.Fresh,
                "disk query failure does not stop other hardware sampling");
            report["injectedFailure"] = SafeDiskSnapshot(failure);
            var retryAt = DateTimeOffset.UtcNow;
            // Keep the hardware timer alive so this exercises retry, rather than the separate >15-second resume reset.
            while (DateTimeOffset.UtcNow - retryAt < TimeSpan.FromSeconds(15.1))
            {
                await Task.Delay(1000);
                _ = await provider.ReadAsync();
            }
            await Task.Delay(1100);
            var recovery = await provider.ReadAsync();
            Check(IsFreshRate(recovery.SystemDiskReadBytesPerSecond) && IsFreshRate(recovery.SystemDiskWriteBytesPerSecond)
                && recovery.SystemDiskReadBytesPerSecond.ObservedAt > second.SystemDiskReadBytesPerSecond.ObservedAt,
                "native rates recover after the real fifteen-second retry interval");
            report["recovery"] = SafeDiskSnapshot(recovery);
            report["recoveryElapsedSeconds"] = (DateTimeOffset.UtcNow - retryAt).TotalSeconds;
            provider.ResetBaseline();
            var reset = await provider.ReadAsync();
            Check(reset.SystemDiskReadBytesPerSecond.Health == SampleHealth.Stale
                && reset.SystemDiskReadBytesPerSecond.ObservedAt == recovery.SystemDiskReadBytesPerSecond.ObservedAt,
                "explicit resume reset does not reuse a pre-resume rate as current");
            await Task.Delay(1100);
            var afterReset = await provider.ReadAsync();
            Check(IsFreshRate(afterReset.SystemDiskReadBytesPerSecond) && IsFreshRate(afterReset.SystemDiskWriteBytesPerSecond),
                "a second post-reset interval supplies fresh disk rates");
            query = GetSampler(provider)?.GetType().GetField("_query", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(GetSampler(provider)) as SafeHandle;
        }
        Check(query is { IsClosed: true }, "provider disposal closes the native disk query handle");
        report["checks"] = checks;
        report["failureCount"] = failures;
        report["completedAtUtc"] = DateTimeOffset.UtcNow;
        report["limitations"] = new[] { "No artificial disk writes or cache bypass were used.", "Device removal and actual sleep/resume were not performed.", "Rates describe all physical disks and do not attribute disk activity to Codex." };
        var arguments = Environment.GetCommandLineArgs();
        var reportIndex = Array.FindIndex(arguments, argument => argument.Equals("--disk-report", StringComparison.OrdinalIgnoreCase));
        if (reportIndex >= 0 && reportIndex + 1 < arguments.Length)
        {
            var path = Path.GetFullPath(arguments[reportIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("DISK_LIVE " + JsonSerializer.Serialize(report));
        return failures;
    }

    private static bool IsFreshRate(MetricSample metric) => metric.Health == SampleHealth.Fresh && metric.Value is { } value && double.IsFinite(value) && value >= 0;
    private static object SafeDiskSnapshot(HardwareSnapshot value) => new
    {
        observedAt = value.ObservedAt,
        read = value.SystemDiskReadBytesPerSecond,
        write = value.SystemDiskWriteBytesPerSecond,
    };
    private static IDiskSampler? GetSampler(HardwareProvider provider) =>
        typeof(HardwareProvider).GetField("_diskSampler", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider) as IDiskSampler;
    private static void SetRetryDue(HardwareProvider provider) =>
        typeof(HardwareProvider).GetField("_diskRetryAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, DateTimeOffset.MinValue);

    private sealed class FixtureSampler(params object[] samples) : IDiskSampler
    {
        private readonly Queue<object> _samples = new(samples);
        internal bool Disposed { get; private set; }
        public PdhDiskRead Read()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            var item = _samples.Dequeue();
            return item is Exception error ? throw error : (PdhDiskRead)item;
        }
        public void Dispose() => Disposed = true;
    }
}
