using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexHud.Core;

public static class WorkloadUsageTests
{
    public static int Run()
    {
        int failures = 0, checks = 0;
        void Check(bool valid, string scenario)
        {
            checks++;
            if (valid) return;
            failures++;
            Console.Error.WriteLine("FAIL workload: " + scenario);
        }
        using var fixture = new Fixture();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string root = fixture.Add("root", now.AddDays(-1));
        string child = fixture.Add("child", now.AddDays(-1), subagent: true, model: "gpt-5.3-codex-spark");
        string archived = fixture.Add("archived", now.AddDays(-1), archived: true, model: "custom-local-model", provider: "custom");
        string review = fixture.Add("review", now.AddDays(-1), review: true);
        Append(root, Usage("root", "old", now.AddSeconds(-1), 1000, 800, 200, 1000, 800, 200));
        var provider = new WorkloadUsageProvider(fixture.Home, () => now);
        WorkloadUsageSnapshot Read() => provider.ReadAsync().GetAwaiter().GetResult();
        UsageTick Latest(WorkloadUsageSnapshot value) => value.OverallHistory.Last();
        var first = Read();
        Check(first.OverallHistory.Count == 0 && first.Tasks.Count == 3 && first.Health == SampleHealth.Partial,
            "startup excludes historical totals and guardian review");
        Check(first.Tasks.Single(task => task.ThreadId == "child").IsSubagent
            && first.Tasks.Single(task => task.ThreadId == "archived").IsArchived, "subagent and archived metadata retained");
        now = now.AddMilliseconds(250);
        Append(root, Usage("root", "r1", now, 100, 80, 20, 1100, 880, 220));
        Append(child, Usage("child", "c1", now, 30, 25, 5, 30, 25, 5));
        Append(archived, Usage("archived", "a1", now, 20, 10, 5, 20, 10, 5));
        Append(review, Usage("review", "g1", now, 999999, 99999, 999, 999999, 99999, 999));
        Check(Read().OverallHistory.Count == 0, "open second is not shown as a completed tick");
        Read();
        now = now.AddMilliseconds(750);
        var completed = Read();
        var counts = Latest(completed).Counts;
        Check(counts?.InputTokens == 150 && counts.CachedInputTokens == 115 && counts.OutputTokens == 30
            && counts.TotalTokens == 180, "all local models count without double-counting cached input");
        Check(completed.Tasks.Sum(task => task.History.LastOrDefault()?.Counts?.TotalTokens ?? 0) == 180,
            "task identities sum to aggregate without parent-child duplication");
        var unchanged = Read();
        Check(Latest(unchanged) == Latest(completed) && unchanged.FilesRead == 0,
            "snapshot is non-consuming and unchanged files skip full reads");
        Append(root, Usage("root", "r1", now, 100, 80, 20, 1100, 880, 220),
            Usage("parent", "inherited", now, 900, 800, 100, 900, 800, 100));
        Read();
        now = now.AddSeconds(1);
        Check(Latest(Read()).Counts?.TotalTokens == 0, "duplicates and inherited parent records are not workload");
        string partial = Usage("root", "r2", now, 10, 8, 2, 1110, 888, 222);
        File.AppendAllText(root, partial[..(partial.Length / 2)]);
        Check(Read().BacklogFiles == 1, "incomplete tail is pending");
        now = now.AddSeconds(1);
        var missing = Latest(Read());
        Check(missing.Health == SampleHealth.Partial && missing.Counts is null, "incomplete empty seconds are gaps");
        File.AppendAllText(root, partial[(partial.Length / 2)..] + "\n");
        Read();
        now = now.AddSeconds(1);
        Check(Latest(Read()).Counts?.TotalTokens == 12, "completed partial record counts at arrival time");
        Check(completed.OverallHistory.Single().Counts?.TotalTokens == 180, "returned snapshots remain immutable");
        now = now.AddSeconds(1);
        string added = fixture.Add("new-child", now, subagent: true);
        Append(added, Usage("new-child", "n1", now, 40, 30, 10, 40, 30, 10));
        Read();
        now = now.AddSeconds(1);
        Check(Latest(Read()).Counts?.TotalTokens == 50, "new sessions include post-start usage");
        now = now.AddSeconds(3);
        Check(Read().OverallHistory.TakeLast(3).All(tick => tick.Health == SampleHealth.Partial),
            "missed sampling seconds are unknown");
        now = now.AddSeconds(1);
        string late = fixture.Add("late-old", now.AddDays(-1));
        Append(late, Usage("late-old", "late", now.AddSeconds(-2), 9000, 8000, 1000, 9000, 8000, 1000));
        Read();
        now = now.AddSeconds(1);
        var lateBucket = Latest(Read());
        Check(lateBucket.Health == SampleHealth.Partial && lateBucket.Counts is null,
            "late old logs create a baseline without historical burst");
        string replacement = root + ".replacement";
        File.WriteAllText(replacement, Meta("root", now) + "\n" + Usage("root", "replaced", now, 100, 80, 20, 100, 80, 20) + "\n");
        File.Move(replacement, root, true);
        Read();
        now = now.AddSeconds(1);
        Check(Latest(Read()).Health == SampleHealth.Partial, "replacement produces a gap");
        Append(root, Usage("root", "replacement-next", now, 5, 4, 1, 105, 84, 21));
        Read();
        now = now.AddSeconds(1);
        Check(Latest(Read()).Counts?.TotalTokens == 6, "replacement baseline accepts subsequent usage");
        fixture.Remove("archived");
        now = now.AddSeconds(2);
        var retired = Read();
        Check(retired.Tasks.Any(task => task.ThreadId == "archived")
            && retired.OverallHistory.Sum(tick => tick.Counts?.TotalTokens ?? 0)
                == retired.Tasks.Sum(task => task.History.Sum(tick => tick.Counts?.TotalTokens ?? 0)),
            "recently removed task metadata remains beside its aggregate contribution");
        for (int second = 0; second < 65; second++) { now = now.AddSeconds(1); Read(); }
        var bounded = Read();
        Check(bounded.OverallHistory.Count == 60
            && bounded.OverallHistory.Last().TickStart - bounded.OverallHistory.First().TickStart == TimeSpan.FromSeconds(59),
            "history retains 60 consecutive completed seconds");
        Check(bounded.Tasks.All(task => task.History.Last().Counts?.TotalTokens == 0), "idle tasks retain authoritative zero histories");
        Check(ReferenceEquals(bounded.Tasks.First(task => task.ThreadId == "root").History, bounded.Tasks.First(task => task.ThreadId == "child").History), "idle tasks share the same zero-history allocation");
        now = now.AddHours(12);
        Check(Read().OverallHistory.All(tick => tick.Health == SampleHealth.Partial), "long suspend remains bounded and unknown");
        using var missingFixture = new Fixture();
        string missingFile = missingFixture.Add("missing-history", now.AddDays(-1), archived: true);
        File.Delete(missingFile);
        var missingProvider = new WorkloadUsageProvider(missingFixture.Home, () => now);
        missingProvider.ReadAsync().GetAwaiter().GetResult();
        now = now.AddSeconds(1);
        var historicalMissing = missingProvider.ReadAsync().GetAwaiter().GetResult();
        Check(historicalMissing.Tasks.Single().History.Last().Counts is null,
            "historically unreadable task counters remain unknown rather than zero");
        missingFixture.Touch("missing-history");
        now = now.AddSeconds(5);
        Check(missingProvider.ReadAsync().GetAwaiter().GetResult().Health == SampleHealth.Partial,
            "updated missing history becomes a current coverage gap");
        using var busy = new Fixture();
        string busyA = busy.Add("a", now.AddDays(-1)), busyB = busy.Add("b", now.AddDays(-1));
        var small = new WorkloadUsageProvider(busy.Home, () => now, readBudget: 1024);
        small.ReadAsync().GetAwaiter().GetResult();
        File.AppendAllText(busyA, JsonSerializer.Serialize(new { type = "response_item", payload = new string('x', 2500) }) + "\n");
        Append(busyB, Usage("b", "b1", now, 10, 8, 2, 10, 8, 2));
        Check(small.ReadAsync().GetAwaiter().GetResult().ReadBytes <= 1024, "global byte budget is bounded");
        now = now.AddSeconds(1);
        small.ReadAsync().GetAwaiter().GetResult();
        now = now.AddSeconds(1);
        var fair = small.ReadAsync().GetAwaiter().GetResult();
        Check(fair.Tasks.Single(task => task.ThreadId == "b").History.Any(tick => tick.Counts?.TotalTokens == 12),
            "round robin prevents a busy file starving another task");
        Console.WriteLine($"Workload usage: {checks} checks, {failures} failure(s)");
        return failures;
    }

    public static async Task<int> LiveAsync(string[] args)
    {
        string? Value(string option) { int i = Array.IndexOf(args, option); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        int seconds = int.TryParse(Value("--seconds"), out int value) ? Math.Clamp(value, 1, 7200) : 600;
        string? output = Value("--output");
        var provider = new WorkloadUsageProvider(CodexLocator.ResolveHome());
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var elapsed = Stopwatch.StartNew();
        var samples = new List<object>();
        var durations = new List<double>();
        int maxBacklog = 0, unavailable = 0, maxHistory = 0;
        long peakWorkingSet = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            var watch = Stopwatch.StartNew();
            var snapshot = await provider.ReadAsync();
            durations.Add(watch.Elapsed.TotalMilliseconds);
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            maxBacklog = Math.Max(maxBacklog, snapshot.BacklogFiles);
            maxHistory = Math.Max(maxHistory, snapshot.OverallHistory.Count);
            if (snapshot.Health == SampleHealth.Unavailable) unavailable++;
            samples.Add(new { elapsedSeconds = elapsed.Elapsed.TotalSeconds, snapshot.ObservedAt,
                health = snapshot.Health.ToString(), snapshot.IndexedThreads, snapshot.FilesChecked,
                snapshot.FilesRead, snapshot.ReadBytes, snapshot.BacklogFiles, snapshot.IndexDiscoveryPerformed,
                historyCount = snapshot.OverallHistory.Count, lastCompleted = snapshot.OverallHistory.LastOrDefault(),
                readMs = watch.Elapsed.TotalMilliseconds, workingSetBytes = process.WorkingSet64, managedBytes = GC.GetTotalMemory(false) });
        } while (elapsed.Elapsed.TotalSeconds < seconds && await timer.WaitForNextTickAsync());
        var sorted = durations.Order().ToArray();
        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)];
        var report = new { check = "live-local-workload", requestedSeconds = seconds,
            effectiveSeconds = elapsed.Elapsed.TotalSeconds, sampleCount = samples.Count,
            cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds, peakWorkingSetBytes = peakWorkingSet,
            readP50Ms = Percentile(.50), readP95Ms = Percentile(.95), readMaxMs = sorted[^1],
            maxBacklogFiles = maxBacklog, maxHistoryCount = maxHistory, unavailableSamples = unavailable,
            passed = elapsed.Elapsed.TotalSeconds >= seconds && unavailable == 0 && maxHistory <= 60, samples };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (output is not null)
        {
            string path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);
            Console.WriteLine(JsonSerializer.Serialize(new { report.check, report.effectiveSeconds, report.sampleCount,
                report.cpuSeconds, report.peakWorkingSetBytes, report.readP95Ms, report.readMaxMs,
                report.maxBacklogFiles, report.unavailableSamples, report.passed, output = path }));
        }
        else Console.WriteLine(json);
        return report.passed ? 0 : 1;
    }

    private static string Meta(string id, DateTimeOffset at) => JsonSerializer.Serialize(new { timestamp = at, type = "session_meta", payload = new { id } });
    private static object Counts(long input, long cache, long output) => new { input_tokens = input,
        cached_input_tokens = cache, cache_write_input_tokens = 0, output_tokens = output,
        reasoning_output_tokens = 0, total_tokens = input + output };
    private static string Usage(string id, string response, DateTimeOffset at, long input, long cache, long output,
        long cumulativeInput, long cumulativeCache, long cumulativeOutput) => JsonSerializer.Serialize(new
        { timestamp = at, type = "token_usage_record", payload = new { thread_id = id, turn_id = "turn", response_id = response,
            usage = Counts(input, cache, output), thread_token_usage = Counts(cumulativeInput, cumulativeCache, cumulativeOutput) } });
    private static void Append(string path, params string[] lines) => File.AppendAllText(path, string.Join('\n', lines) + "\n");
    private static string Q(string text) => "'" + text.Replace("'", "''") + "'";
    private sealed class Fixture : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "codex-hud-workload-" + Guid.NewGuid().ToString("N"));
        private string Database => Path.Combine(Home, "state_5.sqlite");
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Home, "sessions"));
            Directory.CreateDirectory(Path.Combine(Home, "archived_sessions"));
            Sql(Database, "CREATE TABLE threads(id TEXT PRIMARY KEY,rollout_path TEXT,title TEXT,name TEXT,source TEXT,thread_source TEXT,archived INTEGER,model TEXT,reasoning_effort TEXT,model_provider TEXT,updated_at INTEGER);");
        }
        public string Add(string id, DateTimeOffset created, bool subagent = false, bool archived = false,
            bool review = false, string model = "gpt-6-astra", string provider = "openai")
        {
            string path = Path.Combine(Home, archived ? "archived_sessions" : "sessions", id + ".jsonl");
            File.WriteAllText(path, Meta(id, created) + "\n");
            Sql(Database, $"INSERT INTO threads VALUES({Q(id)},{Q(path)},{Q(id)},NULL,{Q(subagent ? "subagent" : "cli")},{Q(review ? "guardian_review" : subagent ? "subagent" : "cli")},{(archived ? 1 : 0)},{Q(model)},'high',{Q(provider)},0);");
            return path;
        }
        public void Remove(string id) => Sql(Database, "DELETE FROM threads WHERE id=" + Q(id));
        public void Touch(string id) => Sql(Database, "UPDATE threads SET updated_at=updated_at+1 WHERE id=" + Q(id));
        public void Dispose() => Directory.Delete(Home, true);
    }
    private static void Sql(string path, string sql)
    {
        int code = sqlite3_open_v2(path, out var database, 0x00000002 | 0x00000004, IntPtr.Zero);
        if (code != 0) throw new IOException("Workload test SQLite open failed.");
        try
        {
            code = sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, out var error);
            if (error != IntPtr.Zero) sqlite3_free(error);
            if (code != 0) throw new IOException("Workload test SQLite command failed: " + code);
        }
        finally { sqlite3_close_v2(database); }
    }
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr context, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr database);
}
