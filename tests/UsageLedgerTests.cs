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
            var beforeTier = Read();
            now = now.AddSeconds(1);
            Append(root, Context("three", now, "gpt-6-astra", "priority"), Usage("root", "three", "tier-change", now, 1, 2));
            var changedTier = Read();
            Check(changedTier.Health == SampleHealth.Fresh && changedTier.Epoch == beforeTier.Epoch
                && changedTier.Counts.TotalTokens == beforeTier.Counts.TotalTokens + 1,
                "valid service-tier change preserves the usage epoch and cumulative accounting");

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
        return failures + RunClassification() + RunCheckpointRecovery() + RunEstimatorCheckpointIntegration();
    }

    private static int RunClassification()
    {
        int failures = 0;
        int checks = 0;
        void Check(bool valid, string name)
        {
            checks++;
            if (valid) return;
            failures++;
            Console.Error.WriteLine("FAIL usage ledger classification: " + name);
        }
        string home = Path.Combine(Path.GetTempPath(), "codex-hud-ledger-role-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        string database = Path.Combine(home, "state_5.sqlite");
        var now = DateTimeOffset.UtcNow.AddMinutes(-2);
        try
        {
            Sql(database, "CREATE TABLE threads(id TEXT PRIMARY KEY,rollout_path TEXT,source TEXT,archived INTEGER,model TEXT,reasoning_effort TEXT,model_provider TEXT,updated_at INTEGER,thread_source TEXT);");
            string AddTask(string id, string? role = null, string model = "gpt-6-astra", bool initial = false)
            {
                string path = Path.Combine(sessions, id + ".jsonl");
                File.WriteAllText(path, Meta(id, initial ? now.AddDays(-1) : now) + "\n", new UTF8Encoding(false));
                Sql(database, $"INSERT INTO threads VALUES({Q(id)},{Q(path)},'subagent',0,{Q(model)},'high','openai',0,{(role is null ? "NULL" : Q(role))});");
                return path;
            }
            string root = AddTask("z-main", initial: true);
            string child = AddTask("z-child", "subagent", initial: true);
            string review = AddTask("review", "guardian_review", "codex-auto-review", initial: true);
            // More internal rows than the source limit must not crowd out any workload row.
            Sql(database, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<4200) "
                + "INSERT INTO threads SELECT 'a-internal-'||i,'/unavailable/internal.jsonl','subagent',1,'codex-auto-review','low','openai',0,'guardian_review' FROM n;");
            var provider = new UsageLedgerProvider(home, () => now);
            UsageLedgerSnapshot Read() => provider.ReadAsync().GetAwaiter().GetResult();
            var baseline = Read();
            Check(baseline.Health == SampleHealth.Fresh && baseline.ObservedThreads == 2 && baseline.Counts.TotalTokens == 0,
                "SQL role exclusion precedes LIMIT and ignores unreadable internal history while retaining ordinary subagents");
            var estimator = new QuotaTokenEstimator();
            var resetAt = now.AddHours(5);
            QuotaWindow Window(double remaining) => new("codex-primary", "codex", "fixture", "primary", 300, remaining, resetAt);
            QuotaSnapshot Quota(double remaining) => new(now, new[] { Window(remaining) }, SampleHealth.Fresh) { AccountKey = "fixture-account" };
            estimator.Observe(Quota(100), baseline);
            UsageLedgerSnapshot latest = baseline;
            for (int step = 1; step <= 3; step++)
            {
                now = now.AddSeconds(1);
                Append(root, Context("work", now), Usage("z-main", "work", "main-" + step, now, 10, step * 10));
                Append(child, Context("work", now), Usage("z-child", "work", "child-" + step, now, 20, step * 20));
                Append(review, Context("review", now, "codex-auto-review", effort: "low"),
                    Usage("review", "review", "review-" + step, now, 100000, step * 100000));
                latest = Read();
                Check(latest.Health == SampleHealth.Fresh && latest.Epoch == baseline.Epoch && latest.Counts.TotalTokens == step * 30
                    && latest.ModelMix?.Contains("codex-auto-review") == false,
                    "interleaved guardian usage does not enter workload counts or reset its epoch, step " + step);
                estimator.Observe(Quota(100 - 3 * step), latest);
            }
            var calibrated = estimator.Get(Window(91), now);
            Check(calibrated.Segments == 3 && calibrated.RemainingTokens is > 0,
                "three real workload segments calibrate while internal approvals continue");
            now = now.AddSeconds(1);
            using (var locked = new FileStream(review, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Append(root, Usage("z-main", "work", "main-locked-review", now, 10, 40));
                latest = Read();
                Check(latest.Health == SampleHealth.Fresh && latest.Epoch == baseline.Epoch && latest.Counts.TotalTokens == 100,
                    "inaccessible guardian log cannot block fresh workload capture");
            }
            now = now.AddSeconds(1);
            Sql(database, "UPDATE threads SET model='named-custom-model',reasoning_effort='low',model_provider='external' WHERE id='review';");
            File.WriteAllText(review, new string('x', UsageLedgerCursor.MaximumRead + 10), new UTF8Encoding(false));
            Append(root, Context("new-model", now, "gpt-5.5", "priority", "low"), Usage("z-main", "new-model", "model-change", now, 5, 45));
            latest = Read();
            Check(latest.Health == SampleHealth.Fresh && latest.Epoch == baseline.Epoch && latest.Counts.TotalTokens == 105
                && latest.ModelMix?.Contains("model:gpt-5.5/effort:low/tier:priority/provider:openai") == true,
                "normal same-pool model and effort changes preserve counts despite changed or malformed internal records");
            now = now.AddSeconds(1);
            Append(child, Context("effort-change", now, effort: "ultra"), Usage("z-child", "effort-change", "effort-r1", now, 7, 67));
            latest = Read();
            Check(latest.Health == SampleHealth.Fresh && latest.Epoch == baseline.Epoch && latest.Counts.TotalTokens == 112,
                "ordinary subagent effort change preserves cumulative workload usage");
            File.Delete(review);
            latest = Read();
            Check(latest.Health == SampleHealth.Fresh && latest.Epoch == baseline.Epoch && latest.Counts.TotalTokens == 112,
                "deleted internal log cannot manufacture a capture gap");
            Sql(database, "DELETE FROM threads WHERE id='review';");
            latest = Read();
            Check(latest.Health == SampleHealth.Fresh && latest.Epoch == baseline.Epoch && latest.Counts.TotalTokens == 112,
                "deleted internal index row cannot manufacture a capture gap");

            now = now.AddSeconds(1);
            string reclassified = AddTask("reclassified");
            Append(reclassified, Context("first", now), Usage("reclassified", "first", "first-r1", now, 10, 10));
            latest = Read();
            Check(latest.Health == SampleHealth.Fresh && latest.Counts.TotalTokens == 122,
                "ordinary task remains included before an explicit internal classification exists");
            Sql(database, "UPDATE threads SET thread_source='guardian_review',model='codex-auto-review' WHERE id='reclassified';");
            File.Delete(reclassified);
            latest = Read();
            Check(latest.Health == SampleHealth.Fresh && latest.Epoch == baseline.Epoch && latest.Counts.TotalTokens == 122,
                "existing cursor becoming guardian is removed without a gap or counter reset");

            now = now.AddSeconds(1);
            string unclassifiedReview = AddTask("unclassified-review", model: "codex-auto-review");
            Append(unclassifiedReview, Context("first", now, "codex-auto-review"), Usage("unclassified-review", "first", "first-r1", now, 100, 100));
            var ambiguous = Read();
            Check(ambiguous.Health == SampleHealth.Partial && ambiguous.Counts.TotalTokens == 0 && ambiguous.Epoch != baseline.Epoch,
                "a review-like model name without the explicit role remains an unmapped model");
            Sql(database, "UPDATE threads SET thread_source='guardian_review' WHERE id='unclassified-review';");
            var classified = Read();
            Check(classified.Health == SampleHealth.Fresh && classified.Epoch == ambiguous.Epoch && classified.Counts.TotalTokens == 0,
                "new explicit internal classification removes only that task's pending ambiguity without adding tokens");
            now = now.AddSeconds(1);
            Append(unclassifiedReview, Usage("unclassified-review", "first", "only-internal", now, 100, 200));
            Check(Read().Counts.TotalTokens == 0, "internal activity alone never synthesizes workload token increments");

            now = now.AddSeconds(1);
            string secondReview = AddTask("unclassified-review-2", model: "codex-auto-review");
            string spark = AddTask("spark", model: "gpt-5.3-codex-spark");
            Append(secondReview, Context("first", now, "codex-auto-review"), Usage("unclassified-review-2", "first", "first-r1", now, 100, 100));
            Append(spark, Context("first", now, "gpt-5.3-codex-spark"), Usage("spark", "first", "first-r1", now, 10, 10));
            var twoAmbiguous = Read();
            Sql(database, "UPDATE threads SET thread_source='guardian_review' WHERE id='unclassified-review-2';");
            var remainingAmbiguous = Read();
            Check(remainingAmbiguous.Health == SampleHealth.Partial && remainingAmbiguous.Epoch == twoAmbiguous.Epoch
                && remainingAmbiguous.Detail?.Contains("额度池") == true,
                "excluding a newly classified guardian cannot remove another model's unresolved pool protection");
            now = now.AddMinutes(11);
            var afterAmbiguity = Read();
            Check(afterAmbiguity.Health == SampleHealth.Fresh, "separate-model protection expires after its existing observation window");
            for (int profile = 1; profile <= 33; profile++)
            {
                now = now.AddSeconds(1);
                Append(root, Context("profile-" + profile, now, effort: "fixture-effort-" + profile),
                    Usage("z-main", "profile-" + profile, "profile-response-" + profile, now, 1, 45 + profile));
            }
            var manyProfiles = Read();
            Check(manyProfiles.Health == SampleHealth.Fresh && manyProfiles.Epoch == afterAmbiguity.Epoch
                && manyProfiles.Counts.TotalTokens == afterAmbiguity.Counts.TotalTokens + 33,
                "bounded profile metadata retention cannot reset complete cumulative token accounting");
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine("FAIL usage ledger classification fixture: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
        Console.WriteLine($"Usage ledger classification: {checks} checks, {failures} failure(s)");
        return failures;
    }
    private static int RunCheckpointRecovery()
    {
        int failures = 0, checks = 0;
        void Check(bool valid, string scenario)
        {
            checks++;
            if (valid) return;
            failures++;
            Console.Error.WriteLine("FAIL ledger checkpoint: " + scenario);
        }
        string home = Path.Combine(Path.GetTempPath(), "codex-hud-ledger-checkpoint-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        string database = Path.Combine(home, "state_5.sqlite");
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        try
        {
            Sql(database, "CREATE TABLE threads(id TEXT PRIMARY KEY,rollout_path TEXT,model TEXT,reasoning_effort TEXT,model_provider TEXT,thread_source TEXT);");
            string AddTask(string id, DateTimeOffset created)
            {
                string path = Path.Combine(sessions, id + ".jsonl");
                File.WriteAllText(path, Meta(id, created) + "\n", new UTF8Encoding(false));
                Sql(database, $"INSERT INTO threads VALUES({Q(id)},{Q(path)},'gpt-6-astra','high','openai',NULL);");
                return path;
            }
            UsageLedgerSnapshot Read(UsageLedgerProvider provider) => provider.ReadCheckpointAsync().GetAwaiter().GetResult();
            string root = AddTask("root", now.AddDays(-1));
            var provider = new UsageLedgerProvider(home, () => now);
            var first = Read(provider);
            Check(first.Health == SampleHealth.Fresh && first.Checkpoint is { Sequence: 1 }
                && first.Checkpoint.Counts == first.Counts && first.Checkpoint.ObservedAt == first.ObservedAt
                && first.Checkpoint.HasValidChecksum(), "same-gate capture binds counts, time and integrity to the cursor baseline");
            Check(provider.ReadAsync().GetAwaiter().GetResult().Checkpoint is null,
                "ordinary five-second reads do not copy a checkpoint");
            now = now.AddSeconds(1);
            Append(root, Context("work", now), Usage("root", "work", "r1", now, 10, 10));
            var paired = Read(provider);
            var checkpoint = paired.Checkpoint!;
            Check(checkpoint.Sequence == 2 && checkpoint.Generation == first.Checkpoint!.Generation && checkpoint.Counts.TotalTokens == 10
                && first.Checkpoint.Counts.TotalTokens == 0, "checkpoint sequence advances without mutating earlier observations");
            bool immutable = false;
            try { ((IList<UsageLedgerCursorCheckpoint>)checkpoint.Cursors).Clear(); }
            catch (NotSupportedException) { immutable = true; }
            Check(immutable, "captured cursor collection is immutable");
            string serialized = JsonSerializer.Serialize(checkpoint);
            var roundTrip = JsonSerializer.Deserialize<UsageLedgerCheckpoint>(serialized)!;
            Check(roundTrip.HasValidChecksum() && !serialized.Contains("token_usage_record") && !serialized.Contains("response_item"),
                "checkpoint JSON round trip retains numeric metadata without message bodies");
            now = now.AddSeconds(1);
            Append(root, Usage("root", "work", "r1", now, 999, 999), Usage("root", "work", "r2", now, 6, 16));
            string child = AddTask("child", now);
            Append(child, Context("work", now), Usage("child", "work", "c1", now, 4, 4));
            var restarted = new UsageLedgerProvider(home, () => now, roundTrip);
            var caughtUp = Read(restarted);
            Check(caughtUp.Health == SampleHealth.Fresh && caughtUp.Epoch == checkpoint.Epoch && caughtUp.Counts.TotalTokens == 20
                && caughtUp.Checkpoint?.Generation == checkpoint.Generation && caughtUp.Checkpoint.Sequence == checkpoint.Sequence + 1,
                "restart replays only offline increments, deduplicates old responses, and includes a demonstrably new task");
            var repeat = Read(restarted);
            Check(repeat.Counts.TotalTokens == 20 && repeat.Checkpoint?.Sequence == caughtUp.Checkpoint!.Sequence + 1,
                "repeated checkpoint reads never count caught-up records twice");
            var invalidCounter = checkpoint with { Counts = new TokenCounts(999, 999, 0, 0, 0, 999) };
            var rejectedCounter = Read(new UsageLedgerProvider(home, () => now, invalidCounter));
            Check(rejectedCounter.Health == SampleHealth.Partial && rejectedCounter.Epoch != checkpoint.Epoch
                && rejectedCounter.Counts.TotalTokens == 0 && rejectedCounter.Checkpoint is null,
                "damaged checkpoint counts cannot inherit a prior epoch or fabricate usage");
            var negative = (checkpoint with { Counts = new TokenCounts(-1, 0, 0, 0, 0, -1) }).Seal();
            Check(Read(new UsageLedgerProvider(home, () => now, negative)).Epoch != checkpoint.Epoch,
                "even a resealed checkpoint must satisfy numeric consistency");
            var badSequence = (checkpoint with { Sequence = 0 }).Seal();
            Check(Read(new UsageLedgerProvider(home, () => now, badSequence)).Epoch != checkpoint.Epoch,
                "checkpoint sequence must be positive");
            var duplicateIds = (checkpoint with { Cursors = new[] { checkpoint.Cursors[0] with { ResponseOrder = new[] { "r1", "r1" } } } }).Seal();
            Check(Read(new UsageLedgerProvider(home, () => now, duplicateIds)).Epoch != checkpoint.Epoch,
                "ambiguous response-deduplication metadata is rejected");
            string otherHome = Path.Combine(home, "other-home");
            Directory.CreateDirectory(Path.Combine(otherHome, "sessions"));
            Sql(Path.Combine(otherHome, "state_5.sqlite"), "CREATE TABLE threads(id TEXT,rollout_path TEXT);");
            var crossed = Read(new UsageLedgerProvider(otherHome, () => now, checkpoint));
            Check(crossed.Health == SampleHealth.Partial && crossed.Epoch != checkpoint.Epoch && crossed.Counts.TotalTokens == 0,
                "checkpoint cannot carry counters across Codex homes");
            now = now.AddSeconds(1);
            var beforeLate = Read(restarted).Checkpoint!;
            string late = AddTask("late-old-task", beforeLate.ObservedAt.AddSeconds(-1));
            Append(late, Context("old", now), Usage("late-old-task", "old", "old-r1", now, 500, 500));
            var lateRead = Read(new UsageLedgerProvider(home, () => now, beforeLate));
            Check(lateRead.Health == SampleHealth.Partial && lateRead.Epoch != beforeLate.Epoch && lateRead.Counts.TotalTokens == 0,
                "late discovery of a pre-checkpoint task cannot silently cross the saved watermark");

            foreach (string mode in new[] { "missing", "replacement", "truncated", "anchor", "schema" })
            {
                now = now.AddSeconds(1);
                File.WriteAllText(root, Meta("root", now.AddSeconds(-1)) + "\n" + Context("failure", now) + "\n"
                    + Usage("root", "failure", "baseline-" + mode, now, 10, 10) + "\n", new UTF8Encoding(false));
                var fresh = new UsageLedgerProvider(home, () => now);
                Read(fresh);
                now = now.AddSeconds(1);
                Append(root, Usage("root", "failure", "new-" + mode, now, 5, 15));
                var saved = Read(fresh).Checkpoint!;
                if (mode == "missing") File.Delete(root);
                else if (mode == "replacement")
                {
                    string replacement = root + ".replacement";
                    File.Copy(root, replacement);
                    File.Move(replacement, root, true);
                }
                else if (mode == "truncated") File.WriteAllText(root, Meta("root", now) + "\n", new UTF8Encoding(false));
                else if (mode == "anchor")
                {
                    using var file = new FileStream(root, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    file.Position = saved.Cursors.Single(cursor => cursor.ThreadId == "root").Offset - 2;
                    file.WriteByte((byte)'!');
                }
                else Sql(database, "ALTER TABLE threads ADD COLUMN changed_schema TEXT;");
                var broken = Read(new UsageLedgerProvider(home, () => now, saved));
                Check(broken.Health != SampleHealth.Fresh && broken.Epoch != saved.Epoch && broken.Counts.TotalTokens == 0
                    && broken.Checkpoint is null, mode + " breaks recovery and starts an explicit new baseline");
            }
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine("FAIL ledger checkpoint fixture: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
        Console.WriteLine($"Ledger checkpoint: {checks} checks, {failures} failure(s)");
        return failures;
    }
    private static int RunEstimatorCheckpointIntegration()
    {
        int failures = 0, checks = 0;
        void Check(bool valid, string scenario)
        {
            checks++;
            if (valid) return;
            failures++;
            Console.Error.WriteLine("FAIL checkpoint integration: " + scenario);
        }
        string home = Path.Combine(Path.GetTempPath(), "codex-hud-checkpoint-integration-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        string database = Path.Combine(home, "state_5.sqlite"), history = Path.Combine(home, "estimator.json");
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        var resetAt = now.AddHours(5);
        try
        {
            Sql(database, "CREATE TABLE threads(id TEXT PRIMARY KEY,rollout_path TEXT,model TEXT,reasoning_effort TEXT,model_provider TEXT,thread_source TEXT);");
            string AddTask(string id, DateTimeOffset created)
            {
                string path = Path.Combine(sessions, id + ".jsonl");
                File.WriteAllText(path, Meta(id, created) + "\n", new UTF8Encoding(false));
                Sql(database, $"INSERT INTO threads VALUES({Q(id)},{Q(path)},'gpt-6-astra','high','openai',NULL);");
                return path;
            }
            UsageLedgerSnapshot Read(UsageLedgerProvider provider) => provider.ReadCheckpointAsync().GetAwaiter().GetResult();
            QuotaWindow Window(double remaining) => new("codex-primary", "codex", "fixture", "primary", 300, remaining, resetAt);
            QuotaSnapshot Quota(double remaining) => new(now, new[] { Window(remaining) }, SampleHealth.Fresh) { AccountKey = "fixture-account" };
            string root = AddTask("root", now.AddDays(-1));
            var provider = new UsageLedgerProvider(home, () => now);
            var estimator = new QuotaTokenEstimator(history);
            estimator.Observe(Quota(100), Read(provider));
            now = now.AddSeconds(1);
            Append(root, Context("work", now), Usage("root", "work", "r1", now, 10, 10));
            var pendingUsage = Read(provider);
            estimator.Observe(Quota(99), pendingUsage);
            string pendingJson = File.ReadAllText(history);
            var pendingDocument = System.Text.Json.Nodes.JsonNode.Parse(pendingJson)!;
            var storedCheckpoint = pendingDocument["LedgerCheckpoint"]!.Deserialize<UsageLedgerCheckpoint>()!;
            Check(pendingDocument["Version"]!.GetValue<int>() == 3
                && storedCheckpoint.Generation == pendingUsage.Checkpoint!.Generation
                && storedCheckpoint.Sequence == pendingUsage.Checkpoint.Sequence && storedCheckpoint.Counts == pendingUsage.Counts,
                "one V3 write contains the checkpoint of its matching one-point quota observation");
            var restored = new QuotaTokenEstimator(history);
            Check(restored.RestoredLedgerCheckpoint is not null, "V3 exposes the valid paired ledger checkpoint for restart");
            now = now.AddSeconds(1);
            Append(root, Usage("root", "work", "r1", now, 10, 10), Usage("root", "work", "r2", now, 6, 16));
            string child = AddTask("child", now);
            Append(child, Context("work", now), Usage("child", "work", "c1", now, 4, 4));
            var resumedProvider = new UsageLedgerProvider(home, () => now, restored.RestoredLedgerCheckpoint);
            var resumedUsage = Read(resumedProvider);
            var resumedQuota = Quota(98);
            Check(resumedUsage.Health == SampleHealth.Fresh && resumedUsage.Counts.TotalTokens == 20
                && resumedUsage.Epoch == pendingUsage.Epoch, "offline replay counts exactly twenty workload tokens including the new task");
            restored.Observe(resumedQuota, resumedUsage);
            var closed = restored.Get(Window(98), now);
            Check(closed.Segments == 1 && closed.ObservedDrop == 2,
                "one point before restart plus one point after restart closes exactly one two-point segment");
            restored.Observe(resumedQuota, resumedUsage);
            Check(restored.Get(Window(98), now).Segments == 1, "replaying the paired observation cannot close the same segment again");
            for (int step = 1; step <= 2; step++)
            {
                now = now.AddSeconds(1);
                Append(root, Usage("root", "work", "later-" + step, now, 20, 16 + step * 20));
                restored.Observe(Quota(98 - 2 * step), Read(resumedProvider));
            }
            var trained = restored.Get(Window(94), now);
            Check(trained.Segments == 3 && trained.ObservedDrop == 6 && trained.RemainingTokens is > 0,
                "verified continuation produces three non-overlapping calibrated segments");
            string trainedJson = File.ReadAllText(history);

            var mixedDocument = System.Text.Json.Nodes.JsonNode.Parse(pendingJson)!;
            var mixed = (storedCheckpoint with { Generation = Guid.NewGuid().ToString("N") }).Seal();
            mixedDocument["LedgerCheckpoint"] = JsonSerializer.SerializeToNode(mixed);
            File.WriteAllText(history, mixedDocument.ToJsonString());
            var mixedEstimator = new QuotaTokenEstimator(history);
            now = now.AddSeconds(1);
            var mixedProvider = new UsageLedgerProvider(home, () => now, mixedEstimator.RestoredLedgerCheckpoint);
            var mixedUsage = Read(mixedProvider);
            mixedEstimator.Observe(Quota(94), mixedUsage);
            Check(mixedEstimator.Get(Window(94), now).Segments == 0,
                "a ledger from a different generation cannot bridge the saved pending anchor");

            var damagedDocument = System.Text.Json.Nodes.JsonNode.Parse(trainedJson)!;
            damagedDocument["LedgerCheckpoint"]!["Checksum"] = "damaged";
            File.WriteAllText(history, damagedDocument.ToJsonString());
            var damagedEstimator = new QuotaTokenEstimator(history);
            Check(damagedEstimator.RestoredLedgerCheckpoint is null,
                "damaged checkpoint is withheld independently from closed estimator history");
            now = now.AddSeconds(1);
            var freshProvider = new UsageLedgerProvider(home, () => now);
            damagedEstimator.Observe(Quota(94), Read(freshProvider));
            Check(damagedEstimator.Get(Window(94), now).Segments == 3,
                "discarding a damaged checkpoint does not discard the three already closed segments");
            for (int confirmation = 0; confirmation < 2; confirmation++)
            {
                now = now.AddSeconds(1);
                var unchanged = Read(freshProvider);
                Check(unchanged.Health == SampleHealth.Fresh && unchanged.Checkpoint is not null && unchanged.Counts.TotalTokens == 0,
                    "healthy restart confirmation has no newly invented workload tokens, observation " + confirmation);
                damagedEstimator.Observe(Quota(94), unchanged);
            }
            var reconfirmed = damagedEstimator.Get(Window(94), now);
            Check(reconfirmed.State is EstimateState.Estimated or EstimateState.Variable && reconfirmed.RemainingTokens is > 0
                && reconfirmed.Segments == 3 && reconfirmed.ObservedDrop == 6,
                "two healthy unchanged quota observations reactivate preserved calibration without any further drop");

            string noCheckpointHistory = Path.Combine(home, "no-checkpoint.json");
            File.WriteAllText(noCheckpointHistory, trainedJson);
            var noCheckpointEstimator = new QuotaTokenEstimator(noCheckpointHistory);
            var noCheckpointProvider = new UsageLedgerProvider(home, () => now, noCheckpointEstimator.RestoredLedgerCheckpoint);
            now = now.AddSeconds(1);
            Append(root, Usage("root", "work", "unpersisted-1", now, 10, 66));
            // A Fresh observation may lack a checkpoint when its metadata exceeds the capture budget.
            var uncaptured = Read(noCheckpointProvider) with { Checkpoint = null };
            Check(uncaptured.Health == SampleHealth.Fresh && uncaptured.Counts.TotalTokens == 70,
                "missing checkpoint does not turn a valid ledger observation into unknown numeric usage");
            noCheckpointEstimator.Observe(Quota(93), uncaptured);
            var unpairedDocument = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(noCheckpointHistory))!;
            Check(unpairedDocument["Windows"]!.AsArray().All(window => window?["Anchor"] is null),
                "Fresh pairing without a new checkpoint cannot persist its new anchor against an older checkpoint");
            var afterUncaptured = new QuotaTokenEstimator(noCheckpointHistory);
            now = now.AddSeconds(1);
            Append(root, Usage("root", "work", "unpersisted-2", now, 10, 76));
            var afterUncapturedProvider = new UsageLedgerProvider(home, () => now, afterUncaptured.RestoredLedgerCheckpoint);
            var afterUncapturedUsage = Read(afterUncapturedProvider);
            afterUncaptured.Observe(Quota(92), afterUncapturedUsage);
            Check(afterUncaptured.Get(Window(92), now).Segments == 3,
                "restart cannot join an unpersisted one-point anchor to the older saved checkpoint to fabricate another segment");
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine("FAIL checkpoint integration fixture: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
        Console.WriteLine($"Checkpoint integration: {checks} checks, {failures} failure(s)");
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
    private static string Context(string turn, DateTimeOffset at, string model = "gpt-6-astra", string? tier = null, string effort = "high") =>
        JsonSerializer.Serialize(new { timestamp = at, type = "turn_context", payload = new { turn_id = turn, model, effort, service_tier = tier } });
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
