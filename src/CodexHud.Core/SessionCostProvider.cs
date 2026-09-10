using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json;
using CodexHud.Core.Native;

namespace CodexHud.Core;

/// <summary>Local lifetime API-equivalent costs, independent of quota and workload baselines.</summary>
public sealed class SessionCostProvider : IDisposable
{
    private const int CheckpointVersion = 2, MaximumLineBytes = 4 * 1024 * 1024;
    private readonly string _home, _homeHash;
    private readonly string? _checkpointPath;
    private readonly int _readBudget;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, FileState> _files;
    private readonly Dictionary<string, ThreadState> _threads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingLine> _pending;
    private string[]? _tracked;
    private int _roundRobin;
    private bool _discoveryComplete, _dirty, _disposed;
    private string? _checkpointError;
    private DateTimeOffset _lastSaved;
    private readonly TaskTurnHistoryReader _history;
    private long _responseRevision, _snapshotResponseRevision = -1;
    private HashSet<string> _snapshotOwners = new(StringComparer.Ordinal);
    private IReadOnlyList<LocalUsageResponse> _responseSnapshot = Array.AsReadOnly(Array.Empty<LocalUsageResponse>());

    public SessionCostProvider(string codexHome, string? checkpointPath = null, int bytesPerRead = 4 * 1024 * 1024)
    {
        _home = Path.GetFullPath(codexHome);
        _homeHash = UsageLedgerCheckpoint.Hash(OperatingSystem.IsWindows() ? _home.ToUpperInvariant() : _home);
        _checkpointPath = checkpointPath;
        _readBudget = Math.Max(1, bytesPerRead);
        _history = new TaskTurnHistoryReader(_home);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _files = new(comparer); _pending = new(comparer);
        Restore();
    }

    public void SetTrackedThreads(IEnumerable<string> threadIds) =>
        Volatile.Write(ref _tracked, threadIds.Where(ValidId).Distinct(StringComparer.Ordinal).ToArray());

    public async Task<SessionCostSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Read(cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private SessionCostSnapshot Read(CancellationToken cancellationToken)
    {
        int budget = _readBudget;
        Discover(ref budget, cancellationToken);

        // Tracking controls the old task-card projection only. One bounded scanner feeds both
        // that projection and the complete local period ledger, including archived tasks.
        var candidates = _files.Values.Where(f => f.Owner != null && !f.Excluded && !f.Missing && !f.Superseded)
            .OrderByDescending(f => f.LastWriteAt).ThenBy(f => f.Path, StringComparer.Ordinal).ToArray();
        // Reserve at most one quarter each for fresh appends and the newest unread history.
        // At least half remains for round-robin backfill, even when the newest file keeps growing.
        int prioritySlice = budget / 4;
        void Prioritize(FileState? file)
        {
            if (file == null || prioritySlice == 0 || budget <= 0) return;
            int allowance = Math.Min(prioritySlice, budget), available = allowance;
            ReadFile(file, ref allowance, cancellationToken);
            budget -= available - allowance;
        }
        var appended = candidates.FirstOrDefault(file => file.Complete && HasUnreadBytes(file));
        Prioritize(appended);
        var recent = candidates.FirstOrDefault(file => !ReferenceEquals(file, appended)
            && !file.Complete && HasUnreadBytes(file));
        Prioritize(recent);
        int start = candidates.Length == 0 ? 0 : _roundRobin % candidates.Length;
        // Discovery alone may consume several complete polls on large installations. Never
        // advance past the newest tasks until some backfill budget is actually available.
        if (budget > 0)
        {
            for (int index = 0; index < candidates.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A priority file may use otherwise available round-robin capacity as well;
                // this keeps small installations from wasting three quarters of their budget.
                ReadFile(candidates[(start + index) % candidates.Length], ref budget, cancellationToken);
                if (budget <= 0) { _roundRobin = start + index + 1; break; }
            }
            if (budget > 0) _roundRobin++;
        }
        var metadata = Metadata();
        var targets = Targets(metadata);
        var now = DateTimeOffset.UtcNow;
        var history = _history.Read(metadata.Keys.ToHashSet(StringComparer.Ordinal), now, cancellationToken);
        var result = Snapshot(metadata, targets) with { Ledger = Ledger(metadata, history, now) };
        Save(false);
        return result;
    }

    private void Discover(ref int budget, CancellationToken cancellationToken)
    {
        _discoveryComplete = true;
        var seen = new HashSet<string>(_files.Comparer);
        try
        {
            foreach (string directory in new[] { "sessions", "archived_sessions" })
            {
                string root = Path.Combine(_home, directory);
                if (!Directory.Exists(root)) continue;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (string path in Directory.EnumerateFiles(root, "*.jsonl", options)
                    .OrderByDescending(File.GetLastWriteTimeUtc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!LocalPath(path)) continue;
                    seen.Add(path);
                    if (!_files.TryGetValue(path, out var file))
                    { file = new FileState { Path = path }; _files.Add(path, file); _dirty = true; }
                    file.Missing = false; file.Superseded = false;
                    var info = new FileInfo(path);
                    file.LastWriteAt = info.LastWriteTimeUtc;
                    file.ObservedLength = info.Length;
                    file.Archived = directory == "archived_sessions";
                    if (file.Owner != null || file.Excluded) continue;
                    // A stable malformed header must not spend the entire discovery budget on
                    // every poll and indefinitely starve readable files later in the directory.
                    if (file.Error != null && file.HeaderCheckedAt == file.LastWriteAt && file.HeaderCheckedLength == file.ObservedLength)
                    { _discoveryComplete = false; continue; }
                    if (budget <= 0) { _discoveryComplete = false; continue; }
                    try
                    {
                        file.HeaderCheckedAt = file.LastWriteAt; file.HeaderCheckedLength = file.ObservedLength;
                        using var stream = Open(path);
                        var header = new List<byte>();
                        while (header.Count < 64 * 1024)
                        {
                            int value = stream.ReadByte();
                            if (value < 0 || value == '\n') break;
                            header.Add((byte)value);
                        }
                        // A header has a separate hard cap so even a tiny read budget progresses.
                        budget -= Math.Min(budget, header.Count + 1);
                        using var doc = JsonDocument.Parse(header.ToArray());
                        if (Text(doc.RootElement, "type") != "session_meta"
                            || !doc.RootElement.TryGetProperty("payload", out var payload)
                            || Text(payload, "id") is not { } owner || !ValidId(owner))
                        { file.Error = "日志首条会话身份无效"; _discoveryComplete = false; continue; }
                        SetHeader(file, payload, owner);
                        file.Identity = LogFileIdentity.Read(stream.SafeFileHandle, out _);
                        file.Error = null; _dirty = true;
                    }
                    catch (Exception ex) when (ReadError(ex))
                    { file.Error = "日志头暂不可读取"; _discoveryComplete = false; }
                }
            }
            var liveIdentities = _files.Values.Where(f => seen.Contains(f.Path) && f.Identity.Length > 0)
                .Select(f => f.Identity).ToHashSet(StringComparer.Ordinal);
            foreach (var file in _files.Values)
            {
                if (seen.Contains(file.Path)) continue;
                // Archive moves preserve file identity; the new path will be read/deduplicated.
                file.Superseded = liveIdentities.Contains(file.Identity);
                file.Missing = !file.Superseded;
            }
        }
        catch (Exception ex) when (ReadError(ex)) { _discoveryComplete = false; }
    }

    private static void SetHeader(FileState file, JsonElement payload, string owner)
    {
        file.Owner = owner; file.SessionId = Text(payload, "session_id");
        file.Title = Text(payload, "title") ?? Text(payload, "agent_path") ?? Text(payload, "agent_nickname");
        file.HasHistoryBase |= payload.TryGetProperty("history_base", out _);
        file.Provider = Text(payload, "model_provider"); file.Parent = Text(payload, "parent_thread_id");
        string? source = Text(payload, "thread_source");
        file.IsSubagent = source == "subagent"; file.Excluded = source == "guardian_review";
        if (payload.TryGetProperty("source", out var sourceElement))
        {
            file.IsSubagent |= sourceElement.ValueKind == JsonValueKind.String && sourceElement.GetString() == "subagent";
            if (sourceElement.ValueKind == JsonValueKind.Object && sourceElement.TryGetProperty("subagent", out var subagent))
            {
                file.IsSubagent = true;
                if (subagent.ValueKind == JsonValueKind.String) file.Excluded |= subagent.GetString() == "guardian_review";
                if (subagent.ValueKind == JsonValueKind.Object && subagent.TryGetProperty("thread_spawn", out var spawn))
                    file.Parent ??= Text(spawn, "parent_thread_id");
            }
        }
        file.IsSubagent |= file.Parent != null;
        // forked_from_id is intentionally not a parent link: it is also present on agents.
        if (file.Parent is { } parent && !ValidId(parent)) { file.Parent = null; file.Gap = true; }
    }

    private Dictionary<string, Meta> Metadata()
    {
        var result = new Dictionary<string, Meta>(StringComparer.Ordinal);
        foreach (var group in _files.Values.Where(f => f.Owner != null).GroupBy(f => f.Owner!, StringComparer.Ordinal))
        {
            var active = group.Where(f => !f.Excluded && !f.Superseded).ToArray();
            if (active.Length == 0) continue;
            var parents = active.Select(f => f.Parent).Distinct(StringComparer.Ordinal).ToArray();
            result[group.Key] = new(parents.Length == 1 ? parents[0] : null,
                active.Any(f => f.IsSubagent), parents.Length > 1, active);
        }
        return result;
    }

    private HashSet<string> Targets(Dictionary<string, Meta> metadata)
    {
        var tracked = Volatile.Read(ref _tracked);
        var targets = new HashSet<string>(tracked ?? metadata.Keys.ToArray(), StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var item in metadata)
                if (item.Value.Parent is { } parent && targets.Contains(parent)) changed |= targets.Add(item.Key);
        } while (changed);
        return targets;
    }

    private bool HasUnreadBytes(FileState file) =>
        (_pending.TryGetValue(file.Path, out var pending) ? pending.ReadOffset : file.Offset) < file.ObservedLength;

    private void ReadFile(FileState file, ref int budget, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = Open(file.Path);
            string identity = LogFileIdentity.Read(stream.SafeFileHandle, out _);
            bool changed = identity != file.Identity || stream.Length < file.Offset
                || file.Offset > 0 && Anchor(stream, file.Offset) != file.Anchor;
            if (changed)
            {
                file.Offset = 0; file.Anchor = ""; file.Identity = identity;
                file.Profiles.Clear(); file.Turns.Clear(); file.CurrentTurn = null;
                file.Discontinuous = true; file.Complete = false;
                _pending.Remove(file.Path); _dirty = true;
            }
            if (!_pending.TryGetValue(file.Path, out var pending))
                _pending[file.Path] = pending = new PendingLine(file.Offset);
            if (pending.ReadOffset > stream.Length)
            { _pending[file.Path] = pending = new PendingLine(file.Offset); file.Discontinuous = true; }
            file.Complete = stream.Length == file.Offset;
            file.Error = null;
            if (stream.Length == pending.ReadOffset || budget <= 0) return;
            stream.Position = pending.ReadOffset;
            int length = (int)Math.Min(Math.Min(budget, 512 * 1024), stream.Length - stream.Position);
            byte[] bytes = new byte[length];
            int count = stream.Read(bytes);
            budget -= count;
            for (int index = 0; index < count; index++)
            {
                if ((index & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
                byte value = bytes[index]; pending.ReadOffset++;
                if (value == '\n')
                {
                    if (!pending.Oversized) Observe(file, pending.Bytes.ToArray());
                    else file.Gap = true;
                    pending.Bytes.SetLength(0); pending.Oversized = false;
                    file.Offset = pending.ReadOffset; _dirty = true;
                }
                else if (!pending.Oversized)
                {
                    if (pending.Bytes.Length >= MaximumLineBytes)
                    { pending.Bytes.SetLength(0); pending.Oversized = true; }
                    else pending.Bytes.WriteByte(value);
                }
            }
            file.Anchor = Anchor(stream, file.Offset);
            file.Complete = file.Offset == stream.Length;
        }
        catch (OperationCanceledException)
        {
            // A canceled read may have consumed complete records: its persisted anchor must
            // match the committed offset, while an incomplete line stays memory-only.
            try { using var stream = Open(file.Path); file.Anchor = Anchor(stream, file.Offset); }
            catch (Exception ex) when (ReadError(ex)) { file.Error = "读取中断，等待重新校验日志"; }
            throw;
        }
        catch (Exception ex) when (ReadError(ex)) { file.Error = "日志暂不可读取；保留此前费用"; }
    }

    private void Observe(FileState file, byte[] bytes)
    {
        if (bytes.Length == 0) return;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            string? type = Text(root, "type");
            if (!root.TryGetProperty("payload", out var payload)) return;
            if (type == "session_meta")
            {
                // Copied headers later in a child log cannot overwrite the physical owner.
                if (file.Offset == 0)
                {
                    if (Text(payload, "id") != file.Owner) { file.IdentityConflict = true; file.Gap = true; }
                    else
                    {
                        // A replaced segment can keep the task owner but start a new session.
                        // Preserve the prior response ledger while accepting its new own records.
                        SetHeader(file, payload, file.Owner!);
                    }
                }
                return;
            }
            if (file.IdentityConflict) return;
            if (type == "event_msg" && Text(payload, "turn_id") is { } eventTurn && ValidId(eventTurn)
                && (Text(payload, "thread_id") is not { } eventOwner || eventOwner == file.Owner)
                && Time(root) is { } eventAt && eventAt <= DateTimeOffset.UtcNow)
            {
                string? eventName = Text(payload, "type");
                if (eventName is "task_started" or "task_complete" or "turn_aborted")
                {
                    if (!file.Turns.TryGetValue(eventTurn, out var timing)) file.Turns[eventTurn] = timing = new();
                    if (eventName == "task_started")
                    {
                        timing.StartedAt = Earlier(timing.StartedAt, eventAt);
                        file.CurrentTurn = eventTurn;
                    }
                    else
                    {
                        timing.EndedAt = Earlier(timing.EndedAt, eventAt);
                        timing.State = eventName == "task_complete" ? ActivityState.Completed : ActivityState.Interrupted;
                    }
                    timing.EvidenceAt = Later(timing.EvidenceAt, eventAt);
                }
            }
            if (type == "response_item" && Text(payload, "role") == "assistant"
                || type == "event_msg" && Text(payload, "type") is "token_count" or "task_started" or "task_complete")
                file.SawActivity = true;
            if (type == "turn_context")
            {
                if (Text(payload, "thread_id") is { } owner && owner != file.Owner) return;
                if (Text(payload, "turn_id") is { } turn && ValidId(turn))
                    file.Profiles[turn] = new Profile(Text(payload, "model"),
                        Text(payload, "service_tier") ?? Text(payload, "speed"), Text(payload, "model_provider") ?? file.Provider);
                return;
            }
            if (type != "token_usage_record" || file.Excluded || Text(payload, "thread_id") != file.Owner) return;
            if (Text(payload, "session_id") is { } session && file.SessionId is { } expectedSession && session != expectedSession)
            { file.Gap = true; return; }
            string? response = Text(payload, "response_id"), turnId = Text(payload, "turn_id");
            if (!ValidId(response) || !ValidId(turnId)) { file.Gap = true; return; }
            DateTimeOffset? at = Time(root);
            var counts = TokenUsageTracker.ReadCounts(payload, "usage").Counts ?? new TokenCounts(null, null, null, null, null, null);
            var cumulative = TokenUsageTracker.ReadCounts(payload, "thread_token_usage").Counts;
            if (cumulative != null && (file.ExpectedAt == null || at >= file.ExpectedAt))
            { file.Expected = cumulative; file.ExpectedAt = at; }
            if (!_threads.TryGetValue(file.Owner!, out var thread))
                _threads[file.Owner!] = thread = new ThreadState { Id = file.Owner! };
            file.Profiles.TryGetValue(turnId!, out var profile);
            var record = new ResponseRecord { ResponseId = response!, TurnId = turnId, At = at, Counts = counts,
                Model = Text(payload, "model") ?? profile?.Model,
                Tier = Text(payload, "service_tier") ?? Text(payload, "speed") ?? profile?.Tier,
                Provider = Text(payload, "model_provider") ?? profile?.Provider ?? file.Provider };
            if (thread.Responses.TryGetValue(response!, out var previous))
            {
                if (previous.Counts != record.Counts || Conflicts(previous.Model, record.Model)
                    || Conflicts(previous.Tier, record.Tier) || Conflicts(previous.Provider, record.Provider)
                    || Conflicts(previous.TurnId, record.TurnId)) thread.Conflict = true;
                else
                {
                    // v1 responses retain their numeric evidence while replay fills turn identity.
                    var firstRecordedAt = Earlier(previous.At, record.At);
                    bool changed = previous.TurnId == null && record.TurnId != null
                        || previous.At != firstRecordedAt || previous.Model == null && record.Model != null
                        || previous.Tier == null && record.Tier != null || previous.Provider == null && record.Provider != null;
                    previous.TurnId ??= record.TurnId; previous.At = firstRecordedAt;
                    previous.Model ??= record.Model; previous.Tier ??= record.Tier; previous.Provider ??= record.Provider;
                    if (changed) { previous.CachedPrice = null; _responseRevision++; }
                }
            }
            else { thread.Responses.Add(response!, record); _responseRevision++; }
            if (!file.Turns.TryGetValue(turnId!, out var ownTurn)) file.Turns[turnId!] = ownTurn = new();
            ownTurn.Confirmed = true;
            // A repeated response must not extend elapsed evidence with a replay timestamp.
            ownTurn.EvidenceAt = Later(ownTurn.EvidenceAt, thread.Responses[response!].At);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { file.Gap = true; }
    }

    private LocalUsageLedgerSnapshot Ledger(Dictionary<string, Meta> metadata,
        TaskTurnHistoryReader.Result history, DateTimeOffset now)
    {
        var tasks = new Dictionary<string, LocalUsageTask>(StringComparer.Ordinal);
        foreach (var item in metadata)
        {
            string id = item.Key;
            var meta = item.Value;
            var indexed = history.Tasks.GetValueOrDefault(id);
            if (indexed?.Excluded == true) continue;
            var notes = new List<string>();
            bool loading = !_discoveryComplete || meta.Files.Any(file => (!file.Complete || file.ObservedLength > file.Offset) && !file.Missing && file.Error == null);
            bool stale = meta.Files.Any(file => file.Error != null || file.Missing || file.Discontinuous);
            bool partial = meta.Conflict || meta.Files.Any(file => file.Gap) || _threads.GetValueOrDefault(id)?.Conflict == true;
            if (loading) notes.Add("正在补齐本地历史，当前汇总仅含已读取记录");
            if (stale) notes.Add("部分日志缺失、替换或暂不可读，保留此前记录");
            if (meta.Conflict) notes.Add("父子关系冲突，任务单独列示");
            if (meta.Files.Any(file => file.Gap)) notes.Add("部分日志记录无效或归属不匹配");
            if (_threads.GetValueOrDefault(id)?.Conflict == true) notes.Add("同一响应记录冲突，仅保留一份已记录用量");
            var responses = _threads.GetValueOrDefault(id)?.Responses.Values.ToArray() ?? [];
            if (responses.Length == 0 && meta.Files.Any(file => file.SawActivity || file.HasHistoryBase))
            { partial = true; notes.Add("已有任务活动但逐响应用量尚未恢复"); }
            var expected = meta.Files.Where(file => file.Expected != null).OrderBy(file => file.ExpectedAt).LastOrDefault()?.Expected;
            if (responses.Length > 0 && (expected == null
                || expected.InputTokens != responses.Sum(response => (decimal)(response.Counts.InputTokens ?? 0))
                || expected.OutputTokens != responses.Sum(response => (decimal)(response.Counts.OutputTokens ?? 0))
                || expected.CachedInputTokens != responses.Sum(response => (decimal)(response.Counts.CachedInputTokens ?? 0))))
            { partial = true; notes.Add("逐响应记录与累计计数尚未对齐，可能缺少历史日志段"); }
            if (responses.Any(response => response.At == null || response.TurnId == null))
            { partial = true; notes.Add("正在恢复响应时间或轮次身份"); }
            bool isSubagent = meta.IsSubagent || indexed?.IsSubagent == true;
            if (isSubagent && meta.Parent == null) { partial = true; notes.Add("父任务尚无法确认，独立列示且只计一次"); }
            string title = indexed?.Title ?? TaskTurnHistoryReader.CleanTitle(meta.Files.Select(file => file.Title).FirstOrDefault(value => value != null));
            tasks[id] = new(id, title, meta.Parent, isSubagent,
                indexed?.Archived ?? meta.Files.All(file => file.Archived),
                stale ? SampleHealth.Stale : loading ? SampleHealth.Loading : partial ? SampleHealth.Partial : SampleHealth.Fresh,
                Array.AsReadOnly(notes.ToArray()));
        }

        if (_snapshotResponseRevision != _responseRevision || !_snapshotOwners.SetEquals(tasks.Keys))
        {
            _responseSnapshot = Array.AsReadOnly(_threads.Values.Where(thread => tasks.ContainsKey(thread.Id))
                .SelectMany(thread => thread.Responses.Values.Select(response => new LocalUsageResponse(thread.Id,
                    response.ResponseId, response.TurnId, response.At, response.Counts, response.Model, response.Tier, response.Provider)))
                .OrderBy(response => response.RecordedAt).ThenBy(response => response.ThreadId, StringComparer.Ordinal)
                .ThenBy(response => response.ResponseId, StringComparer.Ordinal).ToArray());
            _snapshotResponseRevision = _responseRevision;
            _snapshotOwners = tasks.Keys.ToHashSet(StringComparer.Ordinal);
        }
        var turns = history.Turns.Where(item => tasks.ContainsKey(item.Key.ThreadId))
            .ToDictionary(item => item.Key, item => item.Value);
        foreach (var file in metadata.Values.SelectMany(meta => meta.Files))
        {
            if (file.Owner is not { } owner || !tasks.ContainsKey(owner) || file.IdentityConflict) continue;
            foreach (var entry in file.Turns)
            {
                var key = (ThreadId: owner, TurnId: entry.Key);
                var log = entry.Value;
                bool indexed = history.Turns.TryGetValue(key, out var authoritative);
                // An inherited lifecycle marker has no owner field. Neither its physical file nor
                // an unqualified turn_context proves ownership; require the index or own response.
                if (!indexed && !log.Confirmed) continue;
                turns.TryGetValue(key, out var previous);
                var start = authoritative?.StartedAt ?? Earlier(previous?.StartedAt, log.StartedAt);
                var end = authoritative?.EndedAt ?? Earlier(previous?.EndedAt, log.EndedAt);
                var state = authoritative?.State is ActivityState.Completed or ActivityState.Interrupted
                    ? authoritative.State : log.State is ActivityState.Completed or ActivityState.Interrupted ? log.State
                    : previous?.State ?? ActivityState.Unconfirmed;
                bool invalid = start > now || end > now || start is { } origin && end is { } endpoint && endpoint < origin;
                if (invalid) { start = authoritative?.StartedAt; end = authoritative?.EndedAt; }
                // A copied anonymous marker may be retimestamped. For open turns use the
                // deduplicated start plus canonical response timestamps, not a replay's tail.
                var evidence = end ?? start;
                if (evidence > now) evidence = null;
                bool complete = !invalid && start.HasValue && end.HasValue
                    && state is ActivityState.Completed or ActivityState.Interrupted;
                turns[key] = new(owner, entry.Key, start, end, evidence, state,
                    file.Error != null || file.Missing || file.Discontinuous ? SampleHealth.Stale
                    : complete ? SampleHealth.Fresh : SampleHealth.Partial);
            }
        }
        foreach (var response in _responseSnapshot)
        {
            if (response.TurnId is not { } turnId) continue;
            var key = (response.ThreadId, turnId);
            if (!turns.TryGetValue(key, out var existing))
                turns[key] = new(response.ThreadId, turnId, null, null, response.RecordedAt,
                    ActivityState.Unconfirmed, SampleHealth.Partial);
            else if (existing.EndedAt == null && response.RecordedAt <= now)
                turns[key] = existing with { EvidenceAt = Later(existing.EvidenceAt, response.RecordedAt) };
        }
        var immutableTasks = new ReadOnlyDictionary<string, LocalUsageTask>(tasks);
        var immutableTurns = Array.AsReadOnly(turns.Values.OrderBy(turn => turn.StartedAt ?? turn.EvidenceAt)
            .ThenBy(turn => turn.ThreadId, StringComparer.Ordinal).ThenBy(turn => turn.TurnId, StringComparer.Ordinal).ToArray());
        var health = CombinedHealth(tasks.Values.Select(task => task.Health), !_discoveryComplete);
        return new(now, immutableTasks, _responseSnapshot, immutableTurns, health,
            $"本机全部普通任务 · {tasks.Count} 个任务 · {_responseSnapshot.Count} 条响应"
            + (history.Detail == null ? "" : " · " + history.Detail)
            + (_checkpointError == null ? "" : " · " + _checkpointError));
    }
    private SessionCostSnapshot Snapshot(Dictionary<string, Meta> metadata, HashSet<string> targets)
    {
        var own = new Dictionary<string, SessionCostEstimate>(StringComparer.Ordinal);
        foreach (string id in targets)
        {
            if (!metadata.TryGetValue(id, out var meta))
            { own[id] = new(id) { Health = SampleHealth.Unavailable, Notes = ["尚未找到本机会话日志"] }; continue; }
            var notes = new HashSet<string>(StringComparer.Ordinal);
            decimal minimum = 0, maximum = 0;
            long priced = 0, unpriced = 0, unpricedTokens = 0;
            bool ambiguousChild = metadata.Values.Any(child => child.Conflict && child.Files.Any(f => f.Parent == id));
            bool partial = !_discoveryComplete || meta.Conflict || ambiguousChild || meta.IsSubagent && meta.Parent == null;
            bool loading = meta.Files.Any(f => (!f.Complete || f.ObservedLength > f.Offset) && !f.Missing && f.Error == null);
            bool stale = meta.Files.Any(f => f.Error != null || f.Missing || f.Discontinuous);
            if (!_discoveryComplete) notes.Add("正在补齐会话索引；子代理覆盖尚不完整");
            if (meta.Conflict) notes.Add("会话父子关系冲突，未猜测归属");
            if (ambiguousChild) notes.Add("存在父子关系冲突的子代理，其费用尚未并入主会话");
            if (meta.IsSubagent && meta.Parent == null) notes.Add("子代理的父会话暂无法确认");
            if (loading) notes.Add("正在补齐会话历史；当前仅为已读取部分");
            if (stale) notes.Add("日志缺失、替换或暂不可读；保留已记录费用");
            if (meta.Files.Any(f => f.Gap)) { partial = true; notes.Add("部分日志记录无效或归属不匹配"); }
            var responses = _threads.GetValueOrDefault(id)?.Responses.Values.ToArray() ?? [];
            if (responses.Length == 0 && meta.Files.Any(f => f.SawActivity || f.HasHistoryBase))
            { partial = true; notes.Add("已有会话活动但未取得逐响应用量，费用未知"); }
            if (_threads.GetValueOrDefault(id)?.Conflict == true)
            { partial = true; notes.Add("同一响应的重复记录计数冲突，仅计一次"); }
            foreach (var response in responses)
            {
                var price = response.CachedPrice ??= ApiPricingCatalog.Price(response.Counts, response.Model, response.Tier, response.Provider);
                foreach (string note in price.Notes) notes.Add(note);
                if (price.IsPriced)
                { minimum += price.MinimumUsd!.Value; maximum += price.MaximumUsd ?? price.MinimumUsd.Value; priced++; }
                else
                {
                    unpriced++; unpricedTokens = SaturatingAdd(unpricedTokens, Total(response.Counts));
                    notes.Add(price.UnpricedReason ?? "部分响应用量尚未定价");
                }
                partial |= price.IsPartial;
            }
            var expected = meta.Files.Where(f => f.Expected != null).OrderBy(f => f.ExpectedAt).LastOrDefault()?.Expected;
            if (responses.Length > 0)
            {
                decimal observedInput = responses.Sum(r => (decimal)(r.Counts.InputTokens ?? 0));
                decimal observedOutput = responses.Sum(r => (decimal)(r.Counts.OutputTokens ?? 0));
                decimal observedCache = responses.Sum(r => (decimal)(r.Counts.CachedInputTokens ?? 0));
                if (expected == null || expected.InputTokens != observedInput || expected.OutputTokens != observedOutput
                    || expected.CachedInputTokens != observedCache)
                {
                    partial = true; notes.Add("逐响应用量与会话累计尚未对齐；可能缺少历史日志段");
                    decimal missing = (decimal)(expected?.TotalTokens ?? 0) - responses.Sum(r => (decimal)Total(r.Counts));
                    if (missing > 0) unpricedTokens = SaturatingAdd(unpricedTokens, (long)Math.Min(long.MaxValue, missing));
                }
            }
            own[id] = new(id) { SelfUsd = minimum, SelfUpperUsd = maximum, PricedResponses = priced,
                UnpricedResponses = unpriced, UnpricedTokens = unpricedTokens, IsSubagent = meta.IsSubagent,
                LastUsageAt = responses.Select(r => r.At).Max(), Notes = notes.Order(StringComparer.Ordinal).ToArray(),
                Health = stale ? SampleHealth.Stale : loading ? SampleHealth.Loading : partial ? SampleHealth.Partial : SampleHealth.Fresh };
        }
        var children = metadata.Where(m => m.Value.Parent != null && targets.Contains(m.Key))
            .GroupBy(m => m.Value.Parent!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Key).ToArray(), StringComparer.Ordinal);
        var results = new Dictionary<string, SessionCostEstimate>(StringComparer.Ordinal);
        foreach (var item in own)
        {
            var descendants = new HashSet<string>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal) { item.Key };
            bool cycle = false;
            void Visit(string parent)
            {
                if (!children.TryGetValue(parent, out var ids)) return;
                foreach (string child in ids)
                {
                    if (visiting.Contains(child)) { cycle = true; continue; }
                    if (!descendants.Add(child)) continue;
                    visiting.Add(child); Visit(child); visiting.Remove(child);
                }
            }
            Visit(item.Key);
            var descendantsData = descendants.Where(own.ContainsKey).Select(id => own[id]).ToArray();
            var all = new[] { item.Value }.Concat(descendantsData).ToArray();
            var notes = all.SelectMany(e => e.Notes).ToHashSet(StringComparer.Ordinal);
            if (cycle) notes.Add("子代理关联出现循环，已去重并停止递归");
            results[item.Key] = item.Value with
            {
                DescendantsUsd = descendantsData.Sum(e => e.SelfUsd),
                DescendantsUpperUsd = descendantsData.Sum(e => e.SelfUpperUsd),
                DescendantCount = descendantsData.Length,
                PricedResponses = all.Sum(e => e.PricedResponses),
                UnpricedResponses = all.Sum(e => e.UnpricedResponses),
                UnpricedTokens = all.Aggregate(0L, (sum, e) => SaturatingAdd(sum, e.UnpricedTokens)),
                LastUsageAt = all.Select(e => e.LastUsageAt).Max(),
                Health = CombinedHealth(all.Select(e => e.Health), cycle),
                Notes = notes.Order(StringComparer.Ordinal).ToArray()
            };
        }
        return new(DateTimeOffset.UtcNow, results, !_discoveryComplete || results.Values.Any(e => e.Health != SampleHealth.Fresh)
            ? SampleHealth.Partial : SampleHealth.Fresh,
            $"本机逐响应 API 估算 · {results.Count} 个会话 · 价格 {ApiPricingCatalog.Version}"
            + (_checkpointError == null ? "" : " · " + _checkpointError));
    }

    private static SampleHealth CombinedHealth(IEnumerable<SampleHealth> states, bool partial)
    {
        var all = states.ToArray();
        if (all.Contains(SampleHealth.Stale)) return SampleHealth.Stale;
        if (all.Contains(SampleHealth.Loading)) return SampleHealth.Loading;
        if (all.Length > 0 && all.All(s => s == SampleHealth.Unavailable)) return SampleHealth.Unavailable;
        return partial || all.Contains(SampleHealth.Partial) || all.Contains(SampleHealth.Unavailable)
            ? SampleHealth.Partial : SampleHealth.Fresh;
    }

    private void Restore()
    {
        if (_checkpointPath == null || !File.Exists(_checkpointPath)) return;
        try
        {
            if (new FileInfo(_checkpointPath).Length > 256L * 1024 * 1024) throw new IOException("Checkpoint too large.");
            string json = File.ReadAllText(_checkpointPath);
            var saved = JsonSerializer.Deserialize<Checkpoint>(json);
            if (saved == null || saved.Version is not (1 or CheckpointVersion) || saved.HomeHash != _homeHash)
                throw new IOException("Invalid checkpoint.");
            bool legacyChecksum = saved.Checksum != DocumentChecksum(json);
            // Earlier v2 saves hashed typed dates, whose '+' escapes differ from JsonNode strings.
            // Accept that exact old representation once, then persist the canonical document hash.
            if (legacyChecksum && (saved.Version != CheckpointVersion || saved.Checksum != LegacyChecksum(saved)))
                throw new IOException("Invalid checkpoint.");
            bool migrate = saved.Version == 1;
            foreach (var file in saved.Files)
            {
                if (!LocalPath(file.Path) || !ValidId(file.Owner) || file.Offset < 0) throw new IOException("Invalid cursor.");
                file.Complete = false;
                if (migrate)
                {
                    // Keep the old response ledger; only cursors replay to recover turn metadata.
                    file.Offset = 0; file.Anchor = ""; file.Profiles.Clear(); file.Turns.Clear();
                    file.CurrentTurn = null;
                }
                _files[file.Path] = file;
            }
            foreach (var thread in saved.Threads)
            {
                if (!ValidId(thread.Id) || thread.Responses.Any(r => !ValidId(r.Key) || r.Key != r.Value.ResponseId))
                    throw new IOException("Invalid response identity.");
                _threads[thread.Id] = thread;
                _responseRevision++;
            }
            if (migrate || legacyChecksum) _dirty = true;
        }
        catch (Exception ex) when (ReadError(ex) || ex is NotSupportedException)
        { _files.Clear(); _threads.Clear(); _checkpointError = "费用缓存不可用，正从日志重新计算"; }
    }

    private void Save(bool force)
    {
        if (!_dirty || _checkpointPath == null || !force && DateTimeOffset.UtcNow - _lastSaved < TimeSpan.FromSeconds(30)) return;
        try
        {
            var saved = new Checkpoint { HomeHash = _homeHash, PricingVersion = ApiPricingCatalog.Version,
                Files = _files.Values.Where(f => f.Owner != null).ToArray(), Threads = _threads.Values.ToArray() };
            saved.Checksum = Checksum(saved);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_checkpointPath))!);
            string temporary = _checkpointPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved)); File.Move(temporary, _checkpointPath, true);
            _lastSaved = DateTimeOffset.UtcNow; _dirty = false; _checkpointError = null;
        }
        catch (Exception ex) when (ReadError(ex)) { _checkpointError = "费用缓存未能保存；当前估算保留在内存"; }
    }

    private bool LocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        string full = Path.GetFullPath(path);
        return new[] { "sessions", "archived_sessions" }.Any(root => full.StartsWith(
            Path.Combine(_home, root) + Path.DirectorySeparatorChar, LogFileIdentity.PathComparison))
            && full.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
    }
    private static string DocumentChecksum(string json)
    {
        var document = JsonNode.Parse(json) as JsonObject ?? throw new IOException("Invalid checkpoint document.");
        document["Checksum"] = "";
        return UsageLedgerCheckpoint.Hash(JsonSerializer.Serialize(document));
    }
    private static DateTimeOffset? Earlier(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first < second ? first : second;
    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    private static string Checksum(Checkpoint saved) => DocumentChecksum(JsonSerializer.Serialize(saved));

    private static string LegacyChecksum(Checkpoint saved)
    {
        string before = saved.Checksum; saved.Checksum = "";
        string hash = UsageLedgerCheckpoint.Hash(JsonSerializer.Serialize(saved));
        saved.Checksum = before; return hash;
    }
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    private static string Anchor(FileStream stream, long offset)
    {
        if (offset == 0) return "";
        long position = stream.Position;
        try
        {
            int size = (int)Math.Min(256, offset); stream.Position = offset - size;
            byte[] bytes = new byte[size]; stream.ReadExactly(bytes);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { stream.Position = position; }
    }
    private static bool ReadError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException
        or ArgumentException or InvalidOperationException or System.Security.SecurityException;
    private static bool Conflicts(string? first, string? second) => first != null && second != null && first != second;
    private static bool ValidId(string? id) => UsageLedgerCheckpoint.IsId(id);
    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
        && property.GetString() is { Length: > 0 and <= 256 } text ? text : null;
    private static DateTimeOffset? Time(JsonElement value) => DateTimeOffset.TryParse(Text(value, "timestamp"), out var at) ? at : null;
    private static long Total(TokenCounts counts) => counts.TotalTokens ?? SaturatingAdd(counts.InputTokens ?? 0, counts.OutputTokens ?? 0);
    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    public void Dispose()
    {
        if (_disposed) return;
        _gate.Wait();
        try { Save(true); _disposed = true; }
        finally { _gate.Release(); }
    }

    private sealed record Meta(string? Parent, bool IsSubagent, bool Conflict, FileState[] Files);
    private sealed class PendingLine(long offset)
    {
        public long ReadOffset = offset;
        public MemoryStream Bytes { get; } = new();
        public bool Oversized;
    }
    private sealed record Profile(string? Model, string? Tier, string? Provider);
    private sealed class FileState
    {
        public string Path { get; set; } = "";
        public string? Owner { get; set; }
        public string? SessionId { get; set; }
        public string? Title { get; set; }
        public bool Archived { get; set; }
        public Dictionary<string, LogTurn> Turns { get; set; } = new(StringComparer.Ordinal);
        public string? CurrentTurn { get; set; }
        [JsonIgnore] public DateTimeOffset LastWriteAt { get; set; }
        [JsonIgnore] public long ObservedLength { get; set; }
        [JsonIgnore] public DateTimeOffset HeaderCheckedAt { get; set; }
        [JsonIgnore] public long HeaderCheckedLength { get; set; } = -1;
        public string? Provider { get; set; }
        public string? Parent { get; set; }
        public bool IsSubagent { get; set; }
        public bool Excluded { get; set; }
        public string Identity { get; set; } = "";
        public long Offset { get; set; }
        public string Anchor { get; set; } = "";
        public Dictionary<string, Profile> Profiles { get; set; } = new(StringComparer.Ordinal);
        public TokenCounts? Expected { get; set; }
        public DateTimeOffset? ExpectedAt { get; set; }
        public bool Gap { get; set; }
        public bool SawActivity { get; set; }
        public bool HasHistoryBase { get; set; }
        public bool IdentityConflict { get; set; }
        public bool Discontinuous { get; set; }
        public bool Missing { get; set; }
        public bool Superseded { get; set; }
        public bool Complete { get; set; }
        public string? Error { get; set; }
    }
    private sealed class LogTurn
    {
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public DateTimeOffset? EvidenceAt { get; set; }
        public ActivityState State { get; set; } = ActivityState.Unconfirmed;
        public bool Confirmed { get; set; }
    }
    private sealed class ThreadState
    {
        public string Id { get; set; } = "";
        public Dictionary<string, ResponseRecord> Responses { get; set; } = new(StringComparer.Ordinal);
        public bool Conflict { get; set; }
    }
    private sealed class ResponseRecord
    {
        public string ResponseId { get; set; } = "";
        public string? TurnId { get; set; }
        [JsonIgnore] public TokenCostResult? CachedPrice { get; set; }
        public DateTimeOffset? At { get; set; }
        public TokenCounts Counts { get; set; } = new(null, null, null, null, null, null);
        public string? Model { get; set; }
        public string? Tier { get; set; }
        public string? Provider { get; set; }
    }
    private sealed class Checkpoint
    {
        public int Version { get; set; } = CheckpointVersion;
        public string HomeHash { get; set; } = "";
        public string PricingVersion { get; set; } = "";
        public FileState[] Files { get; set; } = [];
        public ThreadState[] Threads { get; set; } = [];
        public string Checksum { get; set; } = "";
    }
}
