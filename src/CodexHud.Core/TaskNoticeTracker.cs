namespace CodexHud.Core;

public sealed record TaskNotice(string TaskId, string Title, ActivityState State, string EventKey);

/// <summary>Consumes task observations independently of whether desktop notifications are enabled.</summary>
public sealed class TaskNoticeTracker
{
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(120);
    private readonly Dictionary<string, Observation> _tasks = new(StringComparer.Ordinal);
    private const int RetainedTasks = 2048;
    private DateTimeOffset? _monitoringSince;
    private DateTimeOffset? _lastSnapshotAt;

    public List<TaskNotice> Observe(ActivitySnapshot snapshot, DateTimeOffset now)
    {
        var notices = new List<TaskNotice>();
        if (snapshot.Health is not (SampleHealth.Fresh or SampleHealth.Partial) || !snapshot.AppPresent
            || snapshot.ObservedAt > now || now - snapshot.ObservedAt > Freshness
            || (_lastSnapshotAt is { } previous && snapshot.ObservedAt <= previous))
            return notices;

        _monitoringSince ??= now;
        _lastSnapshotAt = snapshot.ObservedAt;
        foreach (var task in snapshot.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id) || task.StartedAt > snapshot.ObservedAt
                || task.EvidenceAt > snapshot.ObservedAt || task.EndedAt > snapshot.ObservedAt
                || (task.StartedAt is { } start && task.EndedAt is { } end && end < start))
                continue;

            if (!_tasks.TryGetValue(task.Id, out var observed))
                _tasks.Add(task.Id, observed = new());

            observed.LastSeenAt = snapshot.ObservedAt;
            string? lifecycle = Lifecycle(task);
            if (lifecycle != observed.Lifecycle)
            {
                observed.Lifecycle = lifecycle;
                observed.Delivered.Clear();
                observed.LastNonterminalAt = null;
                observed.WaitState = null;
            }

            if (task.State is ActivityState.Completed or ActivityState.Interrupted)
            {
                observed.WaitState = null;
                // An explicit endpoint must follow an observation of this same turn. In particular,
                // a fresh snapshot containing yesterday's completion is not a completion event.
                if (lifecycle is not null && observed.LastNonterminalAt is { } lastActive
                    && task.EndedAt is { } ended && IsAfterObservation(task, ended, lastActive) && ended >= _monitoringSince
                    && now - ended <= Freshness)
                    Add(task, observed, Key(task.Id, lifecycle, "ended"), notices);
                observed.LastNonterminalAt = null;
                continue;
            }

            // Without a turn ID or start timestamp there is no safe completion identity.
            if (lifecycle is not null && task.EndedAt is null)
                observed.LastNonterminalAt = snapshot.ObservedAt;

            if (task.State is not (ActivityState.AwaitingApproval or ActivityState.AwaitingInput))
            {
                // Falling back to local Unconfirmed evidence does not resolve a desktop request.
                if (task.State == ActivityState.ExecutionEvidence) observed.WaitState = null;
                continue;
            }

            if (observed.WaitState != task.State)
            {
                observed.WaitState = task.State;
                observed.WaitEpisode++;
                observed.WaitNoticeSeen = false;
                observed.AttentionId = null;
            }
            if (!string.IsNullOrWhiteSpace(task.AttentionId))
            {
                var key = Key(task.Id, lifecycle ?? "unknown", task.State.ToString(), "request:" + task.AttentionId);
                // Learning the ID of a wait already announced without one is metadata enrichment.
                if (observed.WaitNoticeSeen && observed.AttentionId is null) observed.Delivered.Add(key);
                else Add(task, observed, key, notices);
                observed.AttentionId = task.AttentionId;
            }
            else if (!observed.WaitNoticeSeen)
                Add(task, observed, Key(task.Id, lifecycle ?? "unknown", task.State.ToString(),
                    "episode:" + observed.WaitEpisode.ToString(System.Globalization.CultureInfo.InvariantCulture)), notices);
            observed.WaitNoticeSeen = true;
        }
        // Missing rows, including a partial read, do not establish that a wait was resolved.
        // Retain all live/waiting identities; discard only surplus inactive history.
        if (_tasks.Count > RetainedTasks)
            foreach (var id in _tasks.Where(pair => pair.Value.WaitState is null && pair.Value.LastNonterminalAt is null)
                .OrderBy(pair => pair.Value.LastSeenAt).Take(_tasks.Count - RetainedTasks).Select(pair => pair.Key).ToArray())
                _tasks.Remove(id);
        return notices;
    }

    private static bool IsAfterObservation(TaskActivity task, DateTimeOffset ended, DateTimeOffset observed) =>
        ended >= observed || (!string.IsNullOrWhiteSpace(task.TurnId) && observed - ended < TimeSpan.FromSeconds(1));

    private static void Add(TaskActivity task, Observation observed, string key, List<TaskNotice> notices)
    {
        if (observed.Delivered.Add(key)) notices.Add(new(task.Id, task.Title, task.State, key));
    }

    private static string? Lifecycle(TaskActivity task) => !string.IsNullOrWhiteSpace(task.TurnId)
        ? "turn:" + task.TurnId
        : task.StartedAt is { } start ? "start:" + start.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    private static string Key(params string[] parts) => string.Concat(parts.Select(part => part.Length + ":" + part));

    private sealed class Observation
    {
        public string? Lifecycle;
        public DateTimeOffset LastSeenAt;
        public HashSet<string> Delivered { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset? LastNonterminalAt;
        public ActivityState? WaitState;
        public long WaitEpisode;
        public bool WaitNoticeSeen;
        public string? AttentionId;
    }
}
