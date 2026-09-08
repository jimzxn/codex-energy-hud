using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexHud.Core;
using CodexHud.Core.Native;

public static class ActivityTests
{
    public static int Run()
    {
        int failures = TaskTimingTests.Run();
        void Check(bool valid, string scenario)
        {
            if (valid) return;
            failures++;
            Console.Error.WriteLine("FAIL activity: " + scenario);
        }

        var now = DateTimeOffset.UtcNow;
        Check(ActivityClassifier.Classify("inProgress", now, true, now) == ActivityState.ExecutionEvidence, "recent unfinished activity");
        Check(ActivityClassifier.Classify("inProgress", now.AddSeconds(-120), true, now) == ActivityState.ExecutionEvidence, "120-second boundary");
        Check(ActivityClassifier.Classify("inProgress", now.AddSeconds(-121), true, now) == ActivityState.Unconfirmed, "stale activity is not running");
        Check(ActivityClassifier.Classify("inProgress", now, false, now) == ActivityState.Unconfirmed, "closed app invalidates running inference");
        Check(ActivityClassifier.Classify("completed", now.AddDays(-1), false, now) == ActivityState.Completed, "explicit completion survives app exit");
        Check(ActivityClassifier.Classify("interrupted", now, true, now) == ActivityState.Interrupted, "explicit interrupt");
        Check(ActivityClassifier.Classify("newSchemaStatus", now, true, now) == ActivityState.Unconfirmed, "unknown status fails closed");
        Check(ActivityClassifier.Classify("inProgress", now.AddMinutes(5), true, now) == ActivityState.Unconfirmed, "future timestamp fails closed");
        Check(ActivityClassifier.Classify("inProgress", null, true, now) == ActivityState.Unconfirmed, "missing timestamp");

        string directory = Path.Combine(Path.GetTempPath(), "codex-hud-activity-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sessions = Path.Combine(directory, "sessions");
            Directory.CreateDirectory(sessions);
            string log = Path.Combine(sessions, "sample.jsonl");
            File.WriteAllText(log, Event("task_started", now.AddSeconds(-10)) + "\n", new UTF8Encoding(false));
            var cursor = new RolloutCursor();
            int budget = 1024 * 1024;
            Check(cursor.Update(log, ref budget) && cursor.Evidence.Status == "inProgress", "initial lifecycle read");
            long completeOffset = cursor.Offset;
            string completion = Event("task_complete", now.AddSeconds(-1));
            File.AppendAllText(log, completion[..(completion.Length / 2)], new UTF8Encoding(false));
            cursor.Update(log, ref budget);
            Check(cursor.Evidence.Status == "inProgress" && cursor.Offset == completeOffset, "half-line not committed");
            int beforeUnchangedPartial = budget;
            cursor.Update(log, ref budget);
            Check(budget == beforeUnchangedPartial && cursor.Offset == completeOffset,
                "unchanged half-line EOF does not consume the shared read budget");
            File.AppendAllText(log, completion[(completion.Length / 2)..] + "\n", new UTF8Encoding(false));
            cursor.Update(log, ref budget);
            Check(cursor.Evidence.Status == "completed", "completed half-line consumed once");
            Check(cursor.Evidence.StartedAt == now.AddSeconds(-10) && cursor.Evidence.EndedAt == now.AddSeconds(-1),
                "same-turn terminal marker retains explicit start and end");
            long endOffset = cursor.Offset;
            cursor.Update(log, ref budget);
            Check(cursor.Offset == endOffset, "unchanged file leaves offset stable");
            File.WriteAllText(log, Event("turn_aborted", now) + "\n", new UTF8Encoding(false));
            cursor.Update(log, ref budget);
            Check(cursor.Evidence.Status == "interrupted", "truncate resets state");
            Check(cursor.Evidence.StartedAt is null && cursor.Evidence.EndedAt == now,
                "cold interruption has explicit end but does not invent a start");
            long oldLength = new FileInfo(log).Length;
            File.WriteAllText(log, Event("task_started", now) + new string(' ', (int)Math.Max(0, oldLength - Event("task_started", now).Length)) + "\n", new UTF8Encoding(false));
            cursor.Update(log, ref budget);
            Check(cursor.Evidence.Status == "inProgress", "truncate and regrow past old size is detected by hash anchor");
            string replacement = Path.Combine(sessions, "replacement.jsonl");
            File.WriteAllText(replacement, Event("task_started", now) + "\n", new UTF8Encoding(false));
            File.Move(replacement, log, true);
            cursor.Update(log, ref budget);
            Check(cursor.Evidence.Status == "inProgress", "same-path file replacement resets identity");
            string renamed = Path.Combine(sessions, "renamed.jsonl");
            File.Move(log, renamed);
            cursor.Update(renamed, ref budget);
            Check(cursor.Evidence.Status == "inProgress", "indexed renamed path is read");
            File.AppendAllText(renamed, "invalid-json\n" + Event("task_complete", now) + "\n", new UTF8Encoding(false));
            cursor.Update(renamed, ref budget);
            Check(cursor.Evidence.Status == "completed", "malformed record does not block later terminal event");

            string large = Path.Combine(sessions, "large.jsonl");
            File.WriteAllText(large, new string('x', RolloutCursor.MaximumRead + 200) + "\n" + Event("task_started", now) + "\n", new UTF8Encoding(false));
            var largeCursor = new RolloutCursor();
            int largeBudget = RolloutCursor.MaximumRead;
            largeCursor.Update(large, ref largeBudget);
            Check(largeCursor.Evidence.Status == "inProgress" && largeBudget == 0, "bounded cold tail skips large record");

            var incompleteCursors = new List<(string Path, RolloutCursor Cursor)>();
            for (int i = 0; i < 21; i++)
            {
                string partialLog = Path.Combine(sessions, "partial-" + i + ".jsonl");
                File.WriteAllText(partialLog, new string('x', 200 * 1024), new UTF8Encoding(false));
                incompleteCursors.Add((partialLog, new RolloutCursor()));
            }
            string lastLog = Path.Combine(sessions, "after-partial-files.jsonl");
            File.WriteAllText(lastLog, Event("task_started", now) + "\n", new UTF8Encoding(false));
            var lastCursor = new RolloutCursor();
            int firstPassBudget = 4 * 1024 * 1024;
            foreach (var partial in incompleteCursors) partial.Cursor.Update(partial.Path, ref firstPassBudget);
            lastCursor.Update(lastLog, ref firstPassBudget);
            Check(lastCursor.Evidence.Status is null, "first incomplete-tail pass uses bounded budget");
            int secondPassBudget = 4 * 1024 * 1024;
            foreach (var partial in incompleteCursors) partial.Cursor.Update(partial.Path, ref secondPassBudget);
            lastCursor.Update(lastLog, ref secondPassBudget);
            Check(lastCursor.Evidence.Status == "inProgress" && secondPassBudget > 3 * 1024 * 1024,
                "unchanged incomplete EOF files cannot starve a later task");

            var terminal = new RolloutEvidence("completed", "t1", now.AddMinutes(-1), now.AddSeconds(-10), now.AddSeconds(-10));
            var later = new RolloutEvidence(null, null, null, now, null);
            Check(ActivityProvider.Merge(terminal, later).Status is null, "new activity after old terminal cannot remain completed");
            var unknownHistory = new RolloutEvidence("failed", "new-turn", now.AddSeconds(-10), now.AddSeconds(-5), now.AddSeconds(-5));
            var oldRunning = new RolloutEvidence("inProgress", "old-turn", now.AddSeconds(-15), now, now.AddSeconds(-15));
            var unknownMerged = ActivityProvider.Merge(unknownHistory, oldRunning);
            Check(ActivityClassifier.Classify(unknownMerged.Status, unknownMerged.LastActivityAt, true, now) == ActivityState.Unconfirmed,
                "unknown latest history status cannot revive older execution evidence");
            Check(unknownMerged.LastActivityAt == unknownHistory.LastActivityAt,
                "unknown current turn never borrows another turn's newer activity for duration");
            var knownTerminal = new RolloutEvidence("interrupted", "new-turn", now.AddSeconds(-10), now, now);
            Check(ActivityProvider.Merge(unknownHistory, knownTerminal).Status == "interrupted",
                "same-turn explicit terminal log can resolve an unknown history status");
            var roundedSecond = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
            var roundedCompletion = new RolloutEvidence("completed", "fast-turn", roundedSecond, roundedSecond, roundedSecond);
            var fractionalStart = new RolloutEvidence("inProgress", "fast-turn", roundedSecond.AddMilliseconds(200),
                roundedSecond.AddMilliseconds(200), roundedSecond.AddMilliseconds(200));
            var fastCompleted = ActivityProvider.Merge(roundedCompletion, fractionalStart);
            Check(ActivityClassifier.Classify(fastCompleted.Status, fastCompleted.LastActivityAt, true,
                    roundedSecond.AddMilliseconds(900)) == ActivityState.Completed,
                "same-turn completion at second precision wins over fractional startup marker");
            var noCompletionTimestamp = new RolloutEvidence("interrupted", "fast-turn", now.AddSeconds(-10),
                now.AddSeconds(-10), now.AddSeconds(-10));
            Check(ActivityProvider.Merge(noCompletionTimestamp, fractionalStart).Status == "interrupted",
                "same-turn interruption remains terminal when completed_at is absent");
            Check(ActivityProvider.Merge(fractionalStart, roundedCompletion).Status == "completed",
                "same-turn terminal rollout wins over more precise history startup timestamp");
            var nextTurn = fractionalStart with { TurnId = "next-turn" };
            Check(ActivityProvider.Merge(roundedCompletion, nextTurn).Status == "inProgress",
                "different newer turn can start after a historical terminal state");

            var completeWithoutStart = new RolloutEvidence("completed", "cold-turn", null,
                now.AddSeconds(-2), now.AddSeconds(-2), now.AddSeconds(-2));
            var coldHistory = new RolloutEvidence("inProgress", "cold-turn", now.AddMinutes(-4),
                now.AddMinutes(-4), now.AddMinutes(-4));
            var coldMerged = ActivityProvider.Merge(coldHistory, completeWithoutStart);
            Check(coldMerged.StartedAt == coldHistory.StartedAt && coldMerged.EndedAt == completeWithoutStart.EndedAt,
                "cold-tail terminal recovers start only from same-turn history");
            Check(ActivityProvider.Merge(coldHistory with { TurnId = "different-turn" }, completeWithoutStart).StartedAt is null,
                "cold-tail terminal never borrows another turn's start");
            var roundedTiming = ActivityProvider.Merge(roundedCompletion with { EndedAt = roundedSecond }, fractionalStart);
            Check(roundedTiming.StartedAt == roundedSecond && roundedTiming.EndedAt == roundedSecond,
                "second-resolution history retains coherent timing when fractional start follows rounded end");
            var reversedTiming = ActivityProvider.Merge(roundedCompletion with { EndedAt = roundedSecond },
                fractionalStart with { StartedAt = roundedSecond.AddSeconds(5) });
            Check(reversedTiming.StartedAt > reversedTiming.EndedAt,
                "clock reversal beyond rounding is preserved as invalid, not silently corrected");
            Check(ActivityProvider.Merge(noCompletionTimestamp, fractionalStart).EndedAt is null,
                "missing terminal timestamp is never replaced with last activity or turn start");
            var newTurnHistory = new RolloutEvidence("inProgress", "current", now.AddSeconds(-125),
                now.AddSeconds(-125), now.AddSeconds(-125));
            var priorTurnActivity = new RolloutEvidence("completed", "previous", now.AddMinutes(-5),
                now, now.AddMinutes(-3), now.AddMinutes(-3));
            var isolatedTurn = ActivityProvider.Merge(newTurnHistory, priorTurnActivity);
            Check(isolatedTurn.TurnId == "current" && isolatedTurn.LastActivityAt == newTurnHistory.LastActivityAt
                && ActivityClassifier.Classify(isolatedTurn.Status, isolatedTurn.LastActivityAt, true, now) == ActivityState.Unconfirmed,
                "late activity from previous turn cannot extend current elapsed evidence or revive its execution state");

            string resetLog = Path.Combine(sessions, "timer-reset.jsonl");
            File.WriteAllText(resetLog, Event("task_started", now.AddMinutes(-2), "old") + "\n"
                + Event("task_complete", now.AddMinutes(-1), "old") + "\n"
                + Event("task_started", now.AddSeconds(-10), "new") + "\n", new UTF8Encoding(false));
            var resetCursor = new RolloutCursor();
            int resetBudget = RolloutCursor.MaximumRead;
            resetCursor.Update(resetLog, ref resetBudget);
            Check(resetCursor.Evidence.StartedAt == now.AddSeconds(-10) && resetCursor.Evidence.EndedAt is null,
                "next turn resets duration and clears previous terminal timestamp");
            File.AppendAllText(resetLog, Event("turn_aborted", now, "unknown-start") + "\n", new UTF8Encoding(false));
            resetCursor.Update(resetLog, ref resetBudget);
            Check(resetCursor.Evidence.StartedAt is null && resetCursor.Evidence.EndedAt == now,
                "terminal for a different turn does not inherit previous start");

            string state = Path.Combine(directory, "state_1.sqlite");
            CreateDatabase(state, "CREATE TABLE threads (id TEXT, title TEXT, name TEXT, cwd TEXT, source TEXT, thread_source TEXT, rollout_path TEXT, archived INTEGER, updated_at INTEGER);" +
                "INSERT INTO threads VALUES ('root','Root',NULL," + Quote("\\\\?\\" + directory) + ",'vscode','user'," + Quote("\\\\?\\" + renamed) + ",0,1);" +
                "INSERT INTO threads VALUES ('child','Child',NULL," + Quote(directory) + ",'vscode','subagent'," + Quote(renamed) + ",0,1);" +
                "INSERT INTO threads VALUES ('remote','Remote',NULL,'/home/user/repo','vscode','user'," + Quote(renamed) + ",0,1);");
            using (var readOnly = new SqliteReader(state))
            {
                Check(readOnly.Query("SELECT id FROM threads WHERE id=?", "root").Count == 1, "native read-only SQL supports parameters");
                bool rejected = false;
                try { readOnly.Query("DELETE FROM threads"); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "read-only adapter rejects mutations");
            }
            using (var provider = new ActivityProvider(directory))
            {
                var snapshot = provider.ReadAsync(true).GetAwaiter().GetResult();
                Check(snapshot.Tasks.Count == 1 && snapshot.Tasks[0].Id == "root", "local top-level filter");
                Check(snapshot.Tasks[0].State == ActivityState.Completed, "rollout completion without history DB");
            }

            string history = Path.Combine(directory, "thread_history_1.sqlite");
            CreateDatabase(history, "CREATE TABLE thread_turns (thread_id TEXT, turn_id TEXT, status TEXT, started_at INTEGER, completed_at INTEGER, rollout_ordinal INTEGER);" +
                "CREATE INDEX idx_turns ON thread_turns(thread_id,rollout_ordinal);" +
                "INSERT INTO thread_turns VALUES ('root','new-turn','inProgress'," + now.ToUnixTimeSeconds() + ",NULL,1);");
            File.WriteAllText(renamed, Event("task_started", now) + "\n", new UTF8Encoding(false));
            using (var provider = new ActivityProvider(directory))
            {
                var active = provider.ReadAsync(true).GetAwaiter().GetResult();
                Check(active.Tasks.Single().State == ActivityState.ExecutionEvidence, "real SQLite history and rollout integration");
                var stopped = provider.ReadAsync(false).GetAwaiter().GetResult();
                Check(stopped.Tasks.Single().State == ActivityState.Unconfirmed, "provider app-exit behavior");
            }

            ExecuteTestDatabase(history, "UPDATE thread_turns SET status='failed' WHERE thread_id='root';");
            using (var provider = new ActivityProvider(directory))
            {
                var unsupportedStatus = provider.ReadAsync(true).GetAwaiter().GetResult();
                Check(unsupportedStatus.Tasks.Single().State == ActivityState.Unconfirmed,
                    "real SQLite unknown terminal status never falls back to recent task_started");
            }

            File.WriteAllText(renamed, Event("task_complete", now.AddSeconds(-2), "new-turn") + "\n", new UTF8Encoding(false));
            ExecuteTestDatabase(history, "UPDATE thread_turns SET status='completed', started_at="
                + now.AddMinutes(-2).ToUnixTimeSeconds() + ", completed_at=" + now.AddSeconds(-2).ToUnixTimeSeconds()
                + " WHERE thread_id='root';");
            using (var provider = new ActivityProvider(directory))
            {
                var timed = provider.ReadAsync(true).GetAwaiter().GetResult().Tasks.Single();
                Check(timed.StartedAt == ActivityProvider.FromUnix(now.AddMinutes(-2).ToUnixTimeSeconds())
                    && timed.EndedAt == now.AddSeconds(-2), "provider exposes merged explicit turn timing from real SQLite and cold log");
            }
            var tokenCounts = new { input_tokens = 10, cached_input_tokens = 8, cache_write_input_tokens = 0,
                output_tokens = 2, reasoning_output_tokens = 1, total_tokens = 12 };
            File.AppendAllText(renamed, JsonSerializer.Serialize(new
            {
                timestamp = now.AddSeconds(-1), type = "token_usage_record",
                payload = new { thread_id = "root", turn_id = "new-turn", response_id = "root-response",
                    turn_token_usage = tokenCounts, thread_token_usage = tokenCounts }
            }) + "\n", new UTF8Encoding(false));
            using (var provider = new ActivityProvider(directory))
            {
                var withTokens = provider.ReadAsync(true).GetAwaiter().GetResult().Tasks.Single();
                Check(withTokens.TokenUsage.TurnId == "new-turn" && withTokens.TokenUsage.CurrentTurn.Counts?.TotalTokens == 12
                    && withTokens.TokenUsage.Thread.Counts?.CachedInputTokens == 8,
                    "provider joins identified current-turn and task cumulative tokens from local log");
                ExecuteTestDatabase(history, "UPDATE thread_turns SET turn_id='next-token-turn', status='inProgress', started_at="
                    + now.ToUnixTimeSeconds() + ", completed_at=NULL WHERE thread_id='root';");
                var withoutTokens = provider.ReadAsync(true).GetAwaiter().GetResult().Tasks.Single();
                Check(withoutTokens.TokenUsage.TurnId == "next-token-turn" && withoutTokens.TokenUsage.CurrentTurn.Counts is null
                    && withoutTokens.TokenUsage.Thread.Counts?.TotalTokens == 12,
                    "new history turn without token event preserves task total but never copies previous turn usage");
            }
            ExecuteTestDatabase(history, "UPDATE thread_turns SET status='failed', completed_at=NULL WHERE thread_id='root';");
            File.WriteAllText(renamed, Event("task_started", now) + "\n", new UTF8Encoding(false));

            var extraRows = new StringBuilder();
            for (int i = 0; i < 42; i++)
                extraRows.Append("INSERT INTO threads VALUES ('unfinished").Append(i).Append("','Pending',NULL,")
                    .Append(Quote(directory)).Append(",'cli','user',").Append(Quote(Path.Combine(sessions, "missing" + i + ".jsonl"))).Append(",0,1);");
            ExecuteTestDatabase(state, extraRows.ToString());
            using (var provider = new ActivityProvider(directory))
                Check(provider.ReadAsync(false).GetAwaiter().GetResult().Tasks.Count == 43, "unfinished candidates are never limited to thirty");

            var completedRows = new StringBuilder();
            var completedTurns = new StringBuilder();
            for (int i = 0; i < 38; i++)
            {
                completedRows.Append("INSERT INTO threads VALUES ('finished").Append(i).Append("','Finished',NULL,")
                    .Append(Quote(directory)).Append(",'cli','user',").Append(Quote(Path.Combine(sessions, "finished" + i + ".jsonl"))).Append(",0,1);");
                completedTurns.Append("INSERT INTO thread_turns VALUES ('finished").Append(i).Append("','turn','completed',")
                    .Append(now.AddMinutes(-1).ToUnixTimeSeconds()).Append(',').Append(now.AddSeconds(-5).ToUnixTimeSeconds()).Append(",1);");
            }
            ExecuteTestDatabase(state, completedRows.ToString());
            ExecuteTestDatabase(history, completedTurns.ToString());
            using (var provider = new ActivityProvider(directory))
            {
                var bounded = provider.ReadAsync(false).GetAwaiter().GetResult();
                Check(bounded.Tasks.Count(t => t.State == ActivityState.Unconfirmed) == 43
                    && bounded.Tasks.Count(t => t.State == ActivityState.Completed) == 30,
                    "only completed history is capped at thirty");
            }

            CreateDatabase(Path.Combine(directory, "state_2.sqlite"), "CREATE TABLE threads (id TEXT);");
            using (var provider = new ActivityProvider(directory))
            {
                var unsupported = provider.ReadAsync(true).GetAwaiter().GetResult();
                Check(unsupported.Health == SampleHealth.Unavailable && unsupported.Tasks.Count == 0, "new unsupported schema fails closed");
            }
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine("FAIL activity fixture: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            // This directory is a GUID-named test fixture, never a user Codex directory.
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        return failures;
    }

    private static string Event(string type, DateTimeOffset timestamp, string turnId = "turn-test") => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new { type, turn_id = turnId }
    });
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    private static void CreateDatabase(string path, string sql) => ExecuteTestDatabase(path, sql);
    private static void ExecuteTestDatabase(string path, string sql)
    {
        int result = sqlite3_open_v2(path, out var database, 0x00000002 | 0x00000004, IntPtr.Zero);
        if (result != 0) throw new IOException("Test SQLite open failed.");
        try
        {
            result = sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, out var error);
            if (error != IntPtr.Zero) sqlite3_free(error);
            if (result != 0) throw new IOException("Test SQLite fixture failed (" + result + ").");
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
