using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CodexHud.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace CodexHud.Core;

/// <summary>Cumulative, locally observed response usage since Epoch. It is not an account quota.</summary>
public sealed record UsageLedgerSnapshot(DateTimeOffset ObservedAt, string Epoch, TokenCounts Counts,
    SampleHealth Health, int ObservedThreads, string? ModelMix, string? Detail = null);

/// <summary>
/// Reads only bounded new JSONL ranges and numeric/identity metadata. Existing files begin at EOF;
/// archived tasks and independent agents are included without using the HUD's filtered task list.
/// </summary>
public sealed class UsageLedgerProvider
{
    private const int MaximumThreads = 4096;
    private const int ReadBudget = 4 * 1024 * 1024;
    private readonly string _home;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, UsageLedgerCursor> _cursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _historicalUnavailable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _profiles = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private string _epoch = Guid.NewGuid().ToString("N");
    private DateTimeOffset _started;
    private TokenCounts _counts = Zero;
    private bool _initialized;
    private bool _failed;
    private readonly Dictionary<string, DateTimeOffset> _ambiguousPools = new(StringComparer.Ordinal);
    private static readonly TokenCounts Zero = new(0, 0, 0, 0, 0, 0);

    public UsageLedgerProvider(string codexHome) : this(codexHome, () => DateTimeOffset.UtcNow) { }

    internal UsageLedgerProvider(string codexHome, Func<DateTimeOffset> clock)
    {
        _home = Path.GetFullPath(NormalizePath(codexHome));
        _clock = clock;
        _started = clock();
    }

    public async Task<UsageLedgerSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Read(cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private UsageLedgerSnapshot Read(CancellationToken cancellationToken)
    {
        var now = _clock();
        try
        {
            var database = Directory.EnumerateFiles(_home, "state_*.sqlite")
                .Select(path => (Path: path, Version: int.TryParse(Path.GetFileNameWithoutExtension(path)[6..], out int v) ? v : -1))
                .Where(item => item.Version >= 0).OrderByDescending(item => item.Version).FirstOrDefault().Path;
            if (database is null) throw new IOException("Local thread index unavailable.");
            using var state = new SqliteReader(database);
            var columns = state.Columns("threads");
            if (!columns.Contains("id") || !columns.Contains("rollout_path"))
                throw new IOException("Local thread index schema unavailable.");
            // Ordinary agents and archived tasks remain included. Explicit internal review roles
            // are outside this workload ledger; this classification says nothing about billing.
            string modelColumn = columns.Contains("model") ? "model" : "NULL AS model";
            string effortColumn = columns.Contains("reasoning_effort") ? "reasoning_effort" : "NULL AS reasoning_effort";
            string providerColumn = columns.Contains("model_provider") ? "model_provider" : "NULL AS model_provider";
            string updatedColumn = columns.Contains("updated_at_ms") ? "updated_at_ms AS ledger_updated" : columns.Contains("updated_at") ? "updated_at AS ledger_updated" : "NULL AS ledger_updated";
            bool hasThreadSource = columns.Contains("thread_source");
            string workloadFilter = hasThreadSource ? " WHERE thread_source IS NULL OR thread_source <> 'guardian_review'" : "";
            // Filter before LIMIT so internal-review history cannot displace user tasks.
            var rows = state.Query($"SELECT id, rollout_path, {modelColumn}, {effortColumn}, {providerColumn}, {updatedColumn} FROM threads{workloadFilter} ORDER BY id LIMIT {MaximumThreads + 1}");
            bool gap = _failed;
            bool complete = rows.Count <= MaximumThreads;
            int budget = ReadBudget;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var indexed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.Take(MaximumThreads))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string id = row.Text("id");
                if (id.Length is 0 or > 256) { complete = false; continue; }
                indexed.Add(id);
                string? path = LocalPath(row.Text("rollout_path"));
                string stamp = row.Text("rollout_path") + "|" + row.Text("ledger_updated");
                bool unavailable = path is null || !File.Exists(path);
                if (!_initialized && unavailable) { _historicalUnavailable[id] = stamp; continue; }
                if (_historicalUnavailable.TryGetValue(id, out string? previousStamp))
                {
                    if (previousStamp == stamp && unavailable) continue;
                    _historicalUnavailable.Remove(id);
                    gap = true;
                }
                if (path is null) { complete = false; continue; }
                seen.Add(id);
                if (!_cursors.TryGetValue(id, out var cursor))
                {
                    cursor = new UsageLedgerCursor(id);
                    _cursors.Add(id, cursor);
                    cursor.SetIndexProfile(row.Text("model"), row.Text("reasoning_effort"), row.Text("model_provider"));
                    var baseline = cursor.Initialize(path, _started, !_initialized, ref budget);
                    if (baseline == LedgerReadState.Gap) gap = true;
                    if (baseline == LedgerReadState.Incomplete) complete = false;
                }
                else cursor.SetIndexProfile(row.Text("model"), row.Text("reasoning_effort"), row.Text("model_provider"));
                var result = cursor.Read(path, _started, now, ref budget);
                if (result.State == LedgerReadState.Gap) gap = true;
                if (result.State == LedgerReadState.Incomplete) complete = false;
                // A valid same-pool profile change does not break cumulative token accounting.
                foreach (var increment in result.Increments)
                {
                    if (!MainPoolProfile(increment.Profile))
                    {
                        // The record has no limitId. A known separate model family cannot be
                        // silently combined with the main Codex account pool.
                        _ambiguousPools[id] = now.AddMinutes(10);
                        gap = true;
                        continue;
                    }
                    try { _counts = Add(_counts, increment.Counts); }
                    catch (OverflowException) { gap = true; }
                    _profiles[increment.Profile] = now;
                }
            }
            foreach (var id in _cursors.Keys.Where(id => !seen.Contains(id)).ToArray())
            {
                bool internalReview = hasThreadSource && state.Query(
                    "SELECT id FROM threads WHERE id=?1 AND thread_source='guardian_review'", id).Count > 0;
                if (internalReview) _ambiguousPools.Remove(id);
                else gap = true;
                _cursors.Remove(id);
            }
            foreach (var id in _historicalUnavailable.Keys.Where(id => !indexed.Contains(id)).ToArray()) _historicalUnavailable.Remove(id);
            foreach (var profile in _profiles.Where(item => now - item.Value > TimeSpan.FromMinutes(10)).Select(item => item.Key).ToArray())
                _profiles.Remove(profile);
            // Profile retention is metadata bookkeeping, not a gap in the numeric ledger.
            if (_profiles.Count > 32)
                foreach (var profile in _profiles.OrderBy(pair => pair.Value).Take(_profiles.Count - 32).Select(pair => pair.Key).ToArray())
                    _profiles.Remove(profile);
            _initialized = true;
            _failed = false;
            if (gap) ResetEpoch(now);
            foreach (var id in _ambiguousPools.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                _ambiguousPools.Remove(id);
            bool ambiguous = _ambiguousPools.Count > 0;
            return new(now, _epoch, _counts,
                gap || !complete || ambiguous ? SampleHealth.Partial : SampleHealth.Fresh,
                seen.Count, ProfileMix(), ambiguous ? "部分模型无法归属额度池" : gap ? "已重新建立采样基线"
                    : !complete ? "本地用量记录尚未补齐" : "本机响应增量；服务等级未记录时以 ? 表示");
        }
        catch (OperationCanceledException)
        {
            // A cancellation can interrupt a multi-file sweep after some cursors advanced.
            _failed = true;
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (!_failed) ResetEpoch(now);
            _failed = true;
            return new(now, _epoch, _counts, SampleHealth.Unavailable, _cursors.Count, ProfileMix(), "本地用量索引暂不可读");
        }
    }

    private void ResetEpoch(DateTimeOffset now)
    {
        _epoch = Guid.NewGuid().ToString("N");
        _counts = Zero;
        _started = now;
        _profiles.Clear();
    }

    private string? ProfileMix() => _profiles.Count == 0 ? null : string.Join(" | ", _profiles.Keys.Order(StringComparer.Ordinal));

    private string? LocalPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            string path = Path.GetFullPath(NormalizePath(raw));
            foreach (string name in new[] { "sessions", "archived_sessions" })
            {
                string root = Path.Combine(_home, name) + Path.DirectorySeparatorChar;
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return path;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return null;
    }

    internal static TokenCounts Add(TokenCounts left, TokenCounts right) => new(
        checked(left.InputTokens + right.InputTokens), checked(left.CachedInputTokens + right.CachedInputTokens),
        checked(left.CacheWriteInputTokens + right.CacheWriteInputTokens), checked(left.OutputTokens + right.OutputTokens),
        checked(left.ReasoningOutputTokens + right.ReasoningOutputTokens), checked(left.TotalTokens + right.TotalTokens));

    private static string NormalizePath(string path) => path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;

    private static readonly HashSet<string> MainPoolModels = new(StringComparer.Ordinal)
    {
        "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5",
        "gpt-5.4", "gpt-5.4-mini", "gpt-5.3-codex", "gpt-5.2", "gpt-5.2-codex",
        "gpt-5.1", "gpt-5.1-codex", "gpt-5.1-codex-max", "gpt-5.1-codex-mini", "gpt-5", "gpt-5-codex"
    };

    private static bool MainPoolProfile(string profile)
    {
        string model = profile.Split('/')[0]["model:".Length..];
        return MainPoolModels.Contains(model) && profile.EndsWith("/provider:openai", StringComparison.Ordinal);
    }
}

internal enum LedgerReadState { Complete, Incomplete, Gap }
internal sealed record LedgerIncrement(TokenCounts Counts, string Profile);
internal sealed record LedgerReadResult(LedgerReadState State, IReadOnlyList<LedgerIncrement> Increments, bool ModelChanged = false);

/// <summary>Does not keep JSON bodies between calls. Partial lines are reread from their start.</summary>
internal sealed class UsageLedgerCursor(string threadId)
{
    internal const int MaximumRead = 512 * 1024;
    private string? _identity;
    private string? _path;
    private long _offset;
    private byte[]? _anchor;
    private bool _initialized;
    private bool _skipLine;
    private bool _failed;
    private TokenCounts? _cumulative;
    private DateTimeOffset? _lastTokenTime;
    private string _profile = "model:?/effort:?/tier:?";
    private string _indexProfile = "model:?/effort:?/tier:?";
    private string _indexProvider = "?";
    private bool _hasUsage;
    private readonly Dictionary<string, string> _turnProfiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _responses = new(StringComparer.Ordinal);
    private readonly Queue<string> _responseOrder = new();

    public void SetIndexProfile(string model, string effort, string provider)
    {
        _indexProvider = ProfilePart(provider);
        _indexProfile = $"model:{ProfilePart(model)}/effort:{ProfilePart(effort)}/tier:?/provider:{_indexProvider}";
    }

    public LedgerReadState Initialize(string path, DateTimeOffset start, bool initialSweep, ref int budget)
    {
        try
        {
            using var stream = Open(path);
            _identity = Identity(stream.SafeFileHandle);
            _path = path;
            _offset = stream.Length;
            bool demonstrablyNew = !initialSweep && NewSession(stream, start, ref budget);
            if (demonstrablyNew) _offset = 0;
            else RestoreMetadata(stream, ref budget);
            _anchor = Anchor(stream, _offset);
            _initialized = true;
            // A discovered old log may contain consumption from a gap in index discovery.
            return initialSweep || demonstrablyNew ? LedgerReadState.Complete : LedgerReadState.Gap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _failed = true;
            return LedgerReadState.Incomplete;
        }
    }

    private void RestoreMetadata(FileStream stream, ref int budget)
    {
        int size = (int)Math.Min(Math.Min(stream.Length, 32 * 1024), Math.Max(0, budget));
        if (size == 0) return;
        var data = new byte[size];
        long start = stream.Length - size;
        stream.Position = start;
        int read = stream.Read(data);
        budget -= read;
        int lineStart = 0;
        bool skip = start > 0;
        for (int i = 0; i < read; i++)
        {
            if (data[i] != (byte)'\n') continue;
            if (skip) skip = false;
            else
            {
                try
                {
                    using var document = JsonDocument.Parse(data.AsMemory(lineStart, i - lineStart));
                    var root = document.RootElement;
                    if (root.TryGetProperty("payload", out var payload))
                    {
                        if (Text(root, "type") == "turn_context" && Text(payload, "turn_id") is { } turn)
                            StoreTurnProfile(turn, payload);
                        else if (Text(root, "type") == "token_usage_record" && Text(payload, "thread_id") == threadId
                            && Time(root) is { } at && TokenUsageTracker.ReadCounts(payload, "thread_token_usage") is { Health: SampleHealth.Fresh, Counts: { } counts })
                        {
                            _cumulative = counts;
                            _lastTokenTime = at;
                            if (Text(payload, "response_id") is { } response) Remember(response);
                        }
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
            }
            lineStart = i + 1;
        }
    }

    private bool NewSession(FileStream stream, DateTimeOffset start, ref int budget)
    {
        int size = (int)Math.Min(Math.Min(stream.Length, 32 * 1024), Math.Max(0, budget));
        if (size == 0) return false;
        var data = new byte[size];
        stream.Position = 0;
        int read = stream.Read(data);
        budget -= read;
        int newline = Array.IndexOf(data, (byte)'\n', 0, read);
        if (newline < 0) return false;
        try
        {
            using var json = JsonDocument.Parse(data.AsMemory(0, newline));
            var root = json.RootElement;
            return Text(root, "type") == "session_meta" && Time(root) is { } at && at >= start
                && root.TryGetProperty("payload", out var payload) && Text(payload, "id") == threadId;
        }
        catch (JsonException) { return false; }
    }

    public LedgerReadResult Read(string path, DateTimeOffset start, DateTimeOffset now, ref int budget)
    {
        var increments = new List<LedgerIncrement>();
        bool gap = false, modelChanged = false;
        try
        {
            if (!_initialized)
            {
                var initialized = Initialize(path, start, false, ref budget);
                if (!_initialized) return new(LedgerReadState.Incomplete, increments);
                gap = initialized != LedgerReadState.Complete || _failed;
            }
            using var stream = Open(path);
            string identity = Identity(stream.SafeFileHandle);
            long length = stream.Length;
            if (_identity != identity || !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)
                || length < _offset || _anchor is not null && !_anchor.AsSpan().SequenceEqual(Anchor(stream, _offset)))
            {
                _initialized = false;
                _cumulative = null;
                _lastTokenTime = null;
                _responses.Clear(); _responseOrder.Clear(); _turnProfiles.Clear();
                _profile = "model:?/effort:?/tier:?";
                _hasUsage = false;
                Initialize(path, start, true, ref budget);
                return new(LedgerReadState.Gap, increments);
            }
            gap |= _failed;
            _failed = false;
            if (length == _offset) return new(gap ? LedgerReadState.Gap : LedgerReadState.Complete, increments);
            if (budget <= 0) return new(LedgerReadState.Incomplete, increments);
            int size = (int)Math.Min(Math.Min(length - _offset, MaximumRead), budget);
            var data = new byte[size];
            stream.Position = _offset;
            int bytes = stream.Read(data);
            budget -= bytes;
            int lineStart = 0;
            for (int i = 0; i < bytes; i++)
            {
                if (data[i] != (byte)'\n') continue;
                if (_skipLine) _skipLine = false;
                else Observe(data.AsMemory(lineStart, i - lineStart), start, now, increments, ref gap, ref modelChanged);
                lineStart = i + 1;
            }
            _offset += lineStart;
            if (lineStart == 0 && bytes == MaximumRead)
            {
                _offset += bytes;
                _skipLine = true;
                _cumulative = null;
                gap = true;
            }
            _anchor = Anchor(stream, _offset);
            return new(gap ? LedgerReadState.Gap : _offset == length ? LedgerReadState.Complete : LedgerReadState.Incomplete,
                increments, modelChanged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _failed = true;
            return new(LedgerReadState.Incomplete, increments);
        }
    }

    private void Observe(ReadOnlyMemory<byte> line, DateTimeOffset start, DateTimeOffset now,
        List<LedgerIncrement> increments, ref bool gap, ref bool modelChanged)
    {
        if (line.IsEmpty) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            string? type = Text(root, "type");
            if (type is not ("turn_context" or "token_usage_record")) return;
            if (!root.TryGetProperty("payload", out var payload)) { gap = true; return; }
            if (type == "turn_context")
            {
                if (Text(payload, "turn_id") is not { } turn) return;
                StoreTurnProfile(turn, payload);
                return;
            }
            // No nested compaction record, inherited parent record, or anonymous notification counts.
            if (Text(payload, "thread_id") != threadId) return;
            if (Text(payload, "response_id") is not { } response || Text(payload, "turn_id") is not { } turnId
                || Time(root) is not { } at || at > now.AddSeconds(5)) { gap = true; return; }
            if (!Remember(response)) return;
            var usage = TokenUsageTracker.ReadCounts(payload, "usage");
            var cumulative = TokenUsageTracker.ReadCounts(payload, "thread_token_usage");
            if (usage.Health != SampleHealth.Fresh || cumulative.Health != SampleHealth.Fresh
                || usage.Counts is not { } current || cumulative.Counts is not { } after
                || !Within(current, after))
            {
                _cumulative = null;
                gap = true;
                return;
            }
            if (at < start)
            {
                // Startup values and old records reintroduced by a fork are only a baseline.
                if (_lastTokenTime is null || at >= _lastTokenTime) { _cumulative = after; _lastTokenTime = at; }
                return;
            }
            if (_lastTokenTime is { } last && at < last) { gap = true; return; }
            if (_cumulative is { } before)
            {
                if (after == before) return; // Old replay after response-ID cache eviction.
                if (!MatchesDelta(before, after, current))
                {
                    _cumulative = after;
                    _lastTokenTime = at;
                    gap = true;
                    return;
                }
            }
            _cumulative = after;
            _lastTokenTime = at;
            string profile = _turnProfiles.GetValueOrDefault(turnId, _indexProfile);
            if (_hasUsage && profile != _profile) modelChanged = true;
            _profile = profile;
            _hasUsage = true;
            if (current.TotalTokens > 0) increments.Add(new(current, profile));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            gap = true;
        }
    }

    private void StoreTurnProfile(string turn, JsonElement payload)
    {
        string profile = $"model:{ProfilePart(Text(payload, "model"))}/effort:{ProfilePart(Text(payload, "effort"))}/tier:{ProfilePart(Text(payload, "service_tier") ?? Text(payload, "speed"))}/provider:{ProfilePart(Text(payload, "model_provider") ?? _indexProvider)}";
        _turnProfiles[turn] = profile;
        if (_turnProfiles.Count > 32) _turnProfiles.Remove(_turnProfiles.Keys.First());
    }

    private static string ProfilePart(string? value) => value is { Length: > 0 and <= 256 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '?') ? value : "?";

    private static bool Within(TokenCounts usage, TokenCounts total) => usage.InputTokens <= total.InputTokens
        && usage.CachedInputTokens <= total.CachedInputTokens && usage.CacheWriteInputTokens <= total.CacheWriteInputTokens
        && usage.OutputTokens <= total.OutputTokens && usage.ReasoningOutputTokens <= total.ReasoningOutputTokens
        && usage.TotalTokens <= total.TotalTokens;

    private bool Remember(string response)
    {
        if (!_responses.Add(response)) return false;
        _responseOrder.Enqueue(response);
        if (_responseOrder.Count > 512) _responses.Remove(_responseOrder.Dequeue());
        return true;
    }

    private static bool MatchesDelta(TokenCounts before, TokenCounts after, TokenCounts usage) =>
        Delta(before.InputTokens, after.InputTokens, usage.InputTokens)
        && Delta(before.CachedInputTokens, after.CachedInputTokens, usage.CachedInputTokens)
        && Delta(before.CacheWriteInputTokens, after.CacheWriteInputTokens, usage.CacheWriteInputTokens)
        && Delta(before.OutputTokens, after.OutputTokens, usage.OutputTokens)
        && Delta(before.ReasoningOutputTokens, after.ReasoningOutputTokens, usage.ReasoningOutputTokens)
        && Delta(before.TotalTokens, after.TotalTokens, usage.TotalTokens);

    private static bool Delta(long? before, long? after, long? usage) =>
        before.HasValue && after.HasValue && usage.HasValue && after >= before && after - before == usage;

    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
        && property.GetString() is { Length: > 0 and <= 256 } result ? result : null;

    private static DateTimeOffset? Time(JsonElement root) => Text(root, "timestamp") is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);

    private static byte[] Anchor(FileStream stream, long offset)
    {
        Span<byte> data = stackalloc byte[(int)Math.Min(64, offset)];
        stream.Position = offset - data.Length;
        return SHA256.HashData(data[..stream.Read(data)]);
    }

    private static string Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Log identity unavailable.");
        return $"{info.VolumeSerialNumber}:{info.FileIndexHigh}:{info.FileIndexLow}:{info.CreationTime.dwHighDateTime}:{info.CreationTime.dwLowDateTime}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
