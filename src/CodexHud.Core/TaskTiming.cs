using System.Globalization;

namespace CodexHud.Core;

/// <summary>Wall elapsed time of the current/latest turn, including tools and unclassified waits.</summary>
public sealed record TaskTimingDisplay(string Label, string Text, TimeSpan? Elapsed, bool IsAdvancing);

public static class TaskTiming
{
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromSeconds(120);

    /// <summary>
    /// An advancing value remains a local activity inference. Terminal values require an explicit
    /// endpoint; unconfirmed, closed-app and stale observations are bounded by actual evidence.
    /// </summary>
    public static TaskTimingDisplay Describe(TaskActivity task, ActivitySnapshot snapshot, DateTimeOffset now)
    {
        if (task.StartedAt is not { } start || start > now) return Missing();
        if (task.State is ActivityState.Completed or ActivityState.Interrupted)
            return task.EndedAt is { } end && end >= start && end <= now
                ? Display("本轮用时", end - start, false) : Missing();

        if (task.EvidenceAt is not { } evidence || evidence < start || evidence > now
            || snapshot.ObservedAt < start || snapshot.ObservedAt > now) return Missing();

        bool advances = task.State == ActivityState.ExecutionEvidence && snapshot.AppPresent
            && snapshot.Health is SampleHealth.Fresh or SampleHealth.Partial
            && now - snapshot.ObservedAt <= EvidenceLifetime && now - evidence <= EvidenceLifetime;
        if (advances) return Display("本轮已用", now - start, true);

        var bound = evidence < snapshot.ObservedAt ? evidence : snapshot.ObservedAt;
        return Display("截至证据", bound - start, false);
    }

    public static string Format(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) return "—";
        string clock = $"{elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        return elapsed.Days > 0 ? elapsed.Days.ToString(CultureInfo.InvariantCulture) + " 天 " + clock : clock;
    }

    private static TaskTimingDisplay Display(string label, TimeSpan elapsed, bool advances) =>
        new(label, Format(elapsed), elapsed, advances);
    private static TaskTimingDisplay Missing() => new("本轮用时", "—", null, false);
}
