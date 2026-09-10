using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHud.Core;

public static class SessionCostTests
{
    public static int Run()
    {
        int failures = 0, checks = 0;
        void Check(bool condition, string scenario)
        {
            checks++;
            if (condition) return;
            failures++;
            Console.Error.WriteLine("FAIL session cost: " + scenario);
        }
        void Scenario(string name, Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine("FAIL session cost " + name + ": " + error.GetType().Name + ": " + error.Message);
            }
        }

        Scenario("self and recursive archived agents", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            string child = fixture.Add("child", parent: "root", fork: "root");
            string nested = fixture.Add("nested", parent: "child", archived: true, fork: "child");
            string review = fixture.Add("review", parent: "root", review: true);
            Append(root, fixture.Usage("root", "r1"));
            // A spawned agent can carry a copied parent header and context. Only its first header owns the file.
            Append(child, fixture.Meta("root"), fixture.Context("parent-turn", "unpriced-parent-model"),
                fixture.Usage("root", "r1"), fixture.Context("one"), fixture.Usage("child", "c1"));
            Append(nested, fixture.Usage("nested", "n1"));
            Append(review, fixture.Usage("review", "g1"));
            using var provider = new SessionCostProvider(fixture.Home);
            provider.SetTrackedThreads(["root"]);
            var result = Read(provider);
            var main = result.Tasks["root"];
            Check(main.SelfUsd == RequestUsd && main.DescendantsUsd == 2 * RequestUsd
                && main.TotalUsd == 3 * RequestUsd, "root total includes own usage and recursively archived child usage once");
            Check(main.DescendantCount == 2, "guardian review is excluded from recursive descendant count");
            Check(main.PricedResponses == 3 && main.UnpricedResponses == 0,
                "copied parent records and guardian usage cannot increase response counts");
            Check(main.SelfUpperUsd == main.SelfUsd && main.TotalUpperUsd == main.TotalUsd,
                "complete cache breakdown produces an exact lower and upper estimate");
            Check(result.Tasks["child"].IsSubagent && result.Tasks["child"].SelfUsd == RequestUsd,
                "first child header remains authoritative after an inherited parent header");
            Check(!main.IsSubagent && main.LastUsageAt is not null, "root identity and latest usage timestamp retained");
            Check(main.Notes.Any(), "missing service tier is reported as a standard-price assumption");
        });

        Scenario("multiple segments, inherited records, and fork", () =>
        {
            using var fixture = new Fixture();
            string first = fixture.Add("root", session: "old");
            Append(first, fixture.Usage("root", "r1", session: "old"));
            string resumed = fixture.Add("root", session: "new", suffix: "new", historyBase: true);
            Append(resumed, fixture.Usage("root", "r1", session: "old"),
                fixture.Usage("root", "r2", cumulative: 2, session: "new"));
            string fork = fixture.Add("fork", fork: "root");
            Append(fork, fixture.Meta("root"), fixture.Context("one", "unknown-inherited-model"),
                fixture.Usage("root", "r1", session: "old"), fixture.Context("one"), fixture.Usage("fork", "f1"));
            using var provider = new SessionCostProvider(fixture.Home);
            var result = Read(provider);
            Check(result.Tasks["root"].SelfUsd == 2 * RequestUsd && result.Tasks["root"].PricedResponses == 2,
                "same-owner history segments and repeated response identities count once");
            Check(result.Tasks["root"].DescendantCount == 0 && result.Tasks["root"].TotalUsd == 2 * RequestUsd,
                "independent user fork is not added to its source conversation");
            Check(result.Tasks["fork"].TotalUsd == RequestUsd && !result.Tasks["fork"].IsSubagent,
                "fork cost begins with its own response rather than copied context");
            var again = Read(provider);
            Check(again.Tasks["root"].TotalUsd == result.Tasks["root"].TotalUsd,
                "repeated reads never add cumulative snapshots");
        });

        Scenario("duplicate across valid same-owner segments", () =>
        {
            using var fixture = new Fixture();
            string first = fixture.Add("root", session: "same");
            Append(first, fixture.Usage("root", "r1", session: "same"));
            string duplicate = fixture.Add("root", session: "same", suffix: "copy", archived: true);
            Append(duplicate, fixture.Usage("root", "r1", session: "same"),
                fixture.Usage("root", "r2", cumulative: 2, session: "same"));
            using var provider = new SessionCostProvider(fixture.Home);
            var result = Read(provider).Tasks["root"];
            Check(result.SelfUsd == 2 * RequestUsd && result.PricedResponses == 2,
                "response deduplication spans session and archive directories");
        });

        Scenario("archive move keeps complete lifetime cost", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            Append(root, fixture.Usage("root", "r1"));
            using var provider = new SessionCostProvider(fixture.Home);
            var before = Read(provider).Tasks["root"];
            string archived = Path.Combine(fixture.Home, "archived_sessions", Path.GetFileName(root));
            File.Move(root, archived);
            var moved = Read(provider).Tasks["root"];
            Check(moved.SelfUsd == before.SelfUsd && moved.PricedResponses == 1,
                "archive move keeps the same response identity and lifetime subtotal");
            Check(moved.Health == SampleHealth.Fresh,
                "readable archive alias replaces the old pathname without creating a permanent stale source");
        });

        Scenario("duplicate response with conflicting price profile", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            Append(root, fixture.Context("one", tier: "standard"), fixture.Usage("root", "r1"));
            string conflicting = fixture.Add("root", suffix: "conflict", archived: true);
            Append(conflicting, fixture.Context("one", tier: "priority"), fixture.Usage("root", "r1"));
            using var provider = new SessionCostProvider(fixture.Home);
            var result = Read(provider).Tasks["root"];
            Check(result.PricedResponses == 1 && result.Health != SampleHealth.Fresh,
                "same response with incompatible known pricing profiles is deduplicated and reported incomplete");
            Check(result.Notes.Any(note => note.Contains("冲突", StringComparison.Ordinal)),
                "duplicate price profile conflict has a visible explanation");
        });

        Scenario("model attribution, absent context, unknown prices", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            Append(root, fixture.Usage("root", "known"), fixture.Context("unknown", "model-with-no-public-price"),
                fixture.Usage("root", "unknown", cumulative: 2, turn: "unknown"),
                fixture.Usage("root", "missing-context", cumulative: 3, turn: "no-context"));
            using var provider = new SessionCostProvider(fixture.Home);
            var result = Read(provider).Tasks["root"];
            Check(result.SelfUsd == RequestUsd && result.PricedResponses == 1 && result.UnpricedResponses == 2,
                "unknown model and missing exact turn context remain unpriced without borrowing latest model");
            Check(result.UnpricedTokens >= 240 && result.Health != SampleHealth.Fresh,
                "unpriced usage is visible and prevents a complete-cost claim");
            Check(result.Notes.Any(), "unknown model or context has an explanatory note");
        });

        Scenario("legacy activity without response usage remains unknown", () =>
        {
            using var fixture = new Fixture();
            string legacy = fixture.Add("legacy");
            Append(legacy, JsonSerializer.Serialize(new
            {
                timestamp = fixture.At.AddSeconds(1), type = "event_msg", payload = new
                {
                    type = "token_count", info = new
                    {
                        total_token_usage = new { input_tokens = 100L, cached_input_tokens = 40L,
                            output_tokens = 20L, reasoning_output_tokens = 5L, total_tokens = 120L },
                        last_token_usage = new { input_tokens = 100L, cached_input_tokens = 40L,
                            output_tokens = 20L, reasoning_output_tokens = 5L, total_tokens = 120L }
                    }
                }
            }));
            fixture.Add("history-only", historyBase: true);
            using var provider = new SessionCostProvider(fixture.Home);
            var result = Read(provider);
            var oldFormat = result.Tasks["legacy"];
            Check(oldFormat.PricedResponses == 0 && oldFormat.Health == SampleHealth.Partial,
                "nonzero legacy token_count without identified response usage cannot appear as complete zero cost");
            Check(oldFormat.Notes.Any(), "legacy usage without priceable response records explains missing evidence");
            Check(result.Tasks["history-only"].Health == SampleHealth.Partial
                && result.Tasks["history-only"].PricedResponses == 0,
                "history_base without recovered response records cannot certify a zero lifetime cost");
        });

        Scenario("owner and session validation", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            Append(root, fixture.Usage("other", "foreign"), fixture.Usage("root", "wrong-session", session: "unrelated"),
                fixture.Usage("root", "valid"));
            using var provider = new SessionCostProvider(fixture.Home);
            var result = Read(provider).Tasks["root"];
            Check(result.SelfUsd == RequestUsd && result.PricedResponses == 1,
                "foreign owner and unrelated session cannot be charged to a file owner");
        });

        Scenario("model and service tier follow each exact turn", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            Append(root, fixture.Usage("root", "standard"), fixture.Context("fast-turn", tier: "priority"),
                fixture.Usage("root", "priority", cumulative: 2, turn: "fast-turn"),
                fixture.Context("sol-turn", "gpt-5.6-sol"),
                fixture.Usage("root", "sol", cumulative: 3, turn: "sol-turn"));
            using var provider = new SessionCostProvider(fixture.Home);
            var result = Read(provider).Tasks["root"];
            Check(result.SelfUsd == 0.005661m && result.PricedResponses == 3,
                "standard Astra, priority Astra and standard Sol prices follow the matching turn context");
            Check(result.UnpricedResponses == 0 && result.SelfUpperUsd == result.SelfUsd,
                "missing tier on a new turn uses annotated standard pricing rather than carrying previous priority");
        });

        Scenario("long context uses individual response input", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            Append(root, fixture.Usage("root", "large", input: 272001, cached: 100000, written: 10000, output: 1000));
            using var provider = new SessionCostProvider(fixture.Home);
            var result = Read(provider).Tasks["root"];
            Check(result.SelfUsd == 3.765020m,
                "per-response input above 272K reaches the official long-context pricing rule");
        });

        Scenario("missing earlier segment", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root", session: "resumed", historyBase: true);
            Append(root, fixture.Usage("root", "latest", cumulative: 4, session: "resumed"));
            using var provider = new SessionCostProvider(fixture.Home);
            var result = Read(provider).Tasks["root"];
            Check(result.SelfUsd == RequestUsd && result.PricedResponses == 1,
                "latest cumulative total is never priced as if all historical tokens used today's model");
            Check(result.Health != SampleHealth.Fresh && result.UnpricedTokens >= 360,
                "missing lifetime records are reconciled against the explicit cumulative total");
            Check(result.Notes.Any(), "missing predecessor segment is disclosed");
        });

        Scenario("cache write and incomplete token breakdown", () =>
        {
            using var fixture = new Fixture();
            string known = fixture.Add("known");
            Append(known, fixture.Usage("known", "known"));
            string missing = fixture.Add("missing");
            Append(missing, fixture.Usage("missing", "missing-cache", omitCached: true));
            string missingWrite = fixture.Add("missing-write");
            Append(missingWrite, fixture.Usage("missing-write", "missing-write", omitWritten: true));
            string invalid = fixture.Add("invalid");
            Append(invalid, fixture.Usage("invalid", "invalid-cache", cached: 110));
            using var provider = new SessionCostProvider(fixture.Home);
            var result = Read(provider);
            Check(result.Tasks["known"].SelfUsd == 0.001665m,
                "cache-write tokens use their price, cached input is discounted, and reasoning stays within output");
            Check(result.Tasks["missing-write"].SelfUsd == 0.001640m
                && result.Tasks["missing-write"].SelfUpperUsd == 0.001790m,
                "unrecorded cache-write tokens yield the published zero-to-all-written cost range");
            var partial = result.Tasks["missing"];
            Check(partial.SelfUpperUsd >= partial.SelfUsd && partial.Health != SampleHealth.Fresh
                && partial.Notes.Any(), "missing cache breakdown yields an explicit uncertainty rather than false precision");
            Check(result.Tasks["invalid"].UnpricedResponses > 0 && result.Tasks["invalid"].Health != SampleHealth.Fresh,
                "cached input beyond total input cannot produce negative billable input");
        });

        Scenario("incremental bounded backfill", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            string filler = JsonSerializer.Serialize(new { timestamp = fixture.At, type = "response_item",
                payload = new { type = "message", content = new string('x', 4096) } });
            for (int index = 0; index < 180; index++) Append(root, filler);
            Append(root, fixture.Usage("root", "r1"));
            using var provider = new SessionCostProvider(fixture.Home, bytesPerRead: 64 * 1024);
            var first = provider.ReadAsync().GetAwaiter().GetResult();
            Check(!first.Tasks.TryGetValue("root", out var incomplete) || incomplete.PricedResponses == 0,
                "one bounded read does not scan a full historical log beyond its budget");
            var result = Read(provider).Tasks["root"];
            Check(result.SelfUsd == RequestUsd && result.PricedResponses == 1,
                "bounded backfill eventually reaches historical usage without skipping oversized history");
        });

        Scenario("unfinished line and unchanged snapshot", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            Append(root, fixture.Usage("root", "r1"));
            using var provider = new SessionCostProvider(fixture.Home);
            var before = Read(provider).Tasks["root"];
            string pending = fixture.Usage("root", "r2", cumulative: 2);
            File.AppendAllText(root, pending[..(pending.Length / 2)], Utf8);
            var incomplete = Read(provider).Tasks["root"];
            Check(incomplete.SelfUsd == before.SelfUsd && incomplete.Health != SampleHealth.Fresh,
                "incomplete JSON preserves the previously known subtotal with incomplete status");
            File.AppendAllText(root, pending[(pending.Length / 2)..] + "\n", Utf8);
            var complete = Read(provider).Tasks["root"];
            Check(complete.SelfUsd == 2 * RequestUsd && complete.PricedResponses == 2,
                "a completed appended record is committed once");
            Check(before.SelfUsd == RequestUsd && before.PricedResponses == 1,
                "previously returned estimates remain immutable as new responses arrive");
        });

        Scenario("replacement and truncation retain old evidence", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            Append(root, fixture.Usage("root", "r1"), fixture.Usage("root", "r2", cumulative: 2));
            using var provider = new SessionCostProvider(fixture.Home);
            var before = Read(provider).Tasks["root"];
            File.WriteAllText(root, fixture.Meta("root") + "\n" + fixture.Context("one") + "\n", Utf8);
            var truncated = Read(provider).Tasks["root"];
            Check(truncated.SelfUsd >= before.SelfUsd && truncated.Health != SampleHealth.Fresh,
                "truncated source cannot erase the previously verified lifetime subtotal");
            string replacement = root + ".replacement";
            File.WriteAllText(replacement, fixture.Meta("root") + "\n" + fixture.Context("one") + "\n"
                + fixture.Usage("root", "r1") + "\n", Utf8);
            File.Move(replacement, root, true);
            var replaced = Read(provider).Tasks["root"];
            Check(replaced.SelfUsd >= before.SelfUsd && replaced.Health != SampleHealth.Fresh,
                "replacement preserves historical cost evidence as stale instead of silently resetting");
        });

        Scenario("replacement header refreshes same-owner session identity", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            Append(root, fixture.Usage("root", "r1"));
            using var provider = new SessionCostProvider(fixture.Home);
            Read(provider);
            string replacement = root + ".replacement";
            File.WriteAllText(replacement, fixture.Meta("root", session: "new-session") + "\n"
                + fixture.Context("one") + "\n"
                + fixture.Usage("root", "r2", cumulative: 2, session: "new-session") + "\n", Utf8);
            File.Move(replacement, root, true);
            var result = Read(provider).Tasks["root"];
            Check(result.SelfUsd == 2 * RequestUsd && result.PricedResponses == 2,
                "same-owner replacement refreshes session identity so new own responses still accumulate");
            Check(result.Health != SampleHealth.Fresh,
                "accepting replacement-session usage retains the historical discontinuity warning");
        });

        Scenario("empty task-card projection still populates the complete ledger", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            Append(root, fixture.Usage("root", "inactive-response"));
            string checkpoint = Path.Combine(fixture.Home, "disabled-checkpoint.json");
            using (var disabled = new SessionCostProvider(fixture.Home, checkpoint))
            {
                disabled.SetTrackedThreads([]);
                var snapshot = Read(disabled);
                Check(snapshot.Tasks.Count == 0,
                    "an explicitly empty tracked set keeps the legacy task-card projection empty");
                Check(snapshot.Ledger?.Responses.Single().ResponseId == "inactive-response",
                    "the complete period ledger keeps collecting while task cards are disabled");
            }
            Check(File.Exists(checkpoint) && File.ReadAllText(checkpoint).Contains("inactive-response", StringComparison.Ordinal),
                "the shared accounting checkpoint retains period usage independently of task-card visibility");
            using var enabled = new SessionCostProvider(fixture.Home, checkpoint);
            enabled.SetTrackedThreads(["root"]);
            Check(Read(enabled).Tasks["root"].SelfUsd == RequestUsd,
                "first enable backfills responses created before monitoring was active");
        });

        Scenario("checkpoint restart and feature disabled", () =>
        {
            using var fixture = new Fixture();
            string root = fixture.Add("root");
            Append(root, fixture.Usage("root", "r1"));
            string checkpoint = Path.Combine(fixture.Home, "cost-checkpoint.json");
            using (var provider = new SessionCostProvider(fixture.Home, checkpoint))
            {
                provider.SetTrackedThreads(["root"]);
                Check(Read(provider).Tasks["root"].SelfUsd == RequestUsd, "enabled tracking backfills existing usage");
                provider.SetTrackedThreads([]);
                Append(root, fixture.Usage("root", "r2", cumulative: 2));
                var disabled = Read(provider);
                Check(!disabled.Tasks.TryGetValue("root", out var dormant) || dormant.SelfUsd == RequestUsd,
                    "empty tracked set keeps the task-card projection empty while shared collection continues");
                provider.SetTrackedThreads(["root"]);
                Check(Read(provider).Tasks["root"].SelfUsd == 2 * RequestUsd,
                    "reenabling catches up usage created while display was disabled");
            }
            Check(File.Exists(checkpoint), "completed checkpoint is persisted when the provider is disposed");
            Append(root, fixture.Usage("root", "r3", cumulative: 3));
            using var resumed = new SessionCostProvider(fixture.Home, checkpoint);
            var result = Read(resumed).Tasks["root"];
            Check(result.SelfUsd == 3 * RequestUsd && result.PricedResponses == 3,
                "restart reconstructs lifetime total and adds offline usage without duplication");
            if (File.Exists(checkpoint))
            {
                string saved = File.ReadAllText(checkpoint);
                Check(!saved.Contains("conversation-secret", StringComparison.Ordinal),
                    "checkpoint contains accounting metadata without stored conversation bodies");
            }
        });

        Scenario("checkpoint restores dated Unicode usage without readable logs on every restart", () =>
        {
            using var fixture = new Fixture(DateTimeOffset.UtcNow.AddDays(-1).ToOffset(TimeSpan.FromHours(9)));
            const string id = "任务-中文", response = "响应-α";
            string root = fixture.Add(id);
            Append(root, fixture.Usage(id, response));
            string checkpoint = Path.Combine(fixture.Home, "cost-checkpoint.json");
            using (var provider = new SessionCostProvider(fixture.Home, checkpoint))
                Check(Read(provider).Ledger!.Responses.Single().ResponseId == response, "dated Unicode fixture is collected before saving");
            Check(ValidDocumentChecksum(checkpoint), "new checkpoint uses the same document checksum when saving and restoring");
            File.Move(root, Path.Combine(fixture.Home, "source-offline.jsonl"));
            for (int restart = 0; restart < 3; restart++)
            {
                using var provider = new SessionCostProvider(fixture.Home, checkpoint, bytesPerRead: 1);
                var snapshot = provider.ReadAsync().GetAwaiter().GetResult();
                var restored = snapshot.Ledger!.Responses.SingleOrDefault();
                Check(restored?.ResponseId == response && restored.RecordedAt == fixture.At.AddSeconds(1)
                    && restored.RecordedAt?.Offset == TimeSpan.FromHours(9)
                    && snapshot.Tasks[id].SelfUsd == RequestUsd && snapshot.Tasks[id].PricedResponses == 1,
                    $"restart {restart + 1} restores counters, Unicode identity and timestamp before any log replay");
                Check(snapshot.Ledger.Health == SampleHealth.Stale, "unavailable source is disclosed while retaining verified cached usage");
            }
        });

        Scenario("legacy v2 checkpoint is retained and rewritten before another restart", () =>
        {
            using var fixture = new Fixture(DateTimeOffset.UtcNow.AddDays(-1).ToOffset(TimeSpan.FromMinutes(330)));
            string root = fixture.Add("legacy-中文");
            Append(root, fixture.Usage("legacy-中文", "旧响应"));
            string checkpoint = Path.Combine(fixture.Home, "cost-checkpoint.json");
            using (var provider = new SessionCostProvider(fixture.Home, checkpoint)) Read(provider);
            WriteLegacyChecksum(checkpoint);
            Check(!ValidDocumentChecksum(checkpoint), "fixture reproduces the old typed-date checksum mismatch");
            File.Move(root, Path.Combine(fixture.Home, "source-offline.jsonl"));
            using (var provider = new SessionCostProvider(fixture.Home, checkpoint, bytesPerRead: 1))
            {
                var snapshot = provider.ReadAsync().GetAwaiter().GetResult();
                Check(snapshot.Ledger!.Responses.SingleOrDefault()?.ResponseId == "旧响应"
                    && snapshot.Tasks["legacy-中文"].SelfUsd == RequestUsd,
                    "legacy v2 response evidence survives validation without rescanning a source");
            }
            Check(ValidDocumentChecksum(checkpoint), "legacy acceptance marks the unchanged checkpoint dirty for canonical resave");
            using var resumed = new SessionCostProvider(fixture.Home, checkpoint, bytesPerRead: 1);
            Check(resumed.ReadAsync().GetAwaiter().GetResult().Ledger!.Responses.SingleOrDefault()?.ResponseId == "旧响应",
                "the migrated checksum restores on the next restart without readable logs");
        });

        Scenario("tampered canonical and legacy checkpoints are not restored", () =>
        {
            foreach (bool legacy in new[] { false, true })
            {
                using var fixture = new Fixture(DateTimeOffset.UtcNow.AddDays(-1).ToOffset(TimeSpan.FromHours(9)));
                string root = fixture.Add("root");
                Append(root, fixture.Usage("root", "r1"));
                string checkpoint = Path.Combine(fixture.Home, "cost-checkpoint.json");
                using (var provider = new SessionCostProvider(fixture.Home, checkpoint)) Read(provider);
                if (legacy) WriteLegacyChecksum(checkpoint);
                string saved = File.ReadAllText(checkpoint);
                string tampered = saved.Replace("\"OutputTokens\":20", "\"OutputTokens\":20000", StringComparison.Ordinal);
                Check(tampered != saved, "tamper fixture changes stored numeric evidence");
                File.WriteAllText(checkpoint, tampered, Utf8);
                File.Move(root, Path.Combine(fixture.Home, "source-offline.jsonl"));
                using var providerAfterTamper = new SessionCostProvider(fixture.Home, checkpoint, bytesPerRead: 1);
                Check(providerAfterTamper.ReadAsync().GetAwaiter().GetResult().Ledger!.Responses.Count == 0,
                    $"{(legacy ? "legacy" : "canonical")} checksum mismatch cannot restore modified counters");
            }
        });

        failures += UsageHistoryTests.Run();
        Console.WriteLine($"Session cost: {checks} checks, {failures} failure(s)");
        return failures;
    }

    private const decimal RequestUsd = 0.001665m;
    private static readonly UTF8Encoding Utf8 = new(false);

    private static bool ValidDocumentChecksum(string path)
    {
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        string declared = document["Checksum"]!.GetValue<string>();
        document["Checksum"] = "";
        return declared == UsageLedgerCheckpoint.Hash(JsonSerializer.Serialize(document));
    }

    private static void WriteLegacyChecksum(string path)
    {
        // Preserve the original typed DateTimeOffset representation with literal '+'; the old
        // writer hashed these exact bytes with only its Checksum property blanked.
        string saved = File.ReadAllText(path);
        string declared = JsonNode.Parse(saved)!["Checksum"]!.GetValue<string>();
        string unsigned = saved.Replace($"\"Checksum\":\"{declared}\"", "\"Checksum\":\"\"", StringComparison.Ordinal);
        File.WriteAllText(path, saved.Replace(declared, UsageLedgerCheckpoint.Hash(unsigned), StringComparison.Ordinal), Utf8);
    }

    // Several reads allow small shared budgets and metadata discovery to catch up without wall-clock sleeps.
    private static SessionCostSnapshot Read(SessionCostProvider provider)
    {
        var result = provider.ReadAsync().GetAwaiter().GetResult();
        for (int index = 0; index < 31; index++) result = provider.ReadAsync().GetAwaiter().GetResult();
        return result;
    }

    private static void Append(string path, params string[] lines) =>
        File.AppendAllText(path, string.Join("\n", lines) + "\n", Utf8);

    private sealed class Fixture : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "codex-hud-cost-tests-" + Guid.NewGuid().ToString("N"));
        public DateTimeOffset At { get; }

        public Fixture(DateTimeOffset? at = null)
        {
            At = at ?? DateTimeOffset.UtcNow.AddDays(-1);
            Directory.CreateDirectory(Path.Combine(Home, "sessions", "2026", "09", "08"));
            Directory.CreateDirectory(Path.Combine(Home, "archived_sessions"));
        }

        public string Add(string id, string? parent = null, bool archived = false, bool review = false,
            string? fork = null, string session = "session", string? suffix = null, bool historyBase = false)
        {
            string directory = archived ? Path.Combine(Home, "archived_sessions") : Path.Combine(Home, "sessions", "2026", "09", "08");
            string path = Path.Combine(directory, "rollout-2026-09-08T00-00-00-" + id + (suffix is null ? "" : "_" + suffix) + ".jsonl");
            Append(path, Meta(id, parent, review, fork, session, historyBase), Context("one"),
                JsonSerializer.Serialize(new { timestamp = At, type = "response_item",
                    payload = new { type = "message", content = "conversation-secret" } }));
            return path;
        }

        public string Meta(string id, string? parent = null, bool review = false, string? fork = null,
            string session = "session", bool historyBase = false) => JsonSerializer.Serialize(new
        {
            timestamp = At, type = "session_meta", payload = new
            {
                id, session_id = session, timestamp = At, parent_thread_id = parent, forked_from_id = fork,
                thread_source = review ? "guardian_review" : parent is null ? "user" : "subagent",
                source = parent is null ? (object)"vscode" : review ? new { subagent = new { other = "guardian" } }
                    : new { subagent = new { thread_spawn = new { parent_thread_id = parent, depth = 1 } } },
                model_provider = "openai", history_mode = "paginated",
                history_base = historyBase ? new { thread_id = id, end_ordinal_exclusive = 10, end_byte_offset = 1000 } : null
            }
        });

        public string Context(string turn, string model = "gpt-6-astra", string? tier = null) => JsonSerializer.Serialize(new
        {
            timestamp = At, type = "turn_context", payload = new { turn_id = turn, model, effort = "high", service_tier = tier }
        });

        public string Usage(string id, string response, int cumulative = 1, string session = "session", string turn = "one",
            bool omitCached = false, long cached = 40, bool omitWritten = false, long input = 100, long written = 10, long output = 20)
        {
            Dictionary<string, object?> Counts(int multiplier) => new()
            {
                ["input_tokens"] = input * multiplier, ["cached_input_tokens"] = omitCached ? null : cached * multiplier,
                ["cache_write_input_tokens"] = omitWritten ? null : written * multiplier, ["output_tokens"] = output * multiplier,
                ["reasoning_output_tokens"] = 5L * multiplier, ["total_tokens"] = (input + output) * multiplier
            };
            return JsonSerializer.Serialize(new
            {
                timestamp = At.AddSeconds(cumulative), type = "token_usage_record", payload = new
                {
                    thread_id = id, session_id = session, root_turn_id = turn, turn_id = turn, response_id = response,
                    usage = Counts(1), turn_token_usage = Counts(cumulative), thread_token_usage = Counts(cumulative)
                }
            });
        }

        public void Dispose() => Directory.Delete(Home, true);
    }
}
