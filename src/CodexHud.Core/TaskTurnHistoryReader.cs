using CodexHud.Core.Native;

namespace CodexHud.Core;

/// <summary>Reads numeric lifecycle and display metadata only, through the WAL-aware read-only adapter.</summary>
internal sealed class TaskTurnHistoryReader(string home)
{
    internal sealed record TaskIndexEntry(string Title, bool Archived, bool IsSubagent, bool Excluded);
    internal sealed record Result(IReadOnlyDictionary<string, TaskIndexEntry> Tasks,
        IReadOnlyDictionary<(string ThreadId, string TurnId), LocalTaskTurn> Turns,
        bool HistoryAvailable, string? Detail);

    internal Result Read(IReadOnlySet<string> owners, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tasks = new Dictionary<string, TaskIndexEntry>(StringComparer.Ordinal);
        var turns = new Dictionary<(string, string), LocalTaskTurn>();
        var notes = new List<string>();
        bool historyAvailable = false;
        try
        {
            if (FindDatabase("state_") is { } path)
            {
                using var state = new SqliteReader(path);
                var columns = state.Columns("threads");
                if (!columns.Contains("id")) throw new IOException("Unsupported task metadata schema.");
                string Select(string name) => columns.Contains(name) ? name : "NULL AS " + name;
                string[] fields = ["id", "name", "title", "agent_nickname", "agent_path", "archived", "thread_source"];
                foreach (var row in state.Query("SELECT " + string.Join(',', fields.Select(Select)) + " FROM threads"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string id = row.Text("id");
                    if (!owners.Contains(id)) continue;
                    string title = First(row.Text("name"), row.Text("title"), row.Text("agent_path"), row.Text("agent_nickname"));
                    string source = row.Text("thread_source");
                    tasks[id] = new(CleanTitle(title), row.Number("archived") == 1,
                        source == "subagent", source == "guardian_review");
                }
            }
        }
        catch (Exception error) when (ReadableError(error)) { notes.Add("任务标题索引暂不可读，使用日志身份"); }
        try
        {
            if (FindDatabase("thread_history_") is { } path)
            {
                using var history = new SqliteReader(path);
                var columns = history.Columns("thread_turns");
                string[] required = ["thread_id", "turn_id", "status", "started_at", "completed_at"];
                if (required.Any(field => !columns.Contains(field))) throw new IOException("Unsupported turn history schema.");
                string duration = columns.Contains("duration_ms") ? "duration_ms" : "NULL AS duration_ms";
                foreach (var row in history.Query("SELECT thread_id,turn_id,status,started_at,completed_at," + duration + " FROM thread_turns"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string owner = row.Text("thread_id"), turnId = row.Text("turn_id");
                    if (!owners.Contains(owner) || !UsageLedgerCheckpoint.IsId(turnId)
                        || tasks.GetValueOrDefault(owner)?.Excluded == true) continue;
                    var start = ActivityProvider.FromUnix(row.Number("started_at"));
                    var end = ActivityProvider.FromUnix(row.Number("completed_at"));
                    long? milliseconds = row.Number("duration_ms");
                    if (milliseconds is >= 0 && milliseconds <= TimeSpan.FromDays(365).TotalMilliseconds)
                    {
                        if (start is { } origin && end is null && row.Text("status") is "completed" or "interrupted")
                            end = SafeAdd(origin, milliseconds.Value);
                        else if (end is { } endpoint && start is null) start = SafeAdd(endpoint, -milliseconds.Value);
                    }
                    bool invalid = start > now || end > now || start is { } s && end is { } e && e < s;
                    if (invalid) { start = null; end = null; }
                    string status = row.Text("status");
                    var state = status == "completed" ? ActivityState.Completed
                        : status == "interrupted" ? ActivityState.Interrupted : ActivityState.Unconfirmed;
                    bool complete = !invalid && start.HasValue && end.HasValue
                        && state is ActivityState.Completed or ActivityState.Interrupted;
                    turns[(owner, turnId)] = new(owner, turnId, start, end, end ?? start, state,
                        complete ? SampleHealth.Fresh : SampleHealth.Partial);
                }
                historyAvailable = true;
            }
            else notes.Add("历史轮次索引尚不可用，按已确认归属的日志补齐");
        }
        catch (Exception error) when (ReadableError(error)) { notes.Add("历史轮次索引暂不可读，保留已确认日志证据"); }
        return new(tasks, turns, historyAvailable, notes.Count == 0 ? null : string.Join("；", notes));
    }

    private string? FindDatabase(string prefix) => !Directory.Exists(home) ? null
        : Directory.EnumerateFiles(home, prefix + "*.sqlite", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Version: int.TryParse(Path.GetFileNameWithoutExtension(path)[prefix.Length..], out int version) ? version : -1))
            .Where(item => item.Version >= 0).OrderByDescending(item => item.Version).Select(item => item.Path).FirstOrDefault();

    private static DateTimeOffset? SafeAdd(DateTimeOffset at, long milliseconds)
    {
        try { return at.AddMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    internal static string CleanTitle(string? title)
    {
        string value = new((title ?? "").Where(character => !char.IsControl(character)).Take(160).ToArray());
        return string.IsNullOrWhiteSpace(value) ? "未命名任务" : value.Trim();
    }
    private static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    private static bool ReadableError(Exception error) => error is IOException or UnauthorizedAccessException
        or ArgumentException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException;
}