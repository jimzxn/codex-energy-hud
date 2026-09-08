using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexHud.Core;

public static class UsageLedgerTests
{
    public static int Run()
    {
        int failures = 0;
        void Check(bool valid, string name)
        {
            if (valid) return;
            failures++;
            Console.Error.WriteLine("FAIL usage ledger: " + name);
        }
        string home = Path.Combine(Path.GetTempPath(), "codex-hud-ledger-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(home, "sessions"), archived = Path.Combine(home, "archived_sessions");
        Directory.CreateDirectory(sessions); Directory.CreateDirectory(archived);
        string database = Path.Combine(home, "state_5.sqlite");
        var now = DateTimeOffset.UtcNow.AddMinutes(-2);
        try
        {
            Sql(database, "CREATE TABLE threads(id TEXT PRIMARY KEY,rollout_path TEXT,source TEXT,archived INTEGER,model TEXT,reasoning_effort TEXT,model_provider TEXT,updated_at INTEGER);");
            string AddTask(string id, string source, bool isArchived, DateTimeOffset created)
            {
                string path = Path.Combine(isArchived ? archived : sessions, id + ".jsonl");
                File.WriteAllText(path, Meta(id, created) + "\n", new UTF8Encoding(false));
                Sql(database, $"INSERT INTO threads VALUES({Q(id)},{Q(path)},{Q(source)},{(isArchived ? 1 : 0)},'gpt-6-astra','high','openai',0);");
                return path;
            }
            string root = AddTask("root", "cli", false, now.AddDays(-1));
            string agent = AddTask("agent", "subagent", false, now.AddDays(-1));
            string old = AddTask("archived", "cli", true, now.AddDays(-1));
            string missingHistory = AddTask("missing-history", "cli", true, now.AddDays(-1));
            File.Delete(missingHistory);
            Sql(database, "INSERT INTO threads VALUES('remote-history','/remote/task.jsonl','remote',1,'gpt-6-astra','high','openai',0);");
            Append(root, Usage("root", "old", "old-response", now.AddHours(-1), 999, 999));
            var provider = new UsageLedgerProvider(home, () => now);
            UsageLedgerSnapshot Read() => provider.ReadAsync().GetAwaiter().GetResult();
            var baseline = Read();
            Check(baseline.Health == SampleHealth.Fresh && baseline.Counts.TotalTokens == 0 && baseline.ObservedThreads == 3,
                "existing files start at EOF; agents and archived logs are included while unchanged unavailable history is only a baseline");
            now = now.AddSeconds(1);
            foreach (var pair in new[] { ("root", root), ("agent", agent), ("archived", old) })
            {
                Append(pair.Item2, Context("one", now), Usage(pair.Item1, "one", "r1", now, 10, pair.Item1 == "root" ? 1009 : 10));
            }
            var parallel = Read();
            Check(parallel.Health == SampleHealth.Fresh && parallel.Counts.TotalTokens == 30
                && parallel.Counts.CachedInputTokens == 30 && parallel.ModelMix == "model:gpt-6-astra/effort:high/tier:?/provider:openai",
                "parallel, agent and archived response increments count exactly once with actual model metadata");
            now = now.AddSeconds(1);
            Append(root, Usage("root", "one", "r2", now, 20, 1029));
            Check(Read().Counts.TotalTokens == 50, "thread cumulative delta agrees with response usage");
            Append(root, Usage("root", "one", "r2", now, 200, 99999),
                Usage("parent", "one", "inherited", now, 900, 900),
                JsonSerializer.Serialize(new { timestamp = now, type = "compacted", payload = new { token_usage_record = "not a top-level usage record" } }));
            Check(Read().Counts.TotalTokens == 50, "duplicate response, copied parent and nested usage are rejected");

            now = now.AddSeconds(1);
            string child = AddTask("new-child", "subagent", false, now);
            Append(child, Usage("parent", "one", "inherited", now.AddHours(-1), 5000, 5000),
                Context("one", now), Usage("new-child", "one", "new-response", now, 30, 30));
            var newChild = Read();
            Check(newChild.Health == SampleHealth.Fresh && newChild.Counts.TotalTokens == 80 && newChild.ObservedThreads == 4,
                "new session identity and timestamp prove post-baseline usage without fork inheritance");

            now = now.AddSeconds(1);
            string discovered = AddTask("late-discovered", "cli", false, now.AddHours(-1));
            Append(discovered, Context("one", now), Usage("late-discovered", "one", "pre-discovery", now, 9000, 9000));
            var discovery = Read();
            Check(discovery.Health == SampleHealth.Partial && discovery.Counts.TotalTokens == 0 && discovery.Epoch != newChild.Epoch,
                "newly discovered old file only creates a new baseline instead of adding history");
            Check(Read().Health == SampleHealth.Fresh, "complete discovery baseline can resume calibration");

            now = now.AddSeconds(1);
            Append(root, JsonSerializer.Serialize(new
            {
                timestamp = now, type = "token_usage_record",
                payload = new { thread_id = "root", turn_id = "one", response_id = "missing", usage = new { total_tokens = 1 } }
            }));
            var missing = Read();
            Check(missing.Health == SampleHealth.Partial && missing.Epoch != discovery.Epoch && missing.Counts.TotalTokens == 0,
                "missing numeric fields restart the epoch rather than silently count unknown usage as zero");
            Read();
            now = now.AddSeconds(1);
            Append(root, Usage("root", "one", "valid-after-missing", now, 10, 1040));
            var recovered = Read();
            Check(recovered.Health == SampleHealth.Fresh && recovered.Counts.TotalTokens == 10,
                "new complete response recovers after a missing record with a fresh epoch");

            using (var locked = new FileStream(root, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(Read().Health == SampleHealth.Partial, "sharing violation pauses the complete-source claim");
            var unlocked = Read();
            Check(unlocked.Health == SampleHealth.Partial && unlocked.Epoch != recovered.Epoch,
                "same-file recovery resets the calibration boundary after unavailable sampling");
            Check(Read().Health == SampleHealth.Fresh, "unchanged recovered file is a valid subsequent baseline");

            now = now.AddSeconds(1);
            string replacement = Path.Combine(sessions, "replacement.tmp");
            File.WriteAllText(replacement, Meta("root", now) + "\n" + Usage("root", "two", "replacement-r1", now, 5, 5) + "\n",
                new UTF8Encoding(false));
            File.Move(replacement, root, true);
            var rotated = Read();
            Check(rotated.Health == SampleHealth.Partial && rotated.Epoch != unlocked.Epoch && rotated.Counts.TotalTokens == 0,
                "file identity rotation drops inherited numeric state and establishes EOF baseline");
            Read();
            now = now.AddSeconds(1);
            Append(root, Context("two", now), Usage("root", "two", "replacement-r2", now, 5, 10));
            Check(Read().Counts.TotalTokens == 5, "new response after replacement is counted without previous file totals");

            now = now.AddSeconds(1);
            Append(root, Usage("root", "two", "regression", now, 1, 1));
            Check(Read().Health == SampleHealth.Partial, "cumulative counter regression pauses calibration");
            Read();
            now = now.AddSeconds(1);
            Append(root, Context("three", now, "gpt-6-astra", "priority"), Usage("root", "three", "tier-change", now, 1, 2));
            Check(Read().Health == SampleHealth.Partial, "actual service-tier change restarts the usage epoch");

            now = now.AddSeconds(1);
            Append(root, Context("spark", now, "gpt-5.3-codex-spark"), Usage("root", "spark", "spark-r1", now, 1, 3));
            var spark = Read();
            Check(spark.Health == SampleHealth.Partial && spark.Detail?.Contains("额度池") == true,
                "separate model pool without a limit ID cannot pollute the main-pool calibration");
            now = now.AddMinutes(11);
            Check(Read().Health == SampleHealth.Fresh, "ambiguous model activity expires only after a fresh observation window");

            string namedUnmapped = AddTask("unmapped-model", "cli", false, now);
            Append(namedUnmapped, Context("one", now, "named-custom-model"), Usage("unmapped-model", "one", "unmapped-r1", now, 1, 1));
            Check(Read().Health == SampleHealth.Partial, "nonempty model name alone does not establish the Codex quota pool");
            now = now.AddMinutes(11);
            Read();
            string external = AddTask("external-provider", "cli", false, now);
            Sql(database, "UPDATE threads SET model_provider='external' WHERE id='external-provider';");
            Append(external, Context("one", now), Usage("external-provider", "one", "external-r1", now, 1, 1));
            Check(Read().Health == SampleHealth.Partial, "known model name under an external provider cannot enter the main pool");
            now = now.AddMinutes(11);
            Read();

            string longRunning = AddTask("long-running", "cli", false, now.AddDays(-1));
            Append(longRunning, Context("long", now.AddHours(-1)),
                JsonSerializer.Serialize(new { type = "response_item", payload = new { test_padding = new string('x', 48 * 1024) } }),
                Usage("long-running", "long", "before-start", now.AddSeconds(-1), 120, 120));
            var longProvider = new UsageLedgerProvider(home, () => now);
            var longBaseline = longProvider.ReadAsync().GetAwaiter().GetResult();
            now = now.AddSeconds(1);
            Append(longRunning, Usage("long-running", "long", "same-turn-after-start", now, 10, 130));
            var longIncrement = longProvider.ReadAsync().GetAwaiter().GetResult();
            Check(longBaseline.Counts.TotalTokens == 0 && longIncrement.Health == SampleHealth.Fresh
                && longIncrement.Counts.TotalTokens == 10 && longIncrement.ModelMix?.Contains("model:gpt-6-astra") == true,
                "running long turn without context in bounded tail uses actual index model and only new response usage");
            // The original provider discovers this old file as a new baseline, never as a historical increment.
            Read();
            Read();

            File.WriteAllText(missingHistory, Meta("missing-history", now) + "\n", new UTF8Encoding(false));
            Check(Read().Health == SampleHealth.Partial, "previously unavailable history appearing later establishes a fresh baseline");
            Read();

            File.AppendAllText(agent, new string('x', UsageLedgerCursor.MaximumRead + 10) + "\n", new UTF8Encoding(false));
            var oversized = Read();
            Check(oversized.Health == SampleHealth.Partial && oversized.Epoch != spark.Epoch,
                "oversized skipped records mark a new epoch instead of claiming complete capture");
            Read();
            File.Delete(old);
            Check(Read().Health == SampleHealth.Partial, "missing archived log keeps coverage incomplete");
            File.WriteAllText(old, Meta("archived", now) + "\n", new UTF8Encoding(false));
            Check(Read().Health == SampleHealth.Partial, "restored missing file has a fresh identity boundary");
            Read();
            File.Delete(database);
            Check(Read().Health == SampleHealth.Unavailable, "missing index preserves unknown status");

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { provider.ReadAsync(canceled.Token).GetAwaiter().GetResult(); Check(false, "cancellation propagates"); }
            catch (OperationCanceledException) { }
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine("FAIL usage ledger fixture: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
        return failures;
    }

    public static async Task<int> LiveAsync()
    {
        var provider = new UsageLedgerProvider(CodexLocator.ResolveHome());
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var first = await provider.ReadAsync();
        var firstMs = timer.Elapsed.TotalMilliseconds;
        await Task.Delay(5000);
        timer.Restart();
        var second = await provider.ReadAsync();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            check = "live-local-usage-ledger", observedAt = second.ObservedAt,
            firstHealth = first.Health.ToString(), health = second.Health.ToString(),
            observedThreads = second.ObservedThreads, epochChanged = first.Epoch != second.Epoch,
            first.Counts, nextCounts = second.Counts, second.ModelMix, second.Detail,
            initialReadMs = firstMs, incrementalReadMs = timer.Elapsed.TotalMilliseconds
        }));
        return second.Health == SampleHealth.Unavailable ? 1 : 0;
    }

    private static void Append(string path, params string[] lines) =>
        File.AppendAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
    private static string Meta(string id, DateTimeOffset at) => JsonSerializer.Serialize(new
        { timestamp = at, type = "session_meta", payload = new { id } });
    private static string Context(string turn, DateTimeOffset at, string model = "gpt-6-astra", string? tier = null) =>
        JsonSerializer.Serialize(new { timestamp = at, type = "turn_context", payload = new { turn_id = turn, model, effort = "high", service_tier = tier } });
    private static string Usage(string thread, string turn, string response, DateTimeOffset at, long usage, long cumulative) =>
        JsonSerializer.Serialize(new
        {
            timestamp = at, ordinal = (long?)null, type = "token_usage_record",
            payload = new { thread_id = thread, turn_id = turn, response_id = response, usage = Counts(usage), thread_token_usage = Counts(cumulative) }
        });
    private static object Counts(long total) => new
    {
        input_tokens = total, cached_input_tokens = total, cache_write_input_tokens = 0,
        output_tokens = 0, reasoning_output_tokens = 0, total_tokens = total
    };
    private static string Q(string value) => "'" + value.Replace("'", "''") + "'";

    private static void Sql(string path, string sql)
    {
        int code = sqlite3_open_v2(path, out var database, 0x00000002 | 0x00000004, IntPtr.Zero);
        if (code != 0) throw new IOException("Ledger SQLite test open failed.");
        try
        {
            code = sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, out var error);
            if (error != IntPtr.Zero) sqlite3_free(error);
            if (code != 0) throw new IOException("Ledger SQLite test command failed (" + code + ").");
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
