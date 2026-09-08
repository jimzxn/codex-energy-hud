using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexHud.Core;

public static class HardwareTests
{
    public static int Run()
    {
        var failures = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { failures++; Console.WriteLine("FAIL Hardware: " + message); }
        }

        Check(Math.Abs(HardwareAlgorithms.CpuPercent(10, 320) - 3.125) < 0.0001,
            "one fully occupied logical processor on a 32-thread CPU is 3.125% of the machine");
        Check(double.IsNaN(HardwareAlgorithms.CpuPercent(1, 0)), "zero-duration intervals must not become 0% usage");
        Check(HardwareAlgorithms.CpuPercent(-10, 100) == 0 && HardwareAlgorithms.CpuPercent(110, 100) == 100,
            "displayed CPU percentages remain bounded");

        const string adapter = "luid_0x00000000_0x00019ef7";
        Check(HardwareAlgorithms.TryParseGpuInstance("pid_123_" + adapter + "_phys_0_eng_2_engtype_Compute", out var parsed)
            && parsed.Pid == 123 && parsed.PhysicalAdapter == 0 && parsed.Engine == 2 && parsed.AdapterToken == adapter,
            "GPU counter PID, LUID, physical adapter and engine must all be retained");
        Check(!HardwareAlgorithms.TryParseGpuInstance("pid_bad_gpu", out _), "malformed counters cannot be attributed");
        var aggregate = HardwareAlgorithms.AggregateGpu(new[]
        {
            (new GpuEngineIdentity(10, adapter, 0, 0), 40d),
            (new GpuEngineIdentity(11, adapter, 0, 0), 30d),
            (new GpuEngineIdentity(12, adapter, 0, 1), 80d),
            (new GpuEngineIdentity(10, adapter, 0, 1), 5d),
            (new GpuEngineIdentity(10, "luid_0x00000000_0xDEADBEEF", 0, 0), 100d),
        }, adapter, new HashSet<int> { 10, 11 });
        Check(aggregate.System == 85 && aggregate.Codex == 70 && aggregate.Matched == 4,
            "GPU group sums processes within an engine, then takes its maximum; other adapters are excluded");
        var idle = HardwareAlgorithms.AggregateGpu(new[] { (new GpuEngineIdentity(12, adapter, 0, 0), 0d) }, adapter, new HashSet<int>());
        Check(idle.Matched == 1 && idle.System == 0 && idle.Codex == 0, "successful idle GPU readings are real zeroes");
        var absent = HardwareAlgorithms.AggregateGpu(Array.Empty<(GpuEngineIdentity, double)>(), adapter, new HashSet<int>());
        Check(absent.Matched == 0, "missing GPU counters are distinguishable from successful zero readings");
        var physical = HardwareAlgorithms.AggregateGpu(new[]
        {
            (new GpuEngineIdentity(10, adapter, 0, 0), 60d),
            (new GpuEngineIdentity(10, adapter, 1, 0), 60d),
        }, adapter, new HashSet<int> { 10 });
        Check(physical.System == 60 && physical.Codex == 60, "engines on different physical GPUs cannot be added together");

        var when = DateTimeOffset.UtcNow.AddMinutes(-1);
        var stale = HardwareAlgorithms.RetainFailure(new MetricSample(42, when), "disconnected");
        Check(stale.Value == 42 && stale.ObservedAt == when && stale.Health == SampleHealth.Stale,
            "read failures preserve the old value AND its actual observation timestamp");
        Check(HardwareAlgorithms.RetainFailure(null, "baseline", true).Health == SampleHealth.Loading,
            "first CPU samples report Loading rather than zero");

        Check(HardwareAlgorithms.IsApplicationImage(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.0_x64__publisher\app\ChatGPT.exe"),
            "Codex package's ChatGPT.exe is an application root");
        Check(!HardwareAlgorithms.IsApplicationImage(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT_26.0_x64__publisher\app\ChatGPT.exe"),
            "the separate ChatGPT application is not the Codex GUI");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Check(!HardwareAlgorithms.IsApplicationImage(Path.Combine(local, @"OpenAI\Codex\bin\somehash\codex.exe")),
            "a Codex sidecar or quota bridge cannot independently count as the GUI");
        Check(!HardwareAlgorithms.IsApplicationImage(@"G:\CodexHome\plugins\.plugin-appserver\codex.exe"),
            "plugin app-server processes do not independently count as the GUI");
        Check(!HardwareAlgorithms.IsApplicationImage(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.0_x64__publisher\app\resources\codex.exe"),
            "a sidecar nested under a genuine app installation is not itself an application root");
        var excluded = HardwareAlgorithms.ExcludedTree(new Dictionary<int, int>
        {
            [10] = 1, [11] = 10, [12] = 11, [20] = 1, [21] = 20, [30] = 1,
        }, new[] { 10, 20 });
        Check(excluded.SetEquals(new[] { 10, 11, 12, 20, 21 }), "widget and quota subprocess trees are entirely excluded");

        if (Environment.GetCommandLineArgs().Contains("--live-hardware", StringComparer.OrdinalIgnoreCase))
            failures += RunLive();
        return failures;
    }

    public static int RunLive() => RunLiveAsync().GetAwaiter().GetResult();

    private static async Task<int> RunLiveAsync()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP Hardware live: Windows required"); return 0; }
        var failures = 0;
        var checks = new List<object>();
        var report = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["startedAtUtc"] = DateTimeOffset.UtcNow,
            ["operatingSystemVersion"] = Environment.OSVersion.VersionString,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["logicalProcessors"] = Environment.ProcessorCount,
            ["activeLogicalProcessorsAllGroups"] = GetActiveProcessorCount(ushort.MaxValue),
            ["processorGroups"] = GetActiveProcessorGroupCount(),
            ["getSystemTimesCoverage"] = "Current implementation is scoped to a single processor group; systems above 64 logical processors were not validated.",
            ["privacy"] = "No process IDs, process paths, account quota values, task titles or task contents are recorded.",
        };
        void Check(bool condition, string message)
        {
            checks.Add(new { passed = condition, check = message });
            if (!condition) { failures++; Console.WriteLine("FAIL Hardware live: " + message); }
        }
        var initialHandles = Process.GetCurrentProcess().HandleCount;
        IReadOnlyList<SafeHandle> capturedNativeHandles = [];
        using (var provider = new HardwareProvider())
        {
            var first = await provider.ReadAsync();
            Check(first.SystemCpu.Health == SampleHealth.Loading, "first system CPU sample must establish a baseline");
            await Task.Delay(1100);
            var second = await provider.ReadAsync();
            report["measuredSnapshot"] = SafeSnapshot(second);
            Console.WriteLine("HARDWARE_LIVE " + JsonSerializer.Serialize(SafeSnapshot(second)));
            Check(second.SystemCpu.Value is >= 0 and <= 100, "system CPU must have a real bounded reading");
            Check(second.SystemMemoryTotal.Value > 0 && second.SystemMemoryUsed.Value > 0
                && second.SystemMemoryUsed.Value <= second.SystemMemoryTotal.Value, "physical memory must reconcile");
            Check(second.SystemGpu.Value is >= 0 and <= 100, "GPU counter must yield a real bounded reading");
            Check(second.GpuMemoryTotal.Value > 0 && second.GpuMemoryUsed.Value is >= 0, "GPU capacity and dedicated usage must read successfully");
            if (second.CodexPresent)
            {
                Check(second.CodexProcessCount > 0, "present Codex must have attributable processes");
                Check(second.CodexCpu.Value is >= 0 and <= 100, "Codex CPU group must have a measured reading");
                Check(second.CodexGpu.Value is >= 0 and <= 100, "Codex GPU group must have a measured reading");
                Check(second.CodexMemory.Value > 0, "Codex private working set must be available");
            }
            provider.ResetBaseline();
            var resumed = await provider.ReadAsync();
            Check(resumed.SystemCpu.Health == SampleHealth.Stale && resumed.SystemCpu.ObservedAt == second.SystemCpu.ObservedAt,
                "reset retains old CPU observation while waiting for a new baseline");
            await Task.Delay(1100);
            var recovered = await provider.ReadAsync();
            Check(recovered.SystemCpu.Health == SampleHealth.Fresh && recovered.SystemCpu.ObservedAt > second.SystemCpu.ObservedAt,
                "CPU sampling recovers after reset");
            var stableHandles = Process.GetCurrentProcess().HandleCount;
            for (var iteration = 0; iteration < 8; iteration++)
            {
                await Task.Delay(200);
                _ = await provider.ReadAsync();
            }
            var afterSamplingHandles = Process.GetCurrentProcess().HandleCount;
            Check(afterSamplingHandles - stableHandles < 20, "repeated samples must not leak process or query handles");
            report["handleStability"] = new { initial = initialHandles, afterWarmup = stableHandles, afterEightMoreSamples = afterSamplingHandles };
            Console.WriteLine($"HARDWARE_HANDLES initial={initialHandles}, stable={stableHandles}, after={afterSamplingHandles}");

            // Test-only reflection inspects the provider's own identity set; no paths or PID values leave this process.
            Check(!TrackedPids(provider).Contains(Environment.ProcessId), "live hardware group excludes the test/widget process itself");

            // Dispose only this provider's PDH handle. This injects a native-query failure without touching a driver or device.
            var beforeFault = await provider.ReadAsync();
            var nativeSampler = typeof(HardwareProvider).GetField("_gpuSampler", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider) as IDisposable;
            Check(nativeSampler is not null, "a real PDH sampler exists before the bounded query-failure injection");
            nativeSampler?.Dispose();
            var fault = await provider.ReadAsync();
            Check(fault.SystemGpu.Health == SampleHealth.Stale && fault.SystemGpu.Value == beforeFault.SystemGpu.Value
                && fault.SystemGpu.ObservedAt == beforeFault.SystemGpu.ObservedAt,
                "injected native GPU query failure retains the previous value and original observation time");
            Check(fault.SystemMemoryUsed.Health == SampleHealth.Fresh && fault.SystemCpu.Health == SampleHealth.Fresh,
                "a GPU query failure does not stop CPU or system-memory sampling");
            provider.ResetBaseline();
            _ = await provider.ReadAsync();
            await Task.Delay(1100);
            var afterFault = await provider.ReadAsync();
            Check(afterFault.SystemGpu.Value is >= 0 and <= 100 && afterFault.SystemGpu.Health is SampleHealth.Fresh or SampleHealth.Partial,
                "GPU native query sampling recovers after an explicit ResetBaseline");
            report["nativeQueryFaultInjection"] = new
            {
                method = "Disposed the test provider's own PDH query; no driver settings changed.",
                oldObservedAtUtc = beforeFault.SystemGpu.ObservedAt,
                failureObservedAtUtc = fault.SystemGpu.ObservedAt,
                failureHealth = fault.SystemGpu.Health.ToString(),
                recoveryObservedAtUtc = afterFault.SystemGpu.ObservedAt,
                recoveryHealth = afterFault.SystemGpu.Health.ToString(),
                recoveryMethod = "Explicit ResetBaseline; automatic 15-second retry was not independently tested.",
            };

            // One CPU-bound worker for at most two seconds. Background machine activity makes total CPU non-causal evidence.
            var beforeLoad = await provider.ReadAsync();
            using var currentProcess = Process.GetCurrentProcess();
            var beforeProcessCpu = currentProcess.TotalProcessorTime;
            var loadClock = Stopwatch.StartNew();
            var load = Task.Run(() =>
            {
                var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
                var value = 1d;
                while (Stopwatch.GetTimestamp() < deadline)
                    for (var iteration = 0; iteration < 128; iteration++) value = Math.Sqrt(value + 1.23456789);
                GC.KeepAlive(value);
            });
            await Task.Delay(650);
            var duringFirst = await provider.ReadAsync();
            await Task.Delay(650);
            var duringSecond = await provider.ReadAsync();
            await load;
            loadClock.Stop();
            var processCpuSeconds = (currentProcess.TotalProcessorTime - beforeProcessCpu).TotalSeconds;
            await Task.Delay(1100);
            var afterLoad = await provider.ReadAsync();
            Check(processCpuSeconds > 0.1, "the bounded single-worker CPU exercise consumed measurable CPU time");
            Check(new[] { beforeLoad, duringFirst, duringSecond, afterLoad }.All(sample => sample.SystemCpu.Value is >= 0 and <= 100),
                "CPU remains available and bounded before, during and after the local workload");
            report["boundedCpuLoad"] = new
            {
                workerCount = 1,
                workerDeadlineSeconds = 2,
                observationElapsedSeconds = loadClock.Elapsed.TotalSeconds,
                testProcessCpuSeconds = processCpuSeconds,
                systemCpuBeforePercent = beforeLoad.SystemCpu.Value,
                systemCpuDuringPercent = new[] { duringFirst.SystemCpu.Value, duringSecond.SystemCpu.Value },
                systemCpuAfterPercent = afterLoad.SystemCpu.Value,
                expectedSingleLogicalProcessorMachineSharePercent = 100d / Environment.ProcessorCount,
                interpretation = "System CPU values are descriptive only. No assertion requires an increase because other workloads are active.",
            };
            capturedNativeHandles = CaptureNativeHandles(provider);
        }
        Check(capturedNativeHandles.Count > 0 && capturedNativeHandles.All(handle => handle.IsClosed),
            "disposing the provider closes all captured process and PDH safe handles");
        report["disposedNativeHandleCount"] = capturedNativeHandles.Count;

        // Observe the real service separately; service/account availability is not a hardware assertion.
        try
        {
            await using var realQuota = new QuotaProvider();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var requestClock = Stopwatch.StartNew();
            var pending = realQuota.ReadAsync(deadline.Token);
            var ownedAtStart = realQuota.OwnedProcessId.HasValue;
            var result = await pending;
            report["realQuotaServiceObservation"] = new
            {
                executableLocated = CodexLocator.ResolveExecutable() is not null,
                ownedProcessObservedAtRequestStart = ownedAtStart,
                quotaHealth = result.Health.ToString(),
                safeMessage = result.Message,
                resolvedHomeExists = Directory.Exists(CodexLocator.ResolveHome()),
                requestElapsedMilliseconds = requestClock.Elapsed.TotalMilliseconds,
                externalDeadlineSeconds = 25,
                providerInternalDeadlineSeconds = 20,
                externalDeadlineExpired = deadline.IsCancellationRequested,
                providerReportedTimeout = result.Message?.Contains("超时", StringComparison.Ordinal) == true,
                ownedProcessRemainedAfterRead = realQuota.OwnedProcessId.HasValue,
                interpretation = "This is an external-service observation, not proof of successful live quota retrieval or of live-PID exclusion.",
            };
        }
        catch (Exception error)
        {
            report["realQuotaServiceObservation"] = new { quotaHealth = "Unavailable", failureType = error.GetType().Name };
        }

        // The existing fixture is a real, long-lived Windows child process controlled through production QuotaProvider.
        // Its deterministic protocol response does not represent account quota data or the real Codex backend.
        var ownedQuotaSeen = false;
        var quotaExcluded = false;
        var selfExcluded = false;
        var quotaHealth = "not-read";
        try
        {
            var fixtureExecutable = Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly()!.Location) + ".exe");
            await using var quota = new QuotaProvider(fixtureExecutable);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var quotaSnapshot = await quota.ReadAsync(deadline.Token);
            quotaHealth = quotaSnapshot.Health.ToString(); // no account windows or percentages are recorded
            var ownedPid = quota.OwnedProcessId;
            ownedQuotaSeen = ownedPid is > 0;
            var aliveBeforeSampling = ownedPid.HasValue && ProcessAlive(ownedPid.Value);
            using var combined = new HardwareProvider(() => quota.OwnedProcessId);
            var combinedFirst = await combined.ReadAsync();
            await Task.Delay(1100);
            var combinedSecond = await combined.ReadAsync();
            var tracked = TrackedPids(combined);
            var aliveAfterSampling = ownedPid.HasValue && ProcessAlive(ownedPid.Value);
            selfExcluded = !tracked.Contains(Environment.ProcessId);
            quotaExcluded = ownedPid.HasValue && !tracked.Contains(ownedPid.Value);
            Check(ownedQuotaSeen && aliveBeforeSampling && aliveAfterSampling,
                "production QuotaProvider owns a real fixture process that remains alive across hardware sampling");
            Check(selfExcluded && quotaExcluded, "native production-provider combination excludes itself and its live owned quota-fixture PID");
            report["combinedProcessExclusion"] = new
            {
                actualOwnedQuotaProcessObserved = ownedQuotaSeen,
                fixtureProtocol = true,
                liveBeforeSampling = aliveBeforeSampling,
                liveAfterSampling = aliveAfterSampling,
                selfExcluded,
                ownedQuotaProcessExcluded = quotaExcluded,
                quotaHealth,
                codexPresent = combinedSecond.CodexPresent,
                trackedCodexProcessCount = tracked.Count,
                hardwareHealth = combinedSecond.Health.ToString(),
                method = "Production QuotaProvider owns a deterministic app-server fixture running as a real Windows process; test-only reflection inspects actual tracked identities after two native samples. No PID values are logged.",
            };
        }
        catch (Exception error)
        {
            Check(false, "native quota-fixture/hardware integration check could not complete within its bounded deadline");
            report["combinedProcessExclusion"] = new
            {
                actualOwnedQuotaProcessObserved = ownedQuotaSeen,
                selfExcluded,
                ownedQuotaProcessExcluded = quotaExcluded,
                quotaHealth,
                failureType = error.GetType().Name,
            };
        }

        report["notPerformed"] = new[]
        {
            "Physical GPU unplug, driver disable or driver restart.",
            "Actual Windows sleep, hibernation or resume.",
            "Independent timed validation of automatic retry after a driver failure.",
            "Systems with more than 64 logical processors or multiple processor groups.",
            "Long-duration leak/soak validation or a complete UI resource benchmark.",
            "Elevated access to otherwise unreadable Codex subprocesses.",
            "Successful quota retrieval from the real Codex backend; the native PID-exclusion integration uses a deterministic fixture.",
        };
        report["completedAtUtc"] = DateTimeOffset.UtcNow;
        report["checks"] = checks;
        report["failureCount"] = failures;
        var arguments = Environment.GetCommandLineArgs();
        var reportIndex = Array.FindIndex(arguments, argument => argument.Equals("--hardware-report", StringComparison.OrdinalIgnoreCase));
        if (reportIndex >= 0 && reportIndex + 1 < arguments.Length)
        {
            var reportPath = Path.GetFullPath(arguments[reportIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("HARDWARE_REPORT_WRITTEN");
        }
        Console.WriteLine($"HARDWARE_BOUNDED_VALIDATION: {failures} failure(s)");
        return failures;
    }

    private static object SafeSnapshot(HardwareSnapshot snapshot)
    {
        static object Metric(MetricSample value) => new { value = value.Value, observedAtUtc = value.ObservedAt, health = value.Health.ToString() };
        return new
        {
            observedAtUtc = snapshot.ObservedAt,
            gpuName = snapshot.GpuName,
            systemCpu = Metric(snapshot.SystemCpu),
            codexCpu = Metric(snapshot.CodexCpu),
            systemGpu = Metric(snapshot.SystemGpu),
            codexGpu = Metric(snapshot.CodexGpu),
            systemMemoryUsedBytes = Metric(snapshot.SystemMemoryUsed),
            systemMemoryTotalBytes = Metric(snapshot.SystemMemoryTotal),
            codexPrivateWorkingSetBytes = Metric(snapshot.CodexMemory),
            gpuDedicatedMemoryUsedBytes = Metric(snapshot.GpuMemoryUsed),
            gpuDedicatedMemoryTotalBytes = Metric(snapshot.GpuMemoryTotal),
            codexPresent = snapshot.CodexPresent,
            codexProcessCount = snapshot.CodexProcessCount,
            health = snapshot.Health.ToString(),
        };
    }

    private static System.Collections.IDictionary TrackedProcesses(HardwareProvider provider) =>
        typeof(HardwareProvider).GetField("_processes", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider)
        as System.Collections.IDictionary ?? throw new InvalidOperationException("Hardware diagnostic identity set is unavailable.");

    private static HashSet<int> TrackedPids(HardwareProvider provider) => TrackedProcesses(provider).Keys.Cast<ProcessIdentity>().Select(identity => identity.Pid).ToHashSet();

    private static bool ProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static IReadOnlyList<SafeHandle> CaptureNativeHandles(HardwareProvider provider)
    {
        var handles = new List<SafeHandle>();
        foreach (var value in TrackedProcesses(provider).Values)
            if (value?.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(value) is SafeHandle processHandle)
                handles.Add(processHandle);
        var sampler = typeof(HardwareProvider).GetField("_gpuSampler", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider);
        if (sampler?.GetType().GetField("_query", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sampler) is SafeHandle queryHandle)
            handles.Add(queryHandle);
        return handles;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();
}
