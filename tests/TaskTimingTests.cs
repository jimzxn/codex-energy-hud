using CodexHud.Core;

public static class TaskTimingTests
{
    public static int Run()
    {
        int failures = 0;
        void Check(bool valid, string scenario)
        {
            if (valid) return;
            failures++;
            Console.Error.WriteLine("FAIL task timing: " + scenario);
        }

        var now = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        var task = new TaskActivity("task", "Task", ActivityState.ExecutionEvidence,
            now.AddSeconds(-10), now.AddSeconds(-75), "fixture");
        var snapshot = new ActivitySnapshot(now, true, new[] { task }, SampleHealth.Partial);
        var live = TaskTiming.Describe(task, snapshot, now);
        Check(live.IsAdvancing && live.Elapsed == TimeSpan.FromSeconds(75) && live.Text == "00:01:15",
            "recent local execution evidence shows current turn wall elapsed");
        Check(TaskTiming.Describe(task, snapshot, now.AddSeconds(1)).Elapsed == TimeSpan.FromSeconds(76),
            "live timer advances without another provider read");

        foreach (var waitingState in new[] { ActivityState.AwaitingApproval, ActivityState.AwaitingInput })
        {
            var waiting = task with { State = waitingState };
            var freshWait = TaskTiming.Describe(waiting, snapshot with { Health = SampleHealth.Fresh }, now);
            Check(freshWait.IsAdvancing && freshWait.Label == "本轮已用" && freshWait.Elapsed == TimeSpan.FromSeconds(75),
                waitingState + " includes a fresh confirmed wait in turn wall duration");
            var partialWait = TaskTiming.Describe(waiting, snapshot, now.AddSeconds(1));
            Check(partialWait.IsAdvancing && partialWait.Elapsed == TimeSpan.FromSeconds(76),
                waitingState + " remains advancing with a valid partial observation");
            foreach (var unavailable in new[]
            {
                snapshot with { Health = SampleHealth.Stale }, snapshot with { AppPresent = false }
            })
            {
                var stopped = TaskTiming.Describe(waiting, unavailable, now.AddSeconds(1));
                Check(!stopped.IsAdvancing && stopped.Label == "截至证据" && stopped.Elapsed == TimeSpan.FromSeconds(65),
                    waitingState + " freezes at evidence after stale read or desktop exit");
            }
            var expired = TaskTiming.Describe(waiting, snapshot, now.AddSeconds(121));
            Check(!expired.IsAdvancing && expired.Elapsed == TimeSpan.FromSeconds(65),
                waitingState + " cannot keep advancing without a refreshed observation");
            var oldWait = waiting with { StartedAt = now.AddMinutes(-5), EvidenceAt = now.AddSeconds(-121) };
            var oldDisplay = TaskTiming.Describe(oldWait, snapshot, now);
            Check(!oldDisplay.IsAdvancing && oldDisplay.Elapsed == TimeSpan.FromSeconds(179),
                waitingState + " requires recent task evidence even with a fresh snapshot");
            Check(TaskTiming.Describe(oldWait with { EvidenceAt = now.AddSeconds(-120) }, snapshot, now).IsAdvancing,
                waitingState + " accepts the 120-second evidence boundary");
            var fallback = TaskTiming.Describe(waiting with { State = ActivityState.Unconfirmed }, snapshot, now);
            Check(!fallback.IsAdvancing && fallback.Elapsed == TimeSpan.FromSeconds(65),
                waitingState + " freezes when a live status falls back to unconfirmed local evidence");
        }
        foreach (var state in new[] { ActivityState.Completed, ActivityState.Interrupted })
        {
            var ended = task with { State = state, EndedAt = now.AddSeconds(-12) };
            var first = TaskTiming.Describe(ended, snapshot, now);
            var later = TaskTiming.Describe(ended, snapshot with { AppPresent = false, Health = SampleHealth.Stale }, now.AddDays(1));
            Check(!first.IsAdvancing && first.Elapsed == TimeSpan.FromSeconds(63) && later.Elapsed == first.Elapsed,
                state + " freezes at explicit endpoint even after app exit and stale snapshot");
            Check(TaskTiming.Describe(ended with { EndedAt = null }, snapshot, now).Elapsed is null,
                state + " with missing end is unknown, never last activity or current clock");
        }

        var bound = TaskTiming.Describe(task with { State = ActivityState.Unconfirmed }, snapshot, now);
        Check(!bound.IsAdvancing && bound.Label == "截至证据" && bound.Elapsed == TimeSpan.FromSeconds(65),
            "unconfirmed task freezes at last evidence");
        foreach (var invalidSnapshot in new[]
        {
            snapshot with { AppPresent = false }, snapshot with { Health = SampleHealth.Stale },
            snapshot with { Health = SampleHealth.Unavailable }, snapshot with { Health = SampleHealth.Loading }
        })
            Check(TaskTiming.Describe(task, invalidSnapshot, now).Elapsed == TimeSpan.FromSeconds(65)
                && !TaskTiming.Describe(task, invalidSnapshot, now).IsAdvancing,
                "closed app or invalid snapshot cannot keep timer advancing");

        Check(!TaskTiming.Describe(task, snapshot, now.AddSeconds(121)).IsAdvancing,
            "snapshot becomes stale without another provider response");
        Check(TaskTiming.Describe(task, snapshot, now.AddSeconds(121)).Elapsed == TimeSpan.FromSeconds(65),
            "expired execution evidence falls back to evidence bound, not continuously growing running time");
        var old = task with { StartedAt = now.AddMinutes(-5), EvidenceAt = now.AddSeconds(-121) };
        Check(!TaskTiming.Describe(old, snapshot, now).IsAdvancing, "fresh snapshot cannot revive old evidence");
        Check(TaskTiming.Describe(old with { EvidenceAt = now.AddSeconds(-120) }, snapshot, now).IsAdvancing,
            "120-second evidence boundary remains consistent with activity classifier");
        Check(TaskTiming.Describe(task with { StartedAt = now.AddSeconds(-15) }, snapshot, now).Text == "00:00:15",
            "new turn start resets wall elapsed");
        Check(TaskTiming.Format(TimeSpan.FromDays(2) + TimeSpan.FromSeconds(3723)) == "2 天 01:02:03",
            "multi-day duration is not wrapped at 24 hours");

        foreach (var invalidTask in new[]
        {
            task with { StartedAt = null }, task with { EvidenceAt = null },
            task with { StartedAt = now.AddSeconds(1) }, task with { EvidenceAt = now.AddSeconds(1) },
            task with { EvidenceAt = task.StartedAt!.Value.AddSeconds(-1) },
            task with { State = ActivityState.Completed, EndedAt = task.StartedAt!.Value.AddSeconds(-1) },
            task with { State = ActivityState.Completed, EndedAt = now.AddSeconds(1) }
        })
            Check(TaskTiming.Describe(invalidTask, snapshot, now).Text == "—",
                "missing, future or reversed timestamps remain unknown");
        Check(TaskTiming.Describe(task, snapshot with { ObservedAt = now.AddSeconds(1) }, now).Elapsed is null,
            "clock rollback before observed timestamp is unknown");
        Check(TaskTiming.Describe(task, snapshot with { ObservedAt = now.AddMinutes(-2) }, now).Elapsed is null,
            "snapshot preceding the turn start is not a valid timing observation");
        Check(TaskTiming.Describe(task with { State = ActivityState.Completed, EndedAt = task.StartedAt }, snapshot, now).Text == "00:00:00",
            "explicit zero-duration turn is valid");
        return failures;
    }
}
