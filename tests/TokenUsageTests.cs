using System.Text;
using System.Text.Json;
using CodexHud.Core;

public static class TokenUsageTests
{
    public static int Run()
    {
        int failures = 0;
        void Check(bool valid, string scenario)
        {
            if (valid) return;
            failures++;
            Console.Error.WriteLine("FAIL tokens: " + scenario);
        }
        var now = DateTimeOffset.UtcNow.AddSeconds(-10);
        var tracker = new TokenUsageTracker("self");
        void Observe(string thread, string turn, string response, object? turnCounts, object? threadCounts, DateTimeOffset? at = null)
        {
            using var record = JsonDocument.Parse(Record(thread, turn, response, turnCounts, threadCounts, at ?? now));
            tracker.Observe(record.RootElement.GetProperty("payload"), at ?? now);
        }
        TaskTokenUsage Read(string? turn = "one", bool readable = true, bool unfinished = true, DateTimeOffset? at = null) =>
            tracker.Snapshot(turn, at ?? now, readable, unfinished);
        Check(Read().CurrentTurn.Counts is null && Read().Thread.Counts is null, "missing records stay unknown");
        Observe("parent", "one", "inherited", Counts(900, 90), Counts(900, 90));
        Check(Read().Thread.Counts is null, "explicit foreign thread ID rejects inherited/fork records");
        Observe("self", "one", "r1", Counts(100, 20, 80, 10), Counts(100, 20, 80, 10));
        Check(Read().Thread.Counts?.TotalTokens == 120 && Read().CurrentTurn.Counts?.TotalTokens == 120,
            "input and output total excludes double-counting cache and reasoning subsets");
        Check(Read().CurrentTurn.Counts?.CachedInputTokens == 80 && Read().CurrentTurn.Counts?.ReasoningOutputTokens == 10,
            "cache and reasoning retained as separate breakdown fields");
        Observe("self", "one", "r1", Counts(200, 40), Counts(200, 40), now.AddSeconds(1));
        Check(Read().Thread.Counts?.TotalTokens == 120 && Read().Thread.ObservedAt == now,
            "duplicate response ID cannot add counters or refresh timestamp");
        Observe("self", "one", "r2", Counts(200, 40, 150, 20), Counts(200, 40, 150, 20), now.AddSeconds(2));
        Check(Read().Thread.Counts?.TotalTokens == 240, "cumulative snapshots replace previous counts rather than sum");
        Check(Read("two").CurrentTurn.Counts is null && Read("two").Thread.Counts?.TotalTokens == 240,
            "new turn without usage cannot inherit previous turn total");
        Check(Read(null).CurrentTurn.Counts is null, "unidentified turn never borrows latest token record");
        Observe("self", "two", "r3", Counts(10, 5, 5, 2), Counts(210, 45, 155, 22), now.AddSeconds(3));
        Check(Read("two").CurrentTurn.Counts?.TotalTokens == 15 && Read("two").Thread.Counts?.TotalTokens == 255,
            "turn totals reset independently while thread total continues");
        Observe("self", "one", "r4", Counts(201, 40, 151, 20), Counts(200, 40, 150, 20), now.AddSeconds(1));
        Check(Read("two").CurrentTurn.Counts?.TotalTokens == 15 && Read("two").Thread.Counts?.TotalTokens == 255,
            "late previous-turn response cannot replace current turn or regress thread timestamp");
        Check(Read("two", false).Thread.Health == SampleHealth.Stale && Read("two", false).Thread.Counts?.TotalTokens == 255,
            "unreadable or incomplete source preserves counts as stale");
        Check(Read("two", at: now.AddSeconds(124)).CurrentTurn.Health == SampleHealth.Stale,
            "unfinished turn with old token evidence is stale");
        Check(Read("two", unfinished: false, at: now.AddDays(5)).CurrentTurn.Health == SampleHealth.Fresh,
            "completed historical counters do not expire merely because they are old");
        tracker.MarkGap();
        Check(Read("two").Thread.Health == SampleHealth.Stale, "skipped log interval marks retained counters stale");
        Observe("self", "two", "r5", Counts(11, 5, 5, 2), Counts(211, 45, 155, 22), now.AddSeconds(4));
        Check(Read("two").Thread.Health == SampleHealth.Fresh, "new explicit cumulative snapshot recovers after log gap");
        Observe("self", "two", "bad", Counts(-1, 5), Counts(long.MaxValue, 5), now.AddSeconds(5));
        Check(Read("two").Thread.Counts?.TotalTokens == 256 && Read("two").Thread.Health == SampleHealth.Stale,
            "negative and overflow values retain last valid totals as stale");
        Observe("self", "two", "regress", Counts(1, 1), Counts(1, 1), now.AddSeconds(6));
        Check(Read("two").Thread.Counts?.TotalTokens == 256 && Read("two").Thread.ObservedAt == now.AddSeconds(4),
            "cumulative regression cannot silently reset usage or refresh evidence");
        Observe("self", "two", "missing", null, null, now.AddSeconds(7));
        Check(Read("two").CurrentTurn.Counts?.TotalTokens == 16, "missing cumulative fields cannot erase valid last reading");

        using (var partial = JsonDocument.Parse("""{"g":{"total_tokens":0}}"""))
        {
            var parsed = TokenUsageTracker.ReadCounts(partial.RootElement, "g");
            Check(parsed.Counts?.TotalTokens == 0 && parsed.Counts.InputTokens is null && parsed.Health == SampleHealth.Partial,
                "real zero is preserved and absent fields remain unknown");
        }
        string[] invalidGroups = [
            """{"input_tokens":9223372036854775808,"output_tokens":0,"total_tokens":0}""",
            """{"input_tokens":1.5,"output_tokens":0,"total_tokens":1}""",
            """{"input_tokens":"12","output_tokens":0,"total_tokens":12}""",
            """{"input_tokens":10,"output_tokens":2,"total_tokens":13}""",
            """{"input_tokens":10,"cached_input_tokens":11,"output_tokens":2,"total_tokens":12}""",
            """{"input_tokens":10,"output_tokens":2,"reasoning_output_tokens":3,"total_tokens":12}"""
        ];
        foreach (var group in invalidGroups)
        {
            using var invalid = JsonDocument.Parse("{\"g\":" + group + "}");
            Check(TokenUsageTracker.ReadCounts(invalid.RootElement, "g").Counts is null, "invalid numeric group is rejected: " + group);
        }
        tracker.Reset();
        Check(Read().Thread.Counts is null, "file identity reset clears previous file's counters");

        string directory = Path.Combine(Path.GetTempPath(), "codex-hud-token-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string log = Path.Combine(directory, "sample.jsonl");
            File.WriteAllText(log, new string('x', RolloutCursor.MaximumRead + 10) + "\n"
                + Record("other", "one", "copied", Counts(900, 90), Counts(900, 90), now) + "\n"
                + Record("self", "one", "r1", Counts(100, 20), Counts(1000, 200), now) + "\n", new UTF8Encoding(false));
            var cursor = new RolloutCursor("self");
            int budget = RolloutCursor.MaximumRead;
            cursor.Update(log, ref budget);
            Check(cursor.IsCaughtUp && cursor.Tokens.Snapshot("one", now, true, true).Thread.Counts?.TotalTokens == 1200,
                "bounded cold tail reads authoritative cumulative totals without earlier responses");
            var next = Record("self", "one", "r2", Counts(200, 20), Counts(1100, 200), now.AddSeconds(1));
            File.AppendAllText(log, next[..(next.Length / 2)], new UTF8Encoding(false));
            budget = RolloutCursor.MaximumRead;
            cursor.Update(log, ref budget);
            Check(!cursor.IsCaughtUp && cursor.Tokens.Snapshot("one", now, cursor.IsCaughtUp, true).Thread.Health == SampleHealth.Stale,
                "incomplete token JSON record is not committed and marks source incomplete");
            File.AppendAllText(log, next[(next.Length / 2)..] + "\n", new UTF8Encoding(false));
            cursor.Update(log, ref budget);
            Check(cursor.IsCaughtUp && cursor.Tokens.Snapshot("one", now, true, true).Thread.Counts?.TotalTokens == 1300,
                "completed JSON record advances cumulative counters once");
            File.AppendAllText(log, "{\"type\":\"compacted\",\"timestamp\":\"" + now.ToString("O")
                + "\",\"payload\":{\"latest_token_usage_record\":{\"thread_id\":\"self\",\"thread_token_usage\":{\"total_tokens\":9999}}}}\n",
                new UTF8Encoding(false));
            cursor.Update(log, ref budget);
            Check(cursor.Tokens.Snapshot("one", now, true, true).Thread.Counts?.TotalTokens == 1300,
                "nested compaction record does not duplicate cumulative usage");
            File.WriteAllText(log, Record("self", "new", "new-r1", Counts(5, 1), Counts(5, 1), now) + "\n", new UTF8Encoding(false));
            cursor.Update(log, ref budget);
            Check(cursor.Tokens.Snapshot("new", now, true, true).Thread.Counts?.TotalTokens == 6,
                "log truncation resets counters instead of retaining a different file epoch");
            using (var locked = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Check(!cursor.Update(log, ref budget), "temporary sharing violation is reported as read failure");
            }
            File.AppendAllText(log, new string('x', RolloutCursor.MaximumRead + 100) + "\n", new UTF8Encoding(false));
            budget = RolloutCursor.MaximumRead;
            bool recovered = cursor.Update(log, ref budget);
            var held = cursor.Tokens.Snapshot("new", now, recovered && cursor.IsCaughtUp, true).Thread;
            Check(recovered && held.Counts?.TotalTokens == 6 && held.Health == SampleHealth.Stale,
                "same-file recovery with no token record in bounded tail preserves verified counts as stale");
            File.AppendAllText(log, Record("self", "new", "new-r2", Counts(6, 2), Counts(6, 2), now.AddSeconds(1)) + "\n",
                new UTF8Encoding(false));
            budget = RolloutCursor.MaximumRead;
            cursor.Update(log, ref budget);
            var refreshed = cursor.Tokens.Snapshot("new", now, cursor.IsCaughtUp, true).Thread;
            Check(refreshed.Counts?.TotalTokens == 8 && refreshed.Health == SampleHealth.Fresh,
                "new cumulative snapshot restores freshness after same-file read failure");
            using (var locked = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None))
                cursor.Update(log, ref budget);
            string replacement = Path.Combine(directory, "replacement.jsonl");
            File.WriteAllText(replacement, Record("self", "replacement", "replacement-r1", Counts(1, 0), Counts(1, 0), now) + "\n",
                new UTF8Encoding(false));
            File.Move(replacement, log, true);
            budget = RolloutCursor.MaximumRead;
            cursor.Update(log, ref budget);
            Check(cursor.Tokens.Snapshot("replacement", now, cursor.IsCaughtUp, true).Thread.Counts?.TotalTokens == 1,
                "true file replacement after a read failure still resets prior epoch counters");
            File.Delete(log);
            bool readable = cursor.Update(log, ref budget);
            Check(!readable && cursor.Tokens.Snapshot("replacement", now, readable, true).Thread.Counts?.TotalTokens == 1
                && cursor.Tokens.Snapshot("replacement", now, readable, true).Thread.Health == SampleHealth.Stale,
                "temporary file loss preserves last valid count with stale validity");
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine("FAIL token cursor fixture: " + exception.GetType().Name + ": " + exception.Message);
        }
        finally { Directory.Delete(directory, true); }
        return failures;
    }

    public static async Task<int> LiveAsync()
    {
        // Explicit opt-in. Reads current local metadata without outputting task titles, paths, IDs,
        // messages, tool responses, credentials, or launching an app-server.
        using var provider = new ActivityProvider(CodexLocator.ResolveHome());
        var result = await provider.ReadAsync(true);
        var tasks = result.Tasks;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            check = "live-local-token-metadata", observedAt = result.ObservedAt, health = result.Health.ToString(),
            taskCount = tasks.Count,
            tasksWithThreadUsage = tasks.Count(task => task.TokenUsage.Thread.Counts is not null),
            tasksWithCurrentTurnUsage = tasks.Count(task => task.TokenUsage.CurrentTurn.Counts is not null),
            counts = tasks.Select((task, index) => new
            {
                sample = index + 1,
                currentTurn = task.TokenUsage.CurrentTurn.Counts,
                currentHealth = task.TokenUsage.CurrentTurn.Health.ToString(),
                thread = task.TokenUsage.Thread.Counts,
                threadHealth = task.TokenUsage.Thread.Health.ToString(),
                tokenObservedAt = task.TokenUsage.Thread.ObservedAt
            })
        }));
        return result.Health == SampleHealth.Unavailable ? 1 : 0;
    }

    private static object Counts(long input, long output, long cache = 0, long reasoning = 0) => new
    {
        input_tokens = input, cached_input_tokens = cache, cache_write_input_tokens = 0,
        output_tokens = output, reasoning_output_tokens = reasoning,
        total_tokens = input <= long.MaxValue - output ? input + output : long.MaxValue
    };

    private static string Record(string thread, string turn, string response, object? turnCounts, object? threadCounts, DateTimeOffset at) =>
        JsonSerializer.Serialize(new
        {
            timestamp = at, type = "token_usage_record",
            payload = new { thread_id = thread, turn_id = turn, response_id = response, turn_token_usage = turnCounts, thread_token_usage = threadCounts }
        });
}
