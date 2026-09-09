using CodexHud.Core;

public static class PeriodUsageTests
{
    private static readonly DateTimeOffset At = new(2025, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TokenCounts Counts = new(1000, 800, 0, 100, 0, 1100);
    public static int Run()
    {
        int failures = 0, checks = 0;
        void Check(bool value, string message) { checks++; if (!value) { failures++; Console.Error.WriteLine("FAIL period usage: " + message); } }
        void Scenario(string name, Action run)
        { try { run(); } catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL period usage " + name + ": " + ex); } }
        Scenario("period response boundary and exact pricing", () =>
        {
            var source = Ledger([Task("root")], [
                Response("root", "before", At.AddTicks(-1)), Response("root", "start", At),
                Response("root", "inside", At.AddMinutes(30)), Response("root", "end", At.AddHours(1))], []);
            var report = Build(source);
            Check(report.Totals.Counts.TotalTokens == 2200 && report.Totals.Counts.InputTokens == 2000
                && report.Totals.Counts.CachedInputTokens == 1600, "left-inclusive right-exclusive; cache is not extra input");
            Check(report.Totals.MinimumUsd == .00378m && report.Totals.MaximumUsd == .00378m,
                "two standard gpt-5.3-codex responses: uncached + cached + output = USD .00378");
            Check(report.Totals.UnknownTurns == 1 && report.Tasks[0].Self.Duration == null,
                "responses without timing do not imply zero task duration");
        });
        Scenario("global duplicates, parent descendants and archive", () =>
        {
            var r = Response("root", "one", At.AddMinutes(1));
            var source = Ledger([Task("root"), Task("child", "root", true), Task("nested", "child", true, true), Task("fork")],
                [r, r, Response("child", "one", At.AddMinutes(2)), Response("nested", "one", At.AddMinutes(3)),
                    Response("fork", "one", At.AddMinutes(4))],
                [Turn("root", "t", 0, 10), Turn("child", "t", 2, 12), Turn("nested", "t", 8, 15), Turn("fork", "t", 0, 5)]);
            var report = Build(source);
            var root = report.Tasks.Single(t => t.ThreadId == "root");
            Check(report.Totals.Counts.TotalTokens == 4400 && root.Group.Counts.TotalTokens == 3300
                && root.Self.Counts.TotalTokens == 1100, "each thread/response once globally and parent total includes descendants");
            Check(root.Group.Duration == TimeSpan.FromMinutes(15) && report.Totals.Duration == TimeSpan.FromMinutes(20),
                "parallel parent and children union, independent task groups add");
            Check(root.Children.Single().Children.Single().Archived && report.Tasks.Count == 2, "archived nested agent and independent fork");
            Check(root.Models.Single().Totals.Counts.TotalTokens == 1100 && root.Turns.Count == 1,
                "row model and turn detail is own data, allowing UI recursive breakdown");
        });
        Scenario("idle gaps and boundary-clipped timing", () =>
        {
            var source = Ledger([Task("root")], [Response("root", "one", At.AddMinutes(1))],
                [Turn("root", "a", -3, 2), Turn("root", "b", 30, 33), Turn("root", "c", 58, 62)]);
            var report = Build(source, At.AddHours(2));
            Check(report.Tasks[0].Group.Duration == TimeSpan.FromMinutes(7), "2 + 3 + 2 minutes; idle excluded and both ends clipped");
        });
        Scenario("unknown model and timestamp", () =>
        {
            var source = Ledger([Task("root")],
                [Response("root", "known", At), Response("root", "unpriced", At.AddSeconds(1)) with { Model = "unknown" },
                 Response("root", "undated", At) with { RecordedAt = null }], [Turn("root", "t", 0, 1)]);
            var report = Build(source);
            Check(report.Totals.Counts.TotalTokens == 2200 && report.Totals.PricedResponses == 1
                && report.Totals.UnpricedResponses == 1 && report.Totals.UnpricedTokens == 1100, "unpriced tokens retained; undated responses excluded");
            Check(report.Totals.MinimumUsd == .00189m && report.Health == SampleHealth.Partial
                && report.Detail!.Contains("缺少时间"), "known subtotal and unknown coverage explicit");
            var unknown = Build(Ledger([Task("root")], [Response("root", "r", At) with { Model = "unknown" }], []));
            Check(unknown.Totals.MinimumUsd == null, "all unpriced is unknown amount, not free");
        });
        Scenario("fresh running and expired evidence", () =>
        {
            var turn = new LocalTaskTurn("root", "t", At, null, At.AddMinutes(1), ActivityState.Unconfirmed, SampleHealth.Fresh);
            var now = At.AddMinutes(2);
            var source = Ledger([Task("root")], [Response("root", "one", At.AddMinutes(1))], [turn]) with { ObservedAt = now };
            var liveTask = new TaskActivity("root", "", ActivityState.ExecutionEvidence, now, At, "") { TurnId = "t" };
            var activity = new ActivitySnapshot(now, true, [liveTask], SampleHealth.Fresh);
            var first = PeriodUsageCalculator.Build(source, Period(), now, activity);
            var later = PeriodUsageCalculator.Build(source, Period(), now.AddSeconds(30), activity);
            Check(first.Totals.Duration == TimeSpan.FromMinutes(2) && later.Totals.Duration == TimeSpan.FromSeconds(150),
                "fresh matching activity evidence advances active turn");
            var stale = PeriodUsageCalculator.Build(source, Period(), now.AddMinutes(3), activity);
            Check(stale.Totals.Duration == TimeSpan.FromMinutes(2) && stale.Health == SampleHealth.Partial,
                "expired evidence freezes at last evidence");
            var closed = PeriodUsageCalculator.Build(source, Period(), now.AddSeconds(30), activity with { AppPresent = false });
            Check(closed.Totals.Duration == TimeSpan.FromMinutes(2), "closed Codex freezes timing");
        });
        Scenario("orphan and cyclic graph cannot duplicate tokens", () =>
        {
            var report = Build(Ledger([Task("a", "b", true), Task("b", "a", true), Task("orphan", "gone", true)],
                [Response("a", "r", At), Response("b", "r", At), Response("orphan", "r", At)], []));
            Check(report.Tasks.Count == 3 && report.Totals.Counts.TotalTokens == 3300
                && report.Health == SampleHealth.Partial, "cycles and orphan agents are standalone roots once");
        });
        Scenario("empty fresh, loading and stale", () =>
        {
            var empty = Build(Ledger([], [], []));
            Check(empty.Totals.Counts.TotalTokens == 0 && empty.Totals.MinimumUsd == 0 && empty.Totals.Duration == TimeSpan.Zero,
                "complete empty window is known zero");
            var loading = Build(Ledger([], [], []) with { Health = SampleHealth.Loading });
            Check(loading.Totals.Counts.TotalTokens == null && loading.Totals.MinimumUsd == null
                && loading.Health == SampleHealth.Loading, "loading empty data is not zero");
            var stale = Build(Ledger([Task("root")], [Response("root", "r", At)], [Turn("root", "t", 0, 1)]) with { Health = SampleHealth.Stale });
            Check(stale.Totals.Counts.TotalTokens == 1100 && stale.Health == SampleHealth.Stale, "stale data preserves subtotal");
        });
        Scenario("manual reset repartitions without loss", () =>
        {
            var tracker = new BillingCycleTracker();
            tracker.Observe(new(At.AddMinutes(1), [new("q", "codex", "Hour", "primary", 60, 20, At.AddHours(1))], SampleHealth.Fresh) { AccountKey = "account" });
            Check(tracker.AddManualReset("q", At.AddMinutes(20), out _), "manual cut created");
            var source = Ledger([Task("root")], [Response("root", "a", At.AddMinutes(10)), Response("root", "b", At.AddMinutes(20))],
                [Turn("root", "t", 10, 30)]);
            var periods = tracker.GetPeriods("q");
            var reports = periods.Select(p => PeriodUsageCalculator.Build(source, p, At.AddHours(2))).ToArray();
            Check(reports.Sum(r => r.Totals.Counts.TotalTokens ?? 0) == 2200
                && reports.All(r => r.Totals.Counts.TotalTokens == 1100), "manual boundary partitions responses exactly");
            Check(reports.Sum(r => r.Totals.Duration?.TotalMinutes ?? 0) == 20, "cross-reset turn clipped without loss");
            Check(tracker.CorrectStart(periods[0].Id, At.AddMinutes(25), out _), "manual correction accepted");
            var corrected = tracker.GetPeriods("q").Select(p => PeriodUsageCalculator.Build(source, p, At.AddHours(2))).ToArray();
            Check(corrected.Sum(r => r.Totals.Counts.TotalTokens ?? 0) == 2200
                && corrected.Single(r => r.Period.IsCurrent).Totals.Counts.TotalTokens == 0, "correction reassigns usage, does not clear it");
        });
        Console.WriteLine($"Period usage: {checks} checks, {failures} failure(s)");
        return failures;
    }
    private static UsagePeriod Period() => new() { Id = "p", WindowKey = "q", Label = "本周期", StartedAt = At, EndsAt = At.AddHours(1), IsCurrent = true };
    private static PeriodUsageSnapshot Build(LocalUsageLedgerSnapshot ledger, DateTimeOffset? now = null) =>
        PeriodUsageCalculator.Build(ledger, Period(), now ?? At.AddHours(2));
    private static LocalUsageTask Task(string id, string? parent = null, bool subagent = false, bool archived = false) =>
        new(id, id, parent, subagent, archived, SampleHealth.Fresh, []);
    private static LocalUsageResponse Response(string id, string response, DateTimeOffset at) =>
        new(id, response, "t", at, Counts, "gpt-5.3-codex", "standard", "openai");
    private static LocalTaskTurn Turn(string id, string turn, int fromMinutes, int throughMinutes) =>
        new(id, turn, At.AddMinutes(fromMinutes), At.AddMinutes(throughMinutes), At.AddMinutes(throughMinutes), ActivityState.Completed, SampleHealth.Fresh);
    private static LocalUsageLedgerSnapshot Ledger(LocalUsageTask[] tasks, LocalUsageResponse[] responses, LocalTaskTurn[] turns) =>
        new(At.AddHours(2), tasks.ToDictionary(t => t.ThreadId), responses, turns, SampleHealth.Fresh);
}
