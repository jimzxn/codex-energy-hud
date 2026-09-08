using CodexHud.Core;

public static class TaskNoticeTests
{
    public static int Run()
    {
        int failures = 0;
        int checks = 0;
        void Check(bool valid, string scenario)
        {
            checks++;
            if (valid) return;
            failures++;
            Console.Error.WriteLine("FAIL task notices: " + scenario);
        }

        var origin = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        TaskActivity Task(string id = "task", string? turn = "turn-1", ActivityState state = ActivityState.ExecutionEvidence,
            int start = -10, int? end = null, string? attention = null) =>
            new(id, "Title " + id, state, origin.AddSeconds(end ?? 0), origin.AddSeconds(start), "fixture",
                end is { } ended ? origin.AddSeconds(ended) : null) { TurnId = turn, AttentionId = attention };
        ActivitySnapshot Snapshot(int seconds, params TaskActivity[] tasks) =>
            new(origin.AddSeconds(seconds), true, tasks, SampleHealth.Partial);
        List<TaskNotice> Observe(TaskNoticeTracker tracker, int seconds, params TaskActivity[] tasks) =>
            tracker.Observe(Snapshot(seconds, tasks), origin.AddSeconds(seconds));

        var tracker = new TaskNoticeTracker();
        var running = Task();
        var completed = Task(state: ActivityState.Completed, end: 4);
        Check(Observe(tracker, 0, running).Count == 0, "first running sample establishes baseline");
        var notices = Observe(tracker, 5, completed);
        Check(notices.Count == 1 && notices[0].TaskId == running.Id && notices[0].Title == running.Title
            && notices[0].State == ActivityState.Completed && notices[0].EventKey.Length > 0,
            "observed same turn produces one explicit completion with UI metadata");
        Check(Observe(tracker, 6, completed).Count == 0 && Observe(tracker, 7, completed).Count == 0,
            "frequent completion sampling never repeats");
        Check(Observe(tracker, 8, completed with { State = ActivityState.Interrupted }).Count == 0,
            "terminal classification correction cannot produce another notice for the same turn");
        Observe(tracker, 10, Task(turn: "turn-2", start: 9));
        Check(Observe(tracker, 15, Task(turn: "turn-2", state: ActivityState.Interrupted, start: 9, end: 14)).Single().State
            == ActivityState.Interrupted, "a new turn can emit its own interruption notice");

        tracker = new();
        Check(Observe(tracker, 0, Task(state: ActivityState.Completed, end: -2)).Count == 0,
            "historical completion on startup is silent");
        Check(Observe(tracker, 5, Task("new", state: ActivityState.Completed, end: 4)).Count == 0,
            "newly discovered completed task without observed running is silent");
        Check(Observe(tracker, 6, Task("old", state: ActivityState.Interrupted, end: -2)).Count == 0,
            "historical interruption is also silent");

        tracker = new();
        Observe(tracker, 0, running);
        Check(Observe(tracker, 5, Task(turn: "other-turn", state: ActivityState.Completed, end: 4)).Count == 0,
            "completion of a different turn cannot close the observed turn");
        Observe(tracker, 10, Task(turn: "turn-2", start: 9));
        Check(Observe(tracker, 15, Task(state: ActivityState.Completed, end: 4)).Count == 0,
            "late previous-turn completion cannot close the current turn");

        tracker = new();
        Observe(tracker, 0, running);
        Check(Observe(tracker, 5, completed with { EndedAt = null }).Count == 0,
            "completion requires an explicit endpoint");
        tracker = new();
        Observe(tracker, 0, running);
        Check(Observe(tracker, 5, Task(state: ActivityState.Completed, end: -1)).Count == 0,
            "completion timestamp must fall within monitoring");
        tracker = new();
        Observe(tracker, 0, running);
        Observe(tracker, 5, running);
        Check(Observe(tracker, 10, completed).Count == 0,
            "old endpoint preceding latest observed running cannot be a new completion");
        tracker = new();
        Observe(tracker, 0, running);
        Check(Observe(tracker, 150, completed).Count == 0,
            "old endpoint discovered after a long gap cannot trigger a late historical alert");

        tracker = new();
        Observe(tracker, 0, running with { TurnId = null });
        Check(Observe(tracker, 5, completed with { TurnId = null }).Count == 1,
            "start timestamp safely identifies a turn when turn ID is missing");
        tracker = new();
        Observe(tracker, 0, running with { StartedAt = null });
        Check(Observe(tracker, 5, completed with { StartedAt = null }).Count == 1,
            "explicit turn ID can identify a turn without start timestamp");
        tracker = new();
        Observe(tracker, 0, running with { TurnId = null, StartedAt = null });
        Check(Observe(tracker, 5, completed with { TurnId = null, StartedAt = null }).Count == 0,
            "missing both lifecycle identifiers never implies the same turn");
        tracker = new();
        Observe(tracker, 0, running with { TurnId = null });
        Check(Observe(tracker, 5, Task(turn: null, state: ActivityState.Completed, start: 1, end: 4)).Count == 0,
            "changed start timestamp means a different turn");

        tracker = new();
        var approval = Task(state: ActivityState.AwaitingApproval, attention: "approval-1");
        Check(Observe(tracker, 0, approval).Single().State == ActivityState.AwaitingApproval,
            "first live approval may notify without prior running");
        Check(Observe(tracker, 5, approval).Count == 0, "same approval request is deduplicated");
        Check(Observe(tracker, 10, approval with { AttentionId = "approval-2" }).Count == 1,
            "different approval request in the same turn can notify");
        Observe(tracker, 15, running);
        Check(Observe(tracker, 20, approval).Count == 0,
            "already delivered explicit request stays deduplicated after leaving waiting state");
        Check(Observe(tracker, 25, approval with { TurnId = "turn-2", StartedAt = origin.AddSeconds(24) }).Count == 1,
            "the same request label in a new turn is a distinct event");
        Check(Observe(tracker, 30, approval with { Id = "parallel" }).Count == 1,
            "parallel tasks do not suppress each other's request");

        tracker = new();
        Observe(tracker, 0, approval);
        Check(Observe(tracker, 5, approval with { AttentionId = null }).Count == 0,
            "temporary loss of request ID cannot repeat a known wait");
        Check(Observe(tracker, 10, approval).Count == 0,
            "restored request ID remains deduplicated");
        tracker = new();
        Observe(tracker, 0, approval with { AttentionId = null });
        Check(Observe(tracker, 5, approval).Count == 0,
            "discovering an ID for an announced anonymous wait cannot repeat it");
        Check(Observe(tracker, 10, approval with { AttentionId = "approval-2" }).Count == 1,
            "a subsequently different explicit request still notifies");

        tracker = new();
        var waiting = Task(state: ActivityState.AwaitingInput);
        Check(Observe(tracker, 0, waiting).Single().State == ActivityState.AwaitingInput,
            "first live input wait without request ID may notify");
        Check(Observe(tracker, 5, waiting).Count == 0, "unchanged anonymous waiting episode is silent");
        Observe(tracker, 10, running);
        Check(Observe(tracker, 15, waiting).Count == 1, "leaving and re-entering anonymous wait starts a new episode");
        Check(Observe(tracker, 20, waiting with { State = ActivityState.AwaitingApproval }).Count == 1,
            "changing wait type starts a new episode");
        Check(Observe(tracker, 25, waiting).Count == 1, "returning from approval to input starts another episode");

        foreach (var health in new[] { SampleHealth.Loading, SampleHealth.Unavailable, SampleHealth.Stale })
        {
            tracker = new();
            Observe(tracker, 0, waiting);
            Check(tracker.Observe(Snapshot(5) with { Health = health }, origin.AddSeconds(5)).Count == 0,
                health + " cannot emit notifications");
            Check(Observe(tracker, 10, waiting).Count == 0, health + " does not clear a pending episode");
        }
        tracker = new();
        Observe(tracker, 0, waiting);
        Observe(tracker, 5);
        Check(Observe(tracker, 10, waiting).Count == 0, "missing row in partial snapshot does not resolve waiting");
        tracker.Observe(Snapshot(15) with { Health = SampleHealth.Fresh }, origin.AddSeconds(15));
        Check(Observe(tracker, 20, waiting).Count == 0, "row disappearance alone never proves a wait resolved");
        tracker.Observe(Snapshot(25, running) with { AppPresent = false }, origin.AddSeconds(25));
        Check(Observe(tracker, 30, waiting).Count == 0, "closed app does not clear pending request");

        tracker = new();
        Check(tracker.Observe(Snapshot(5, approval), origin).Count == 0, "future snapshot is ignored");
        Check(tracker.Observe(Snapshot(0, approval), origin.AddSeconds(121)).Count == 0, "aged snapshot is ignored");
        Check(tracker.Observe(Snapshot(0, approval) with { AppPresent = false }, origin).Count == 0,
            "absent app cannot generate a live approval alert");
        Check(Observe(tracker, 5, approval).Count == 1, "invalid initial inputs do not consume a later live request");
        Check(tracker.Observe(Snapshot(4, running), origin.AddSeconds(6)).Count == 0,
            "out-of-order snapshot cannot clear waiting state");
        Check(tracker.Observe(Snapshot(5, running), origin.AddSeconds(6)).Count == 0,
            "conflicting repeat of the same snapshot cannot clear waiting state");
        Check(Observe(tracker, 10, approval).Count == 0, "recovery from stale or future observations stays deduplicated");

        foreach (var invalidTask in new[]
        {
            completed with { EndedAt = origin.AddSeconds(6) },
            completed with { EvidenceAt = origin.AddSeconds(6) },
            completed with { StartedAt = origin.AddSeconds(6) },
            completed with { StartedAt = origin.AddSeconds(4), EndedAt = origin.AddSeconds(3) }
        })
        {
            tracker = new();
            Observe(tracker, 0, running);
            Check(Observe(tracker, 5, invalidTask).Count == 0, "future and reversed task timestamps cannot notify");
        }

        tracker = new();
        Observe(tracker, 0, running, Task("parallel"));
        Check(Observe(tracker, 5, completed, Task("parallel", state: ActivityState.Completed, end: 4)).Count == 2,
            "parallel observed turns can finish in the same sample");
        tracker = new();
        Observe(tracker, 0, running with { State = ActivityState.Unconfirmed });
        Check(Observe(tracker, 5, completed).Count == 1, "explicit completion can resolve a previously observed unconfirmed turn");
        tracker = new();
        Observe(tracker, 0, waiting); // UI intentionally consumes this while notifications are disabled.
        Check(Observe(tracker, 5, waiting).Count == 0, "enabling notifications cannot replay an already consumed wait");
        Observe(tracker, 10, running);
        Check(Observe(tracker, 15, waiting).Count == 1, "a new waiting episode still notifies after re-enabling");

        tracker = new();
        Observe(tracker, 0, running);
        var fractional = origin.AddSeconds(5.8);
        tracker.Observe(Snapshot(5, running) with { ObservedAt = fractional }, fractional);
        Check(Observe(tracker, 6, Task(state: ActivityState.Completed, end: 5)).Count == 1,
            "explicit same turn allows sub-second history timestamp precision loss");
        tracker = new();
        Observe(tracker, 0, running with { TurnId = null });
        tracker.Observe(Snapshot(5, running with { TurnId = null }) with { ObservedAt = fractional }, fractional);
        Check(Observe(tracker, 6, Task(turn: null, state: ActivityState.Completed, end: 5)).Count == 0,
            "timestamp tolerance requires an explicit turn ID");
        tracker = new();
        Observe(tracker, 0, running);
        Observe(tracker, 6, running);
        Check(Observe(tracker, 7, Task(state: ActivityState.Completed, end: 5)).Count == 0,
            "a full second of reversed time cannot be explained by precision tolerance");
        tracker = new();
        var lateStart = origin.AddSeconds(0.8);
        tracker.Observe(Snapshot(0, running) with { ObservedAt = lateStart }, lateStart);
        Check(Observe(tracker, 5, Task(state: ActivityState.Completed, end: 0)).Count == 0,
            "timestamp tolerance must not admit a record before monitoring began");

        tracker = new();
        Observe(tracker, 0, waiting);
        Observe(tracker, 5, running with { State = ActivityState.Unconfirmed });
        Check(Observe(tracker, 10, waiting).Count == 0,
            "local unconfirmed fallback cannot clear anonymous desktop waiting episode");
        Observe(tracker, 15, running);
        Check(Observe(tracker, 20, waiting).Count == 1,
            "explicit execution after a fallback still resolves a wait episode");
        Observe(tracker, 25, running with { TurnId = "new-turn", State = ActivityState.Unconfirmed });
        Check(Observe(tracker, 30, waiting with { TurnId = "new-turn" }).Count == 1,
            "new turn identity starts a fresh wait even when first seen unconfirmed");

        tracker = new();
        Observe(tracker, 0, waiting);
        var history = Enumerable.Range(0, 3000).Select(index =>
            Task("historical-" + index, state: ActivityState.Completed, end: -1)).ToArray();
        Check(Observe(tracker, 5, history).Count == 0, "large historical task list is silent");
        Check(Observe(tracker, 10, waiting).Count == 0,
            "trimming surplus inactive history must retain pending wait identity");
        Check(Observe(tracker, 15, history[0]).Count == 0,
            "rediscovered evicted historical completion remains silent");

        Console.WriteLine($"Task notices: {checks} checks, {failures} failure(s)");
        return failures;
    }
}
