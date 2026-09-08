using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CodexHud.Core.Native;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("CodexHud.Tests")]

namespace CodexHud.Core;

public static class ActivityClassifier
{
    public static ActivityState Classify(string? latestStatus, DateTimeOffset? latestEvidenceAt,
        bool appPresent, DateTimeOffset now)
    {
        if (latestStatus == "completed") return ActivityState.Completed;
        if (latestStatus == "interrupted") return ActivityState.Interrupted;
        if (appPresent && latestStatus == "inProgress" && latestEvidenceAt is { } evidence
            && evidence <= now.AddSeconds(5) && now - evidence <= TimeSpan.FromSeconds(120))
            return ActivityState.ExecutionEvidence;
        return ActivityState.Unconfirmed;
    }
}

/// <summary>Observes local top-level task history. It does not claim live approval or input state.</summary>
public sealed class ActivityProvider : IActivityProvider
{
    private readonly string _codexHome;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RolloutCursor> _cursors = new(StringComparer.Ordinal);
    private bool _disposed;
    private const int ReadBudget = 4 * 1024 * 1024;

    public ActivityProvider(string? codexHome = null)
    {
        _codexHome = Path.GetFullPath(NormalizePathPrefix(codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")));
    }

    public async Task<ActivitySnapshot> ReadAsync(bool appPresent, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => Read(appPresent, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private ActivitySnapshot Read(bool appPresent, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var tasks = new List<TaskActivity>();
        try
        {
            var statePath = FindDatabase("state_");
            if (statePath is null)
                return new(now, appPresent, tasks, SampleHealth.Unavailable, "未找到本机 Codex 任务索引");
            using var state = new SqliteReader(statePath);
            var columns = state.Columns("threads");
            Require(columns, "id", "title", "cwd", "source", "rollout_path", "archived", "updated_at");
            string name = columns.Contains("name") ? "name" : "NULL AS name";
            string threadSource = columns.Contains("thread_source") ? "thread_source" : "NULL AS thread_source";
            var records = state.Query($"SELECT id, title, {name}, cwd, source, {threadSource}, rollout_path, updated_at " +
                                      "FROM threads WHERE archived=0 ORDER BY updated_at DESC");
            using var history = OpenHistory(out bool historyUnsupported);
            int remainingBudget = ReadBudget;
            int inaccessible = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsTopLevelLocal(row)) continue;
                string id = row.Text("id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                seen.Add(id);
                var baseline = ReadHistory(history, id);
                string? path = AllowedRolloutPath(row.Text("rollout_path"));
                if (!_cursors.TryGetValue(id, out var cursor)) _cursors[id] = cursor = new RolloutCursor(id);
                if (path is null) cursor.Invalidate();
                bool readable = path is not null && cursor.Update(path, ref remainingBudget);
                if (!readable) inaccessible++;
                var merged = Merge(baseline, cursor.Evidence);
                // History timestamps establish a turn start, never real-time approval/input state.
                DateTimeOffset? evidenceAt = merged.LastActivityAt ?? merged.StartedAt;
                var status = ActivityClassifier.Classify(merged.Status, evidenceAt, appPresent, now);
                string title = string.IsNullOrWhiteSpace(row.Text("name")) ? row.Text("title") : row.Text("name");
                title = new string(title.Where(c => !char.IsControl(c)).Take(160).ToArray()).Trim();
                if (title.Length == 0) title = "未命名任务";
                tasks.Add(new(id, title, status, evidenceAt, merged.StartedAt,
                    baseline.Status is null ? "本地日志推断" : "本地历史与日志推断", merged.EndedAt)
                {
                    TurnId = merged.TurnId,
                    TokenUsage = cursor.Tokens.Snapshot(merged.TurnId, now, readable && cursor.IsCaughtUp,
                        status is ActivityState.ExecutionEvidence or ActivityState.Unconfirmed)
                });
            }
            foreach (var id in _cursors.Keys.Where(id => !seen.Contains(id)).ToArray()) _cursors.Remove(id);
            // Never cap unfinished candidates; only the historical completed/interrupted portion is bounded.
            var unfinished = tasks.Where(t => t.State is ActivityState.ExecutionEvidence or ActivityState.Unconfirmed);
            var finished = tasks.Where(t => t.State is ActivityState.Completed or ActivityState.Interrupted)
                .OrderByDescending(t => t.EvidenceAt).Take(30);
            var ordered = unfinished.Concat(finished)
                .OrderBy(t => t.State == ActivityState.ExecutionEvidence ? 0 : t.State == ActivityState.Unconfirmed ? 1 : 2)
                .ThenByDescending(t => t.EvidenceAt).ToArray();
            string message = "本地活动证据；无法确认实时运行、审批或输入等待";
            if (!appPresent) message = "Codex 未运行；未结束的历史记录状态未确认";
            if (historyUnsupported) message += "；历史格式暂不支持";
            if (inaccessible > 0) message += "；部分日志不可读";
            if (remainingBudget <= 0) message += "；正在补齐本地记录";
            return new(now, appPresent, ordered, SampleHealth.Partial, message);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return new(now, appPresent, Array.Empty<TaskActivity>(), SampleHealth.Unavailable,
                "本地任务索引不可读或格式暂不支持");
        }
    }

    private string? FindDatabase(string prefix)
    {
        if (!Directory.Exists(_codexHome)) return null;
        return Directory.EnumerateFiles(_codexHome, prefix + "*.sqlite", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Version: int.TryParse(Path.GetFileNameWithoutExtension(path)[prefix.Length..], out int v) ? v : -1))
            .Where(file => file.Version >= 0).OrderByDescending(file => file.Version)
            .Select(file => file.Path).FirstOrDefault();
    }

    private SqliteReader? OpenHistory(out bool unsupported)
    {
        unsupported = false;
        var path = FindDatabase("thread_history_");
        if (path is null) return null;
        SqliteReader? reader = null;
        try
        {
            reader = new(path);
            Require(reader.Columns("thread_turns"), "thread_id", "turn_id", "status", "started_at", "completed_at", "rollout_ordinal");
            return reader;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            reader?.Dispose();
            unsupported = true;
            return null;
        }
    }

    private static void Require(HashSet<string> columns, params string[] required)
    {
        if (required.Any(column => !columns.Contains(column))) throw new InvalidOperationException("Unsupported SQLite schema.");
    }

    private static RolloutEvidence ReadHistory(SqliteReader? history, string id)
    {
        if (history is null) return new();
        var row = history.Query("SELECT turn_id,status,started_at,completed_at FROM thread_turns " +
                                "WHERE thread_id=? ORDER BY rollout_ordinal DESC LIMIT 1", id).FirstOrDefault();
        if (row is null) return new();
        string status = row.Text("status");
        var started = FromUnix(row.Number("started_at"));
        var completed = FromUnix(row.Number("completed_at"));
        // Preserve the latest turn even when its status belongs to a newer schema. Discarding it
        // would let an older rollout's inProgress marker masquerade as the current lifecycle.
        return new(string.IsNullOrWhiteSpace(status) ? "unknown" : status, row.Text("turn_id"),
            started, completed ?? started, completed ?? started,
            status is "completed" or "interrupted" ? completed : null);
    }

    internal static RolloutEvidence Merge(RolloutEvidence history, RolloutEvidence rollout)
    {
        bool sameTurn = !string.IsNullOrEmpty(history.TurnId) && history.TurnId == rollout.TurnId;
        bool differentTurns = !string.IsNullOrEmpty(history.TurnId) && !string.IsNullOrEmpty(rollout.TurnId)
            && !sameTurn;
        if (history.Status is not null and not ("inProgress" or "completed" or "interrupted"))
        {
            // A same-turn explicit terminal log is still evidence of completion/interruption.
            // Otherwise a new/unrecognized history status remains unconfirmed until supported
            // lifecycle evidence arrives; an old execution marker must never override it.
            bool sameTurnTerminal = rollout.Status is "completed" or "interrupted"
                && sameTurn
                && rollout.StatusAt.HasValue && history.StartedAt.HasValue && rollout.StatusAt >= history.StartedAt;
            return sameTurnTerminal ? MergeTurnTiming(rollout, history, rollout)
                : history with { LastActivityAt = sameTurn ? Max(history.LastActivityAt, rollout.LastActivityAt) : history.LastActivityAt };
        }
        if (sameTurn)
        {
            // SQLite history can use whole seconds while the JSONL marker uses milliseconds.
            // An explicit terminal state cannot regress to that same turn's start marker merely
            // because its timestamp was rounded or completed_at is unavailable. New turn IDs
            // still follow the normal chronological merge below.
            if (history.Status is "completed" or "interrupted" && rollout.Status == "inProgress")
                return MergeTurnTiming(history, history, rollout);
            if (rollout.Status is "completed" or "interrupted" && history.Status == "inProgress")
                return MergeTurnTiming(rollout, history, rollout);
        }
        var basis = rollout.StatusAt.HasValue && (!history.StatusAt.HasValue || rollout.StatusAt >= history.StatusAt)
            ? rollout : history;
        // Late writes to another turn cannot extend this turn's elapsed evidence or make a
        // recent historical start look active. Anonymous tails retain only the existing local
        // inference path; if they follow a terminal marker, its timing is explicitly discarded.
        var activity = differentTurns ? basis.LastActivityAt : Max(history.LastActivityAt, rollout.LastActivityAt);
        // A later execution record after an old terminal marker does not prove the new turn's lifecycle.
        if (basis.Status is "completed" or "interrupted" && activity > basis.StatusAt?.AddSeconds(1))
            return basis with { Status = null, StartedAt = null, EndedAt = null, LastActivityAt = activity };
        return sameTurn ? MergeTurnTiming(basis, history, rollout) : basis with { LastActivityAt = activity };
    }

    private static RolloutEvidence MergeTurnTiming(RolloutEvidence basis, RolloutEvidence history,
        RolloutEvidence rollout)
    {
        // Only identified copies of the same turn can fill each other's missing endpoints.
        // A bounded cold-tail read commonly sees a terminal marker without its task_started.
        var ended = basis.Status is "completed" or "interrupted"
            ? basis.EndedAt ?? (ReferenceEquals(basis, history) ? rollout.EndedAt : history.EndedAt) : null;
        var started = rollout.StartedAt ?? history.StartedAt;
        // Prefer a coherent pair from history when its whole-second completion timestamp is
        // earlier than the fractional rollout start. Never invent a completion timestamp.
        if (started is { } preciseStart && ended is { } roundedEnd && history.StartedAt is { } historyStart
            && preciseStart > roundedEnd && preciseStart - roundedEnd < TimeSpan.FromSeconds(1)
            && roundedEnd.Ticks % TimeSpan.TicksPerSecond == 0
            && historyStart.Ticks % TimeSpan.TicksPerSecond == 0
            && preciseStart.ToUnixTimeSeconds() == historyStart.ToUnixTimeSeconds()
            && historyStart <= roundedEnd) started = historyStart;
        return basis with
        {
            StartedAt = started,
            EndedAt = ended,
            LastActivityAt = Max(history.LastActivityAt, rollout.LastActivityAt)
        };
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left > right ? left : right;

    private static bool IsTopLevelLocal(SqliteRow row)
    {
        string cwd = NormalizePathPrefix(row.Text("cwd"));
        if (cwd.Length < 3 || !char.IsAsciiLetter(cwd[0]) || cwd[1] != ':' || (cwd[2] != '\\' && cwd[2] != '/')) return false;
        if (row.Text("thread_source") is "subagent" or "guardian_review") return false;
        string source = row.Text("source");
        if (source.StartsWith('{')) return false;
        return source is "cli" or "vscode" or "exec" or "appServer" or "app-server" or "desktop";
    }

    private string? AllowedRolloutPath(string value)
    {
        value = NormalizePathPrefix(value);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return null;
        try
        {
            string path = Path.GetFullPath(value);
            string root = Path.Combine(_codexHome, "sessions") + Path.DirectorySeparatorChar;
            // This adapter never searches remote hosts or recursively scans the sessions tree.
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch (ArgumentException) { return null; }
    }

    private static string NormalizePathPrefix(string path) =>
        path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;

    internal static DateTimeOffset? FromUnix(long? timestamp)
    {
        if (timestamp is null or <= 0) return null;
        try { return timestamp > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value) : DateTimeOffset.FromUnixTimeSeconds(timestamp.Value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public void Dispose() => _disposed = true;
}

internal sealed record RolloutEvidence(string? Status = null, string? TurnId = null,
    DateTimeOffset? StartedAt = null, DateTimeOffset? LastActivityAt = null, DateTimeOffset? StatusAt = null,
    DateTimeOffset? EndedAt = null);

/// <summary>Stores only file identity/offset and event metadata. Incomplete lines are reread, never retained.</summary>
internal sealed class RolloutCursor(string? threadId = null)
{
    internal const int MaximumRead = 256 * 1024;
    private string? _path;
    private string? _identity;
    private long _offset;
    private bool _initialized;
    private bool _skipToNewline;
    private byte[]? _anchorHash;
    private long _observedLength = -1;
    private long _observedWriteTime;
    public RolloutEvidence Evidence { get; private set; } = new();
    internal TokenUsageTracker Tokens { get; } = new(threadId);
    internal bool IsCaughtUp { get; private set; }
    internal long Offset => _offset;

    public bool Update(string path, ref int budget)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            string identity = FileIdentity(stream.SafeFileHandle, out long writeTime);
            long length = stream.Length;
            bool fileChanged = !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)
                || identity != _identity || length < _offset
                || (_anchorHash is not null && !_anchorHash.AsSpan().SequenceEqual(AnchorHash(stream, _offset)));
            if (!_initialized || fileChanged)
            {
                _path = path;
                _identity = identity;
                _offset = Math.Max(0, length - MaximumRead);
                _skipToNewline = _offset > 0;
                Evidence = new();
                // A temporary read failure requires rechecking the bounded tail, but does not
                // erase previously verified counters when file identity and anchor still match.
                // A replacement, truncation, or rewrite is a different epoch and must reset.
                if (fileChanged) Tokens.Reset();
                else Tokens.MarkGap();
                _anchorHash = null;
                _observedLength = -1;
                _initialized = true;
            }
            IsCaughtUp = length == _offset;
            if (budget <= 0 || length == _offset) return true;
            // A crashed/incomplete final JSON record can remain unchanged indefinitely. Remember
            // reaching that EOF without retaining its body, so it does not consume the shared
            // read budget on every poll and starve tasks later in the index.
            if (_observedLength == length && _observedWriteTime == writeTime) return true;
            if (length - _offset > MaximumRead)
            {
                _offset = length - MaximumRead;
                _skipToNewline = true;
                Evidence = new();
                Tokens.MarkGap();
            }
            int size = (int)Math.Min(Math.Min(length - _offset, MaximumRead), budget);
            var buffer = new byte[size];
            stream.Position = _offset;
            int bytes = stream.Read(buffer, 0, size);
            long readEnd = _offset + bytes;
            budget -= bytes;
            int start = 0;
            for (int i = 0; i < bytes; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                if (_skipToNewline) _skipToNewline = false;
                else Observe(buffer.AsMemory(start, i - start));
                start = i + 1;
            }
            _offset += start;
            IsCaughtUp = _offset == length;
            if (start == 0 && bytes == MaximumRead)
            {
                // Oversized JSON records are skipped without retaining prompts or message bodies.
                _offset += bytes;
                _skipToNewline = true;
                Tokens.MarkGap();
            }
            if (readEnd == length)
            {
                _observedLength = length;
                _observedWriteTime = writeTime;
            }
            _anchorHash = AnchorHash(stream, _offset);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Invalidate();
            return false;
        }
    }

    public void Invalidate()
    {
        Evidence = new();
        IsCaughtUp = false;
        _initialized = false;
        // Retain identity, offset, and numeric metadata to distinguish recovery from rotation.
        Tokens.MarkGap();
        _observedLength = -1;
    }

    private static byte[] AnchorHash(FileStream stream, long offset)
    {
        Span<byte> anchor = stackalloc byte[(int)Math.Min(64, offset)];
        stream.Position = offset - anchor.Length;
        int count = stream.Read(anchor);
        return SHA256.HashData(anchor[..count]);
    }

    private void Observe(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var rootType) || !root.TryGetProperty("timestamp", out var time)) return;
            var timestamp = ParseTime(time);
            if (timestamp is null || timestamp > DateTimeOffset.UtcNow.AddMinutes(1)) return;
            string? kind = rootType.GetString();
            if (kind == "token_usage_record" && root.TryGetProperty("payload", out var usage))
                Tokens.Observe(usage, timestamp.Value);
            else if (kind == "event_msg" && root.TryGetProperty("payload", out var payload)
                && payload.TryGetProperty("type", out var eventType))
            {
                string? eventName = eventType.GetString();
                string? turnId = payload.TryGetProperty("turn_id", out var turn) && turn.ValueKind == JsonValueKind.String ? turn.GetString() : null;
                if (eventName == "task_started")
                    Evidence = new("inProgress", turnId, timestamp, timestamp, timestamp);
                else if (eventName is "task_complete" or "turn_aborted")
                    Evidence = new(eventName == "task_complete" ? "completed" : "interrupted", turnId,
                        !string.IsNullOrEmpty(turnId) && turnId == Evidence.TurnId ? Evidence.StartedAt : null,
                        timestamp, timestamp, timestamp);
                else if (eventName is "item_completed" or "token_count")
                    Activity(timestamp.Value);
            }
            else if (kind == "response_item" && root.TryGetProperty("payload", out var item)
                && item.TryGetProperty("type", out var itemType)
                && itemType.GetString() is "reasoning" or "function_call" or "function_call_output"
                    or "custom_tool_call" or "custom_tool_call_output" or "agent_message")
                Activity(timestamp.Value);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
    }

    private void Activity(DateTimeOffset timestamp)
    {
        if (Evidence.LastActivityAt is null || timestamp > Evidence.LastActivityAt)
            Evidence = Evidence with { LastActivityAt = timestamp };
    }

    private static DateTimeOffset? ParseTime(JsonElement time)
    {
        if (time.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(time.GetString(),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result)) return result;
        if (time.ValueKind == JsonValueKind.Number && time.TryGetInt64(out long timestamp)) return ActivityProvider.FromUnix(timestamp);
        return null;
    }

    private static string FileIdentity(SafeFileHandle handle, out long writeTime)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot determine log identity.");
        writeTime = ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime;
        return $"{info.VolumeSerialNumber}:{info.FileIndexHigh}:{info.FileIndexLow}:{info.CreationTime.dwHighDateTime}:{info.CreationTime.dwLowDateTime}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
