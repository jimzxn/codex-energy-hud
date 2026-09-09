using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHud.Core;

public static class UsageHistoryTests
{
    public static int Run()
    {
        int checks = 0, failures = 0;
        void Check(bool condition, string description)
        {
            checks++;
            if (!condition) { failures++; Console.Error.WriteLine("FAIL usage history: " + description); }
        }
        void Scenario(string description, Action action)
        {
            try { action(); }
            catch (Exception error) { failures++; Console.Error.WriteLine("FAIL usage history " + description + ": " + error); }
        }
        Scenario("complete ledger and immutable snapshots", () =>
        {
            using var fixture = new Fixture();
            fixture.Add("root", "root-turn");
            fixture.Add("other", "other-turn");
            fixture.Add("child", "child-turn", parent: "root", archived: true);
            fixture.Add("guardian", "guardian-turn", parent: "root", review: true);
            using var provider = new SessionCostProvider(fixture.Home);
            provider.SetTrackedThreads(["root"]);
            var snapshot = Read(provider);
            var ledger = snapshot.Ledger!;
            Check(snapshot.Tasks.Count == 2 && !snapshot.Tasks.ContainsKey("other"), "legacy projection contains tracked root and descendants only");
            Check(ledger.Tasks.Count == 3 && ledger.Responses.Count == 3 && ledger.Tasks.ContainsKey("other"),
                "complete ledger includes untracked tasks and excludes guardian reviews");
            Check(ledger.Tasks["child"].Archived && ledger.Tasks["child"].ParentThreadId == "root", "archived child metadata retained");
            Check(ReferenceEquals(ledger.Responses, Read(provider).Ledger!.Responses), "unchanged response ledger reuses its immutable projection");
            bool immutable = false;
            try { ((IList<LocalUsageResponse>)ledger.Responses).Clear(); } catch (NotSupportedException) { immutable = true; }
            Check(immutable, "public response collection cannot mutate provider accounting");
            fixture.AppendUsage("root", "root-turn", "next", cumulative: 2);
            var newer = Read(provider).Ledger!;
            Check(ledger.Responses.Count == 3 && newer.Responses.Count == 4, "new appends cannot mutate an earlier returned snapshot");
        });
        Scenario("inherited lifecycle requires exact owned turn evidence", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root", "root-turn", usage: false);
            Append(root, fixture.Event("task_started", "root-turn", 0), fixture.Usage("root", "root-turn", "r1", 10),
                fixture.Event("task_complete", "root-turn", 20));
            string child = fixture.Add("child", "child-turn", parent: "root", usage: false);
            Append(child, fixture.Meta("root"), fixture.Event("task_started", "root-turn", 100),
                fixture.Context("root-turn"), fixture.Event("task_complete", "root-turn", 120),
                fixture.Event("task_started", "child-turn", 30), fixture.Context("child-turn"),
                fixture.Usage("child", "child-turn", "c1", 40), fixture.Event("task_complete", "child-turn", 50));
            using var provider = new SessionCostProvider(fixture.Home);
            var turns = Read(provider).Ledger!.Turns;
            Check(turns.Count == 2 && !turns.Any(turn => turn.ThreadId == "child" && turn.TurnId == "root-turn"),
                "retimestamped copied parent lifecycle is not a child turn");
            var own = turns.Single(turn => turn.ThreadId == "child");
            Check(own.StartedAt == fixture.At.AddSeconds(30) && own.EndedAt == fixture.At.AddSeconds(50),
                "own response identity corroborates anonymous lifecycle endpoints");
            Check(turns.Single(turn => turn.ThreadId == "root").EndedAt == fixture.At.AddSeconds(20),
                "copied parent completion cannot extend the original parent's elapsed time");
        });
        Scenario("same-owner replay and restart preserve duration", () =>
        {
            using var fixture = new Fixture();
            string path = fixture.Add("root", "one", usage: false);
            Append(path, fixture.Event("task_started", "one", 0), fixture.Usage("root", "one", "r1", 10), fixture.Event("task_complete", "one", 20));
            string copy = fixture.Add("root", "one", suffix: "copy", usage: false);
            Append(copy, fixture.Event("task_started", "one", 100), fixture.Usage("root", "one", "r1", 110), fixture.Event("task_complete", "one", 120));
            string checkpoint = Path.Combine(fixture.Home, "ledger.json");
            using (var provider = new SessionCostProvider(fixture.Home, checkpoint))
            {
                var ledger = Read(provider).Ledger!;
                Check(ledger.Responses.Count == 1 && ledger.Turns.Count == 1, "same owned turn and response are deduplicated across files");
                Check(ledger.Responses[0].RecordedAt == fixture.At.AddSeconds(10), "response period attribution uses its earliest canonical timestamp even when a replay is scanned first");
                Check(ledger.Turns[0].StartedAt == fixture.At && ledger.Turns[0].EndedAt == fixture.At.AddSeconds(20),
                    "earliest coherent lifecycle survives a later replay");
            }
            using var resumed = new SessionCostProvider(fixture.Home, checkpoint);
            var restored = Read(resumed).Ledger!;
            Check(restored.Responses.Count == 1 && restored.Turns[0].StartedAt == fixture.At,
                "v2 checkpoint persists confirmed turn timing and response identity");
        });
        Scenario("budget exhaustion cannot certify stale subtotals as complete", () =>
        {
            using var fixture = new Fixture();
            fixture.Add("root", "one");
            using var provider = new SessionCostProvider(fixture.Home, bytesPerRead: 256);
            Check(Read(provider).Ledger!.Responses.Count == 1, "initial ordinary task catches up within the bounded scanner");
            fixture.AppendUsage("root", "one", "r2", cumulative: 2);
            File.WriteAllText(Path.Combine(fixture.Home, "sessions", "broken.jsonl"), new string('x', 65000) + "\n", Utf8);
            var starved = provider.ReadAsync().GetAwaiter().GetResult().Ledger!;
            Check(starved.Tasks["root"].Health != SampleHealth.Fresh && starved.Health != SampleHealth.Fresh,
                "new unread bytes mark an earlier completed cursor incomplete even when discovery consumes this poll's budget");
            var caughtUp = Read(provider).Ledger!;
            Check(caughtUp.Responses.Count == 2, "a stable malformed header does not repeatedly starve valid appended usage");
            Check(caughtUp.Health != SampleHealth.Fresh, "unresolved malformed discovery remains disclosed after valid tasks catch up");
        });
        Scenario("recent cold backfill and live appends share budget fairly", () =>
        {
            using var fixture = new Fixture();
            fixture.Add("live", "one");
            using var provider = new SessionCostProvider(fixture.Home, bytesPerRead: 4096);
            Read(provider);
            string filler = JsonSerializer.Serialize(new { type = "response_item", payload = new { content = new string('x', 64 * 1024) } });
            for (int index = 0; index < 100; index++)
            {
                string archived = fixture.Add("old-" + index, "one", archived: true);
                Append(archived, filler);
                File.SetLastWriteTimeUtc(archived, fixture.At.AddMinutes(-index - 1).UtcDateTime);
            }
            string recent = fixture.Add("recent", "one", usage: false);
            Append(recent, JsonSerializer.Serialize(new { type = "response_item", payload = new { content = new string('x', 24 * 1024) } }),
                fixture.Usage("recent", "one", "recent-response", 10));
            File.SetLastWriteTimeUtc(recent, fixture.At.AddHours(1).UtcDateTime);
            // A permanently unfinished JSON tail is physically caught up and must not repeatedly
            // take the newest-file priority slot while other recent logs still have unread bytes.
            string unfinished = fixture.Add("unfinished", "one", usage: false);
            File.AppendAllText(unfinished, "{", Utf8);
            File.SetLastWriteTimeUtc(unfinished, DateTime.UtcNow.AddMinutes(5));
            var discovery = provider.ReadAsync().GetAwaiter().GetResult().Ledger!;
            Check(discovery.Responses.Count == 1, "a poll consumed entirely by metadata discovery cannot exceed the body-read budget");
            LocalUsageLedgerSnapshot ledger = discovery;
            for (int index = 1; index <= 64; index++)
            {
                fixture.AppendUsage("live", "one", "live-" + index, index + 1);
                ledger = provider.ReadAsync().GetAwaiter().GetResult().Ledger!;
            }
            Check(ledger.Responses.Any(response => response.ResponseId == "recent-response"),
                "newest unread task reaches its response before a large older backfill round finishes");
            Check(ledger.Responses.Count(response => response.ThreadId == "live") == 65,
                "completed live task keeps catching up appended responses during cold backfill");
            Check(ledger.Responses.Count(response => response.ThreadId.StartsWith("old-", StringComparison.Ordinal)) >= 10,
                "reserved recent reads cannot starve older archived tasks");
            Check(ledger.Tasks.Values.Any(task => task.Health == SampleHealth.Loading),
                "the test retains a real historical backlog rather than passing only after a complete full scan");
        });
        Scenario("v1 checkpoint upgrades without dropping counters", () =>
        {
            using var fixture = new Fixture();
            fixture.Add("root", "one");
            string checkpoint = Path.Combine(fixture.Home, "ledger.json");
            decimal prior;
            using (var provider = new SessionCostProvider(fixture.Home, checkpoint)) prior = Read(provider).Tasks["root"].SelfUsd;
            var saved = JsonNode.Parse(File.ReadAllText(checkpoint))!.AsObject();
            saved["Version"] = 1;
            foreach (var node in saved["Files"]!.AsArray())
                foreach (string field in new[] { "Title", "Archived", "Turns", "CurrentTurn" }) node!.AsObject().Remove(field);
            foreach (var thread in saved["Threads"]!.AsArray())
                foreach (var response in thread!["Responses"]!.AsObject()) response.Value!.AsObject().Remove("TurnId");
            saved["Checksum"] = "";
            saved["Checksum"] = UsageLedgerCheckpoint.Hash(JsonSerializer.Serialize(saved));
            File.WriteAllText(checkpoint, JsonSerializer.Serialize(saved), Utf8);
            using var migrated = new SessionCostProvider(fixture.Home, checkpoint, bytesPerRead: 128);
            var beforeReplay = migrated.ReadAsync().GetAwaiter().GetResult();
            Check(beforeReplay.Tasks["root"].SelfUsd == prior && beforeReplay.Ledger!.Responses.Single().TurnId == null,
                "v1 numeric evidence is available before a bounded metadata replay finishes");
            var afterReplay = Read(migrated);
            Check(afterReplay.Tasks["root"].SelfUsd == prior && afterReplay.Ledger!.Responses.Single().TurnId == "one",
                "migration enriches response turn identity without double counting");
            Check(!File.ReadAllText(checkpoint).Contains("conversation-secret", StringComparison.Ordinal), "migration cache contains no conversation bodies");
        });
        if (OperatingSystem.IsWindows()) Scenario("full indexed history takes precedence over retimestamped logs", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root", "one", archived: true, usage: false);
            Append(root, fixture.Event("task_started", "one", 100), fixture.Usage("root", "one", "r1", 110), fixture.Event("task_complete", "one", 120));
            Execute(Path.Combine(fixture.Home, "state_1.sqlite"), "CREATE TABLE threads (id TEXT,name TEXT,title TEXT,archived INTEGER,thread_source TEXT);"
                + "INSERT INTO threads VALUES ('root','Renamed task','Old title',1,'user');");
            long start = fixture.At.ToUnixTimeSeconds();
            var sql = new StringBuilder("CREATE TABLE thread_turns (thread_id TEXT,turn_id TEXT,status TEXT,started_at INTEGER,completed_at INTEGER,duration_ms INTEGER);");
            sql.Append($"INSERT INTO thread_turns VALUES ('root','one','completed',{start},{start + 20},20000);");
            for (int index = 0; index < 40; index++)
                sql.Append($"INSERT INTO thread_turns VALUES ('root','old-{index}','completed',{start - 1000 - index * 20},{start - 990 - index * 20},10000);");
            sql.Append($"INSERT INTO thread_turns VALUES ('root','derived','completed',{start + 200},NULL,15000);");
            sql.Append("INSERT INTO thread_turns VALUES ('root','unknown','completed',NULL,NULL,NULL);");
            Execute(Path.Combine(fixture.Home, "thread_history_1.sqlite"), sql.ToString());
            using var provider = new SessionCostProvider(fixture.Home);
            var ledger = Read(provider).Ledger!;
            Check(ledger.Tasks["root"].Title == "Renamed task" && ledger.Tasks["root"].Archived, "state index supplies current renamed title and archived status");
            Check(ledger.Turns.Count == 43, "history includes every archived task turn without the activity view's thirty-row cap");
            var owned = ledger.Turns.Single(turn => turn.TurnId == "one");
            Check(owned.StartedAt == fixture.At && owned.EndedAt == fixture.At.AddSeconds(20),
                "authoritative indexed endpoints cannot be overwritten by retimestamped lifecycle copies");
            var derived = ledger.Turns.Single(turn => turn.TurnId == "derived");
            Check(derived.EndedAt == fixture.At.AddSeconds(215), "recorded duration can recover a missing terminal endpoint");
            var unknown = ledger.Turns.Single(turn => turn.TurnId == "unknown");
            Check(unknown.StartedAt == null && unknown.EndedAt == null && unknown.Health == SampleHealth.Partial,
                "missing lifecycle endpoints remain unknown rather than becoming zero duration");
        });
        Console.WriteLine($"Usage history: {checks} checks, {failures} failure(s)");
        return failures;
    }

    private static readonly UTF8Encoding Utf8 = new(false);
    private static SessionCostSnapshot Read(SessionCostProvider provider)
    {
        var result = provider.ReadAsync().GetAwaiter().GetResult();
        for (int index = 0; index < 63; index++) result = provider.ReadAsync().GetAwaiter().GetResult();
        return result;
    }
    private static void Append(string path, params string[] lines) => File.AppendAllText(path, string.Join('\n', lines) + "\n", Utf8);
    private sealed class Fixture : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "codex-hud-history-tests-" + Guid.NewGuid().ToString("N"));
        public DateTimeOffset At { get; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds());
        public Fixture() { Directory.CreateDirectory(Path.Combine(Home, "sessions")); Directory.CreateDirectory(Path.Combine(Home, "archived_sessions")); }
        public string Add(string id, string turn, string? parent = null, bool archived = false, bool review = false, bool usage = true, string? suffix = null)
        {
            string path = Path.Combine(Home, archived ? "archived_sessions" : "sessions", id + (suffix ?? "") + ".jsonl");
            Append(path, Meta(id, parent, review), Context(turn));
            if (usage) Append(path, Usage(id, turn, id + "-response", 10));
            return path;
        }
        public string Meta(string id, string? parent = null, bool review = false) => JsonSerializer.Serialize(new
        { timestamp = At, type = "session_meta", payload = new { id, parent_thread_id = parent, model_provider = "openai",
            thread_source = review ? "guardian_review" : parent == null ? "user" : "subagent", source = "vscode" } });
        public string Context(string turn) => JsonSerializer.Serialize(new
        { timestamp = At, type = "turn_context", payload = new { turn_id = turn, model = "gpt-6-astra", service_tier = "standard" } });
        public string Event(string kind, string turn, int seconds) => JsonSerializer.Serialize(new
        { timestamp = At.AddSeconds(seconds), type = "event_msg", payload = new { type = kind, turn_id = turn } });
        public string Usage(string id, string turn, string response, int seconds, int cumulative = 1)
        {
            object Counts(int multiplier) => new { input_tokens = 100 * multiplier, cached_input_tokens = 40 * multiplier,
                cache_write_input_tokens = 0, output_tokens = 20 * multiplier, reasoning_output_tokens = 5 * multiplier, total_tokens = 120 * multiplier };
            return JsonSerializer.Serialize(new { timestamp = At.AddSeconds(seconds), type = "token_usage_record",
                payload = new { thread_id = id, turn_id = turn, response_id = response, usage = Counts(1), thread_token_usage = Counts(cumulative) } });
        }
        public void AppendUsage(string id, string turn, string response, int cumulative) => Append(Path.Combine(Home, "sessions", id + ".jsonl"), Usage(id, turn, response, 20, cumulative));
        public void Dispose() => Directory.Delete(Home, true);
    }
    private static void Execute(string path, string sql)
    {
        if (sqlite3_open_v2(path, out var database, 0x00000002 | 0x00000004, IntPtr.Zero) != 0) throw new IOException("Cannot create test SQLite database.");
        try
        {
            int status = sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, out var error);
            if (error != IntPtr.Zero) sqlite3_free(error);
            if (status != 0) throw new IOException("Cannot populate test SQLite database: " + status);
        }
        finally { sqlite3_close_v2(database); }
    }
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr context, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr database);
}