using CodexHud.Core;

public static class BillingCycleTests
{
    private static readonly DateTimeOffset Start = new(2025, 5, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Key = "codex/primary/300";

    public static int Run()
    {
        int failures = 0, checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) { failures++; Console.Error.WriteLine("FAIL billing cycle: " + message); }
        }
        void Scenario(string title, Action action)
        {
            try { action(); }
            catch (Exception error) { failures++; Console.Error.WriteLine("FAIL billing cycle " + title + ": " + error.Message); }
        }

        Scenario("initial and weekly windows", () =>
        {
            var tracker = new BillingCycleTracker();
            tracker.Observe(Sample(Start.AddHours(1), 30));
            var first = tracker.GetPeriods(Key).Single();
            Check(first.StartedAt == Start && first.EndsAt == Start.AddHours(5)
                && first.IsEstimated && first.IsCurrent && first.Reason == "Initial", "initial scope is inferred from actual duration");
            var weekly = new QuotaWindow("codex/primary/10080", "codex", "Codex · 7 天", "primary", 10080, 80, Start.AddDays(7));
            tracker.Observe(Sample(Start.AddHours(2), 29) with { Windows = [weekly] });
            Check(tracker.GetPeriods(weekly.Key).Single().StartedAt == Start, "weekly is not assumed to be five hours");
        });
        Scenario("first detection, delayed confirmation and deduplication", () =>
        {
            var tracker = new BillingCycleTracker();
            tracker.Observe(Sample(Start, 20));
            var detected = Start.AddMinutes(1);
            tracker.Observe(Sample(detected, 100));
            var pending = tracker.GetPeriods(Key)[0];
            Check(pending.StartedAt == detected && pending.IsPending && pending.Reason == "Recovery"
                && pending.BoundaryEarliestAt == Start && tracker.RecheckAt == detected.AddSeconds(5), "first discovery establishes provisional boundary");
            tracker.Observe(Sample(detected.AddSeconds(4), 99));
            Check(tracker.GetPeriods(Key)[0].IsPending, "sample before five seconds cannot confirm");
            tracker.Observe(Sample(detected.AddSeconds(5), 98));
            var confirmed = tracker.GetPeriods(Key)[0];
            Check(!confirmed.IsPending && confirmed.Id == pending.Id && confirmed.StartedAt == detected
                && tracker.RecheckAt == null, "confirmation retains original boundary and id");
            tracker.Observe(Sample(detected.AddSeconds(5), 100));
            tracker.Observe(Sample(detected.AddSeconds(6), 97));
            Check(tracker.GetPeriods(Key).Count == 2 && tracker.GetPeriods(Key)[1].EndsAt == detected,
                "duplicate and normal consumption do not cut twice");
        });
        Scenario("brief rebound is cancelled", () =>
        {
            var tracker = new BillingCycleTracker();
            tracker.Observe(Sample(Start, 30));
            tracker.Observe(Sample(Start.AddSeconds(60), 31));
            tracker.Observe(Sample(Start.AddSeconds(65), 29));
            Check(tracker.GetPeriods(Key).Count == 1 && tracker.RecheckAt == null
                && tracker.GetPeriods(Key)[0].EndsAt == Start.AddHours(5), "cancelled rebound restores previous end");
        });
        Scenario("card count and expiry evidence", () =>
        {
            var tracker = new BillingCycleTracker();
            tracker.Observe(Sample(Start, 30, cards: 3));
            tracker.Observe(Sample(Start.AddSeconds(60), 100, cards: 2));
            Check(tracker.GetPeriods(Key)[0].Reason == "Card", "unexplained inventory drop plus rebound is card inference");
            tracker.Observe(Sample(Start.AddSeconds(65), 25, cards: 2));
            Check(tracker.GetPeriods(Key).Count == 1, "inventory alone cannot confirm a rebound that disappears");
            var expiry = new BillingCycleTracker();
            expiry.Observe(Sample(Start, 30, cards: 3) with
            { ResetCredits = new(3, Start, SampleHealth.Fresh) { Credits = [new("expires", Start.AddSeconds(30))] } });
            expiry.Observe(Sample(Start.AddSeconds(60), 100, cards: 2));
            Check(expiry.GetPeriods(Key)[0].Reason == "Recovery", "known expiry explains count decrease");
            var noRebound = new BillingCycleTracker();
            noRebound.Observe(Sample(Start, 30, cards: 3));
            noRebound.Observe(Sample(Start.AddSeconds(60), 29, cards: 2));
            Check(noRebound.GetPeriods(Key).Count == 1, "card count alone never cuts a period");
        });
        Scenario("confirmation retries and late card metadata", () =>
        {
            var tracker = new BillingCycleTracker();
            tracker.Observe(Sample(Start, 20));
            tracker.Observe(Sample(Start.AddMinutes(1), 100));
            var before = DateTimeOffset.UtcNow;
            tracker.Observe(Sample(Start.AddMinutes(1).AddSeconds(1), 99) with { Health = SampleHealth.Stale });
            Check(tracker.RecheckAt >= before.AddSeconds(5) && tracker.GetPeriods(Key)[0].IsPending,
                "failed verification backs off instead of polling in a tight loop");
            tracker.Observe(Sample(Start.AddMinutes(1).AddSeconds(5), 99, cards: 2));
            Check(tracker.GetPeriods(Key)[0].Reason == "Card" && !tracker.GetPeriods(Key)[0].IsPending,
                "card metadata arriving with confirmation upgrades the same boundary");
            var omitted = new BillingCycleTracker();
            omitted.Observe(Sample(Start, 20) with
            { ResetCredits = new(3, Start, SampleHealth.Fresh) { Credits = [new("expires-later", Start.AddSeconds(63))] } });
            omitted.Observe(Sample(Start.AddMinutes(1), 100));
            omitted.Observe(Sample(Start.AddMinutes(1).AddSeconds(5), 99, cards: 2));
            Check(omitted.GetPeriods(Key)[0].Reason == "Recovery", "omitted intermediate detail rows do not erase known card expiry");
            var missing = new BillingCycleTracker();
            missing.Observe(Sample(Start, 20));
            missing.Observe(Sample(Start.AddMinutes(1), 100));
            before = DateTimeOffset.UtcNow;
            missing.Observe(Sample(Start.AddMinutes(1).AddSeconds(5), 99) with { Windows = [] });
            Check(missing.RecheckAt >= before.AddSeconds(5), "missing target window also backs off verification");
            missing.Observe(Sample(Start.AddMinutes(1).AddSeconds(10), 99));
            Check(!missing.GetPeriods(Key)[0].IsPending, "fresh target window resumes verification");
        });
        Scenario("same batch card event and natural boundary", () =>
        {
            var tracker = new BillingCycleTracker();
            QuotaSnapshot Multi(DateTimeOffset at, double remaining, int cards) => Sample(at, remaining, cards: cards) with
            { Windows = [Window(remaining), Window(remaining) with { Key = "codex/secondary/10080", Slot = "secondary", WindowMinutes = 10080 }] };
            tracker.Observe(Multi(Start, 30, 3));
            tracker.Observe(Multi(Start.AddMinutes(1), 100, 2));
            var pending = tracker.GetPeriods(null).Where(p => p.IsPending).ToArray();
            Check(pending.Length == 2 && pending.Select(p => p.ResetEventId).Distinct().Count() == 1,
                "affected windows share a single reset event id");
            tracker.Observe(Multi(Start.AddMinutes(1).AddSeconds(5), 99, 2));
            Check(tracker.GetPeriods(null).Count == 4 && tracker.GetPeriods(null).Count(p => p.IsCurrent) == 2,
                "per-window confirmation does not duplicate account event");
            var natural = new BillingCycleTracker();
            natural.Observe(Sample(Start, 30, cards: 3));
            natural.Observe(Sample(Start.AddHours(5).AddSeconds(1), 100, Start.AddHours(10), cards: 2));
            natural.Observe(Sample(Start.AddHours(5).AddSeconds(6), 99, Start.AddHours(10), cards: 2));
            Check(natural.GetPeriods(Key).Count == 2 && natural.GetPeriods(Key)[0].Reason == "Card",
                "card and natural boundary in same observation create only one split");
        });
        Scenario("natural deadline and timestamp drift", () =>
        {
            var tracker = new BillingCycleTracker();
            tracker.Observe(Sample(Start, 30));
            tracker.Observe(Sample(Start.AddHours(4), 20, Start.AddHours(5).AddSeconds(60)));
            tracker.Observe(Sample(Start.AddHours(5).AddSeconds(1), 20, Start.AddHours(5).AddSeconds(60)));
            Check(tracker.GetPeriods(Key).Count == 1, "small reset metadata drift does not create a new cycle");
            tracker.Observe(Sample(Start.AddHours(5).AddSeconds(61), 100, Start.AddHours(10)));
            Check(tracker.GetPeriods(Key).Count == 2 && tracker.GetPeriods(Key)[0].StartedAt == Start.AddHours(5)
                && tracker.GetPeriods(Key)[0].Reason == "Natural" && !tracker.GetPeriods(Key)[0].IsPending,
                "natural boundary uses accepted deadline without polling delay");
            tracker.Observe(Sample(Start.AddHours(5).AddSeconds(66), 99, Start.AddHours(10)));
            Check(tracker.GetPeriods(Key).Count == 2, "natural reset cannot split again on next sample");
            var corrected = new BillingCycleTracker();
            corrected.Observe(Sample(Start, 30));
            corrected.Observe(Sample(Start.AddHours(1), 29, Start.AddHours(6)));
            corrected.Observe(Sample(Start.AddHours(5).AddSeconds(1), 28, Start.AddHours(6)));
            Check(corrected.GetPeriods(Key).Count == 1, "metadata extension alone never proves a reset at old deadline");
        });
        Scenario("restart and hidden gap retain first discovery", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "codex-billing-cycle-tests-" + Guid.NewGuid().ToString("N"));
            string checkpoint = Path.Combine(directory, "periods.json");
            try
            {
                var tracker = new BillingCycleTracker(checkpoint);
                tracker.Observe(Sample(Start, 10, cards: 3));
                var detected = Start.AddSeconds(120);
                tracker.Observe(Sample(detected, 100, cards: 2));
                string id = tracker.GetPeriods(Key)[0].Id;
                var restored = new BillingCycleTracker(checkpoint);
                Check(restored.GetPeriods(Key).Count == 2 && restored.RecheckAt == detected.AddSeconds(5), "pending state is persisted atomically");
                restored.Observe(Sample(detected.AddSeconds(5), 99, cards: 2));
                var confirmed = new BillingCycleTracker(checkpoint);
                Check(confirmed.GetPeriods(Key)[0].Id == id && !confirmed.GetPeriods(Key)[0].IsPending
                    && confirmed.GetPeriods(Key)[0].StartedAt == detected, "restart confirms original first detection");
                confirmed.Observe(Sample(detected.AddSeconds(10), 98, cards: 2));
                Check(confirmed.GetPeriods(Key).Count == 2 && !File.Exists(checkpoint + ".tmp"), "confirmed event is not replayed after restart");
                File.WriteAllText(checkpoint, "{\"Version\":99}");
                var invalid = new BillingCycleTracker(checkpoint);
                Check(invalid.GetPeriods(Key).Count == 0 && invalid.Detail?.Contains("不可用") == true,
                    "unsupported checkpoint does not fabricate history");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        Scenario("offline windows leave unknown interval unassigned", () =>
        {
            var tracker = new BillingCycleTracker();
            tracker.Observe(Sample(Start, 30));
            tracker.Observe(Sample(Start.AddHours(16), 80, Start.AddHours(20)));
            var periods = tracker.GetPeriods(Key);
            Check(periods.Count == 2 && periods[0].StartedAt == Start.AddHours(15) && periods[0].IsEstimated
                && periods[1].EndsAt == Start.AddHours(5), "unobserved cycles are not appended to old period");
        });
        Scenario("manual split and correction", () =>
        {
            var tracker = new BillingCycleTracker();
            tracker.Observe(Sample(Start, 30));
            Check(tracker.AddManualReset(Key, Start.AddHours(1), out _), "manual reset can be added inside known period");
            string id = tracker.GetPeriods(Key)[0].Id;
            Check(tracker.CorrectStart(id, Start.AddMinutes(30), out _) && tracker.GetPeriods(Key)[0].StartedAt == Start.AddMinutes(30)
                && tracker.GetPeriods(Key)[1].EndsAt == Start.AddMinutes(30) && !tracker.GetPeriods(Key)[0].IsEstimated,
                "manual correction updates both adjacent boundaries");
            Check(!tracker.CorrectStart(id, Start, out _) && !tracker.AddManualReset(Key, Start.AddMinutes(30), out _)
                && !tracker.CorrectStart(id, Start.AddHours(5), out _), "invalid overlap and duplicate boundaries are rejected");
            tracker.Observe(Sample(Start.AddHours(2), 29));
            Check(tracker.GetPeriods(Key)[0].StartedAt == Start.AddMinutes(30), "new quota observations preserve manual override");
            tracker.Observe(Sample(Start.AddHours(3), 100));
            string pendingId = tracker.GetPeriods(Key)[0].Id;
            Check(!tracker.AddManualReset(Key, Start.AddHours(2), out _), "manual insertion cannot invalidate pending rollback state");
            Check(tracker.CorrectStart(pendingId, Start.AddHours(2), out _), "pending estimated reset can be directly corrected");
            tracker.Observe(Sample(Start.AddHours(3).AddSeconds(6), 99));
            Check(tracker.GetPeriods(Key).Count == 3 && tracker.GetPeriods(Key)[0].StartedAt == Start.AddHours(2)
                && !tracker.GetPeriods(Key)[0].IsPending, "correcting pending boundary commits it without later duplicate");
        });
        Scenario("confirmed recovery accepts the final endpoint", () =>
        {
            var tracker = new BillingCycleTracker();
            tracker.Observe(Sample(Start, 20));
            tracker.Observe(Sample(Start.AddMinutes(1), 100));
            string id = tracker.GetPeriods(Key)[0].Id;
            tracker.Observe(Sample(Start.AddMinutes(1).AddSeconds(5), 99, Start.AddHours(6)));
            Check(tracker.GetPeriods(Key)[0].Id == id && tracker.GetPeriods(Key)[0].EndsAt == Start.AddHours(6)
                && tracker.GetPeriods(Key)[0].StartedAt == Start.AddMinutes(1), "confirmation updates delayed endpoint without moving first detected start");
            tracker.Observe(Sample(Start.AddHours(5).AddMinutes(1), 80, Start.AddHours(6)));
            Check(tracker.GetPeriods(Key).Count == 2 && tracker.GetPeriods(Key)[0].EndsAt > Start.AddHours(5).AddMinutes(1),
                "previous endpoint cannot prematurely end the confirmed reporting period");
        });
        Scenario("manual reset merges after restart and later independent refill splits", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "codex-billing-manual-tests-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "periods.json");
            try
            {
                var tracker = new BillingCycleTracker(path);
                tracker.Observe(Sample(Start, 20));
                Check(tracker.AddManualReset(Key, Start.AddMinutes(1), out _), "manual reset after last observation is accepted");
                string id = tracker.GetPeriods(Key)[0].Id;
                string? eventId = tracker.GetPeriods(Key)[0].ResetEventId;
                Check(tracker.CorrectStart(id, Start.AddSeconds(30), out _), "unobserved manual boundary can be corrected");
                tracker = new BillingCycleTracker(path);
                Check(tracker.RecheckAt != null && tracker.GetPeriods(Key).Count == 2, "restart retains manual merge marker");
                tracker.Observe(Sample(Start.AddMinutes(2), 100, Start.AddHours(6), cards: 2));
                Check(tracker.GetPeriods(Key).Count == 2 && tracker.GetPeriods(Key)[0].Id == id
                    && tracker.GetPeriods(Key)[0].StartedAt == Start.AddSeconds(30)
                    && tracker.GetPeriods(Key)[0].EndsAt == Start.AddHours(6)
                    && tracker.GetPeriods(Key)[0].Reason == "Manual" && tracker.RecheckAt == null,
                    "first post-reset sample refreshes manual endpoint and baseline without another period");
                tracker = new BillingCycleTracker(path);
                tracker.Observe(Sample(Start.AddMinutes(2).AddSeconds(5), 99, Start.AddHours(6), cards: 2));
                Check(tracker.GetPeriods(Key).Count == 2 && tracker.GetPeriods(Key)[0].StartedAt == Start.AddSeconds(30),
                    "merged manual boundary and correction survive another restart");
                tracker.Observe(Sample(Start.AddMinutes(10), 20, Start.AddHours(6), cards: 2));
                tracker.Observe(Sample(Start.AddMinutes(20), 100, Start.AddHours(7), cards: 1));
                tracker.Observe(Sample(Start.AddMinutes(20).AddSeconds(5), 99, Start.AddHours(7), cards: 1));
                Check(tracker.GetPeriods(Key).Count == 3 && tracker.GetPeriods(Key)[0].Reason == "Card"
                    && tracker.GetPeriods(Key)[0].ResetEventId != eventId && tracker.GetPeriods(Key)[1].Id == id,
                    "a later independent recovery still creates a distinct automatic event");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        Scenario("manual events across windows and late historical insert", () =>
        {
            const string second = "codex/secondary/10080";
            QuotaSnapshot Multi(DateTimeOffset at, double remaining, int cards = 3) => Sample(at, remaining, cards: cards) with
            { Windows = [Window(remaining), Window(remaining) with { Key = second, Slot = "secondary", WindowMinutes = 10080 }] };
            var tracker = new BillingCycleTracker();
            tracker.Observe(Multi(Start, 20));
            tracker.AddManualReset(Key, Start.AddMinutes(1), out _);
            string? eventId = tracker.GetPeriods(Key)[0].ResetEventId;
            tracker.Observe(Multi(Start.AddMinutes(2), 100, 2));
            tracker.Observe(Multi(Start.AddMinutes(2).AddSeconds(5), 99, 2));
            Check(tracker.GetPeriods(Key).Count == 2 && tracker.GetPeriods(second).Count == 2
                && tracker.GetPeriods(second)[0].ResetEventId == eventId,
                "other windows in the same observed refill share the manual event without duplicating its window");
            var both = new BillingCycleTracker();
            both.Observe(Multi(Start, 20));
            both.AddManualReset(Key, Start.AddMinutes(1), out _);
            both.AddManualReset(second, Start.AddMinutes(1), out _);
            Check(both.GetPeriods(Key)[0].ResetEventId == both.GetPeriods(second)[0].ResetEventId,
                "same-time manual entries in separate windows share an event id");
            both.Observe(Multi(Start.AddMinutes(2), 100, 2));
            Check(both.GetPeriods(Key).Count == 2 && both.GetPeriods(second).Count == 2 && both.RecheckAt == null,
                "multiple manual window markers merge independently on the next observation");
            var history = new BillingCycleTracker();
            history.Observe(Sample(Start, 20));
            history.AddManualReset(Key, Start.AddMinutes(2), out _);
            history.AddManualReset(Key, Start.AddMinutes(1), out _);
            history.Observe(Sample(Start.AddMinutes(3), 100, Start.AddHours(6), cards: 1));
            Check(history.GetPeriods(Key).Count == 3 && history.GetPeriods(Key)[0].StartedAt == Start.AddMinutes(2)
                && history.GetPeriods(Key)[0].EndsAt == Start.AddHours(6)
                && history.GetPeriods(Key)[1].EndsAt == Start.AddMinutes(2),
                "adding an earlier manual event does not replace the latest merge target or overlap historical periods");
        });
        Scenario("corrected automatic candidate cannot roll back after restart", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "codex-billing-corrected-tests-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "periods.json");
            try
            {
                var tracker = new BillingCycleTracker(path);
                tracker.Observe(Sample(Start, 30));
                tracker.Observe(Sample(Start.AddMinutes(1), 31));
                string id = tracker.GetPeriods(Key)[0].Id;
                Check(tracker.CorrectStart(id, Start.AddSeconds(45), out _), "automatic pending boundary can be manually confirmed");
                tracker = new BillingCycleTracker(path);
                Check(tracker.GetPeriods(Key).Count == 2 && tracker.GetPeriods(Key)[0].IsCorrected,
                    "checkpoint accepts a corrected candidate awaiting endpoint verification");
                tracker.Observe(Sample(Start.AddMinutes(1).AddSeconds(5), 29));
                Check(tracker.GetPeriods(Key).Count == 2 && tracker.GetPeriods(Key)[0].Id == id
                    && tracker.GetPeriods(Key)[0].StartedAt == Start.AddSeconds(45) && !tracker.GetPeriods(Key)[0].IsPending,
                    "a later non-sustained percentage cannot delete or replace the user-confirmed boundary");
                tracker.Observe(Sample(Start.AddMinutes(2), 100, Start.AddHours(6)));
                tracker.Observe(Sample(Start.AddMinutes(2).AddSeconds(5), 99, Start.AddHours(6)));
                Check(tracker.GetPeriods(Key).Count == 3 && tracker.GetPeriods(Key)[1].Id == id,
                    "manual confirmation updates baseline so later recovery remains independently detectable");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        Scenario("account isolation and missing evidence", () =>
        {
            var tracker = new BillingCycleTracker();
            tracker.Observe(Sample(Start, 30));
            string first = tracker.GetPeriods(Key)[0].Id;
            tracker.Observe(Sample(Start.AddMinutes(1), 100) with { AccountKey = null });
            tracker.Observe(Sample(Start.AddMinutes(2), 100) with { Health = SampleHealth.Stale });
            Check(tracker.GetPeriods(Key).Count == 1 && tracker.GetPeriods(Key)[0].Id == first,
                "unknown identity and stale readings do not create reset");
            tracker.Observe(Sample(Start.AddMinutes(3), 100) with { AccountKey = "another-account" });
            Check(tracker.GetPeriods(Key).Count == 1 && tracker.GetPeriods(Key)[0].AccountKey == "another-account"
                && !tracker.CorrectStart(first, Start.AddMinutes(1), out _), "account switch cannot reuse prior credit or manual records");
            tracker.Observe(Sample(Start.AddMinutes(4), 29));
            Check(tracker.GetPeriods(Key).Single().Id == first, "returning account restores its own period");
            tracker.Observe(Sample(Start.AddMinutes(5), 100) with { ResetCredits = ResetCreditSample.Missing });
            Check(tracker.GetPeriods(Key)[0].Reason == "Recovery", "missing card evidence is not called card redemption");
        });
        Console.WriteLine($"Billing cycles: {checks} checks, {failures} failure(s)");
        return failures;
    }

    private static QuotaWindow Window(double remaining, DateTimeOffset? reset = null) =>
        new(Key, "codex", "Codex · 5 小时", "primary", 300, remaining, reset ?? Start.AddHours(5));

    private static QuotaSnapshot Sample(DateTimeOffset at, double remaining, DateTimeOffset? reset = null, int cards = 3) =>
        new(at, [Window(remaining, reset)], SampleHealth.Fresh)
        { AccountKey = "account-a", ResetCredits = new(cards, at, SampleHealth.Fresh) };
}
