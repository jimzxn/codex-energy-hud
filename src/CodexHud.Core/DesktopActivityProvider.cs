using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexHud.Core;

/// <summary>Read-only observer of the desktop's local follower stream. Never owns a task or answers an approval.</summary>
public sealed class DesktopActivityProvider : IAsyncDisposable
{
    internal const int MaxFrameBytes = 16 * 1024 * 1024;
    internal const int SubscriptionLimit = 8;
    private readonly string _pipeName;
    public DesktopActivityProvider() : this("codex-ipc") { }
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _retryDelay;
    internal DesktopActivityProvider(string pipeName, TimeSpan? handshakeTimeout = null, TimeSpan? retryDelay = null)
    { _pipeName = pipeName; _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(15); _retryDelay = retryDelay ?? TimeSpan.FromSeconds(15); }
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Dictionary<string, DesktopTaskProjection> _states = new(StringComparer.Ordinal);
    private HashSet<string> _desired = new(StringComparer.Ordinal);
    private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);
    private NamedPipeClientStream? _pipe;
    private Task? _reader;
    private string? _clientId;
    private string? _initializeId;
    private DateTimeOffset _retryAt;
    private DateTimeOffset _lastProbe;
    private bool _disposed;
    internal int HandshakeCount { get; private set; }
    internal string? ConnectionIdentity { get { lock (_sync) return _clientId; } }

    public async Task<ActivitySnapshot> EnrichAsync(ActivitySnapshot local, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The named-pipe endpoint below is a Windows desktop protocol. macOS requires its
        // own evidenced socket transport; do not let .NET map this guessed name to a Unix pipe.
        if (!OperatingSystem.IsWindows())
            return local with
            {
                LiveTaskCount = 0,
                LiveHealth = SampleHealth.Unavailable,
                Message = $"macOS 桌面实时通道尚未接入；{local.Message}"
            };
        var now = DateTimeOffset.UtcNow;
        List<object> messages = [];
        lock (_sync)
        {
            if (_disposed) return local;
            _desired = local.AppPresent ? local.Tasks
                .Where(t => Guid.TryParse(t.Id, out _))
                .OrderBy(t => _states.TryGetValue(t.Id, out var known) && known.Enrich(t, now).State is ActivityState.AwaitingApproval or ActivityState.AwaitingInput
                    ? 0 : t.State is ActivityState.ExecutionEvidence or ActivityState.Unconfirmed ? 1 : 2)
                .ThenByDescending(t => t.EvidenceAt).Take(SubscriptionLimit)
                .Select(t => t.Id).ToHashSet(StringComparer.Ordinal) : new(StringComparer.Ordinal);
            foreach (var id in _states.Keys.Where(id => !_desired.Contains(id)).ToArray()) _states.Remove(id);
            if (!local.AppPresent)
            {
                _states.Clear();
                foreach (var id in _subscribed) messages.Add(Follow(id, false));
                _subscribed.Clear();
            }
            else if (_reader is null || _reader.IsCompleted)
            {
                if (now >= _retryAt) _reader = Task.Run(ConnectionLoopAsync);
            }
            if (_clientId is not null)
            {
                foreach (var id in _subscribed.Except(_desired).ToArray())
                { messages.Add(Follow(id, false)); _subscribed.Remove(id); }
                foreach (var id in _desired.Except(_subscribed).ToArray())
                { messages.Add(Follow(id, true)); _subscribed.Add(id); }
                // A router round trip confirms the connection without repeatedly transferring task history.
                if (_initializeId is null && now - _lastProbe >= TimeSpan.FromSeconds(30))
                { messages.Add(Initialize()); _lastProbe = now; }
            }
            // This also covers the first handshake, before a client ID has been assigned.
            if (_initializeId is not null && now - _lastProbe > _handshakeTimeout) _pipe?.Dispose();
        }
        foreach (var message in messages) await SendAsync(message, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            bool connected = local.AppPresent && _clientId is not null && _pipe?.IsConnected == true;
            int count = 0;
            var tasks = local.Tasks.Select(task =>
            {
                if (!connected || !_states.TryGetValue(task.Id, out var state)) return task;
                var enriched = state.Enrich(task, now);
                if (!ReferenceEquals(enriched, task)) count++;
                return enriched;
            }).OrderBy(t => t.State switch { ActivityState.AwaitingApproval => 0, ActivityState.AwaitingInput => 1,
                ActivityState.ExecutionEvidence => 2, ActivityState.Unconfirmed => 3, _ => 4 })
                .ThenByDescending(t => t.EvidenceAt).ToArray();
            return local with
            {
                ObservedAt = now,
                Tasks = tasks,
                LiveTaskCount = count,
                LiveHealth = connected ? (count > 0 ? SampleHealth.Partial : SampleHealth.Loading) : SampleHealth.Unavailable,
                Message = connected ? $"桌面实时覆盖 {count}/{local.Tasks.Count}；{local.Message}" : local.Message
            };
        }
    }

    private object Initialize()
    {
        _initializeId = Guid.NewGuid().ToString();
        return new { type = "request", requestId = _initializeId, sourceClientId = _clientId ?? "initializing-client",
            version = 0, method = "initialize", @params = new { clientType = "codex-hud-observer" } };
    }
    private object Follow(string id, bool following) => new { type = "broadcast", method = "thread-stream-following-changed",
        version = 1, sourceClientId = _clientId, @params = new { conversationId = id, hostId = "local", following } };

    private async Task ConnectionLoopAsync()
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            }
            object initialize;
            lock (_sync)
            {
                if (_disposed) return;
                _pipe = pipe;
                initialize = Initialize();
                _lastProbe = DateTimeOffset.UtcNow;
            }
            await SendAsync(initialize, _stop.Token).ConfigureAwait(false);
            byte[] header = new byte[4];
            while (!_stop.IsCancellationRequested)
            {
                await pipe.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
                int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length <= 0 || length > MaxFrameBytes) throw new IOException("Unsupported desktop frame size.");
                byte[] frame = new byte[length];
                await pipe.ReadExactlyAsync(frame, _stop.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 128 });
                await ReceiveAsync(document.RootElement).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ObjectDisposedException or ArgumentException) { }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pipe, pipe)) _pipe = null;
                _clientId = null;
                _initializeId = null;
                _states.Clear();
                _subscribed.Clear();
                _retryAt = DateTimeOffset.UtcNow + _retryDelay;
            }
            pipe?.Dispose();
        }
    }

    private async Task ReceiveAsync(JsonElement message)
    {
        var kind = Text(message, "type");
        List<object> replies = [];
        lock (_sync)
        {
            if (kind == "response" && Text(message, "method") == "initialize"
                && Text(message, "requestId") == _initializeId && Text(message, "resultType") == "success"
                && message.TryGetProperty("result", out var result) && Text(result, "clientId") is { Length: > 0 } id)
            {
                _clientId = id;
                HandshakeCount++;
                _initializeId = null;
                foreach (var task in _desired.Except(_subscribed)) { replies.Add(Follow(task, true)); _subscribed.Add(task); }
            }
            else if (kind == "client-discovery-request" && Text(message, "requestId") is { } requestId)
            {
                // Explicitly refuse every operation, including all follower approval and ownership methods.
                replies.Add(new { type = "client-discovery-response", requestId, response = new { canHandle = false } });
            }
            else if (kind == "broadcast" && message.TryGetProperty("params", out var parameters))
            {
                if (Text(message, "method") == "client-status-changed" && Text(parameters, "status") == "disconnected")
                {
                    string? owner = Text(parameters, "clientId");
                    foreach (var key in _states.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray())
                        _states.Remove(key);
                }
                else if (Text(message, "method") == "thread-stream-following-status-requested"
                    && Text(parameters, "hostId") == "local" && Text(parameters, "conversationId") is { } followingId
                    && _desired.Contains(followingId)) replies.Add(Follow(followingId, true));
                else if (Text(message, "method") == "thread-stream-state-changed"
                    && Text(parameters, "hostId") == "local" && Text(parameters, "conversationId") is { } taskId
                    && _desired.Contains(taskId))
                {
                    if (!message.TryGetProperty("version", out var version) || !version.TryGetInt32(out var v) || v != 11)
                    { _states.Remove(taskId); return; }
                    string? owner = Text(message, "sourceClientId");
                    if (owner is null || !parameters.TryGetProperty("change", out var change)) { _states.Remove(taskId); return; }
                    if (!_states.TryGetValue(taskId, out var state)) state = new(taskId);
                    if (state.Accept(change, owner)) _states[taskId] = state;
                    else
                    {
                        _states.Remove(taskId);
                        // Repeating a follower announcement requests a fresh snapshot without resuming the task.
                        replies.Add(Follow(taskId, true));
                    }
                }
            }
        }
        foreach (var reply in replies) await SendAsync(reply, _stop.Token).ConfigureAwait(false);
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        try
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(message);
            byte[] frame = new byte[data.Length + 4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, data.Length);
            data.CopyTo(frame, 4);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await _write.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                NamedPipeClientStream? pipe;
                lock (_sync) pipe = _pipe;
                if (pipe?.IsConnected == true) await pipe.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
            }
            finally { _write.Release(); }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException
            || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { lock (_sync) { _states.Clear(); _clientId = null; _pipe?.Dispose(); } }
    }

    internal static string? Text(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        Task? reader;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _states.Clear();
            reader = _reader;
        }
        _stop.Cancel();
        lock (_sync) _pipe?.Dispose();
        if (reader is not null) await reader.ConfigureAwait(false);
        _stop.Dispose();
        _write.Dispose();
    }
}

/// <summary>Retains only runtime/request/turn metadata, never prompts, commands, answers, or tool outputs.</summary>
internal sealed class DesktopTaskProjection(string taskId)
{
    private JsonNode? _metadata;
    private long _revision = -1;
    internal string? Owner { get; private set; }
    internal string RetainedJson => _metadata?.ToJsonString() ?? "null";
    private static readonly HashSet<string> TurnFields = ["turnId", "status", "turnStartedAtMs", "durationMs"];
    private static readonly HashSet<string> RequestFields = ["id", "method", "completed", "params"];
    private static readonly HashSet<string> ParameterFields = ["threadId", "turnId", "itemId", "tool"];

    internal bool Accept(JsonElement change, string owner)
    {
        try
        {
            if (!change.TryGetProperty("revision", out var revision) || !revision.TryGetInt64(out var next) || next < 0) return false;
            switch (DesktopActivityProvider.Text(change, "type"))
            {
                case "snapshot":
                    if (!change.TryGetProperty("conversationState", out var state)
                        || DesktopActivityProvider.Text(state, "id") != taskId) return false;
                    _metadata = Filter(JsonNode.Parse(state.GetRawText()), []);
                    if (_metadata is null) return false;
                    Owner = owner;
                    _revision = next;
                    return true;
                case "patches":
                    if (_metadata is null || Owner != owner || !change.TryGetProperty("baseRevision", out var basis)
                        || !basis.TryGetInt64(out var previous) || previous != _revision || next <= previous
                        || !change.TryGetProperty("patches", out var patches) || patches.ValueKind != JsonValueKind.Array
                        || patches.GetArrayLength() > 20000) return false;
                    foreach (var patch in patches.EnumerateArray()) Apply(patch);
                    _revision = next;
                    return true;
                default: return false;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException
            or FormatException or IndexOutOfRangeException or OverflowException)
        { _metadata = null; return false; }
    }

    internal TaskActivity Enrich(TaskActivity local, DateTimeOffset now)
    {
        if (_metadata is null) return local;
        string? runtime = String(_metadata["threadRuntimeStatus"]?["type"]);
        if (runtime is not ("active" or "idle")) return local;
        var turns = new List<JsonNode>();
        if (_metadata["turns"] is JsonArray direct) turns.AddRange(direct.Where(n => n is not null)!);
        if (_metadata["turnHistory"]?["history"]?["entitiesByKey"] is JsonObject entities)
            turns.AddRange(entities.Select(e => e.Value).Where(n => n is not null)!);
        var latest = turns.Where(n => String(n["turnId"]) is { Length: > 0 })
            .OrderByDescending(n => Number(n["turnStartedAtMs"]) ?? double.MinValue).FirstOrDefault();
        string? turnId = String(latest?["turnId"]) ?? local.TurnId;
        string? turnStatus = String(latest?["status"]);
        var started = Timestamp(latest?["turnStartedAtMs"]) ?? (turnId == local.TurnId ? local.StartedAt : null);
        // A fresh local turn/terminal record outranks a delayed desktop projection. Receiving an
        // old snapshot now must not give its old request a new lifecycle or completion timestamp.
        if (local.TurnId is { Length: > 0 } && turnId is { Length: > 0 } && turnId != local.TurnId
            && (started is null || local.StartedAt is null || started <= local.StartedAt)) return local;
        if (turnId == local.TurnId && local.State is ActivityState.Completed or ActivityState.Interrupted
            && runtime == "active") return local;
        var flags = (_metadata["threadRuntimeStatus"]?["activeFlags"] as JsonArray)?.Select(String).ToHashSet()
            ?? new HashSet<string?>();
        var requests = (_metadata["requests"] as JsonArray)?.Where(n => n is JsonObject && n["completed"]?.ToJsonString() != "true"
            && (String(n["params"]?["threadId"]) is not { } id || id == taskId)
            && (String(n["params"]?["turnId"]) is not { Length: > 0 } requestTurn || requestTurn == turnId)).ToArray() ?? [];
        JsonNode? waiting = requests.FirstOrDefault(n => String(n?["method"]) is "item/commandExecution/requestApproval"
            or "item/fileChange/requestApproval" or "item/permissions/requestApproval");
        ActivityState? status = waiting is null ? null : ActivityState.AwaitingApproval;
        if (waiting is null)
        {
            waiting = requests.FirstOrDefault(n => String(n?["method"]) is "item/tool/requestUserInput" or "item/tool/requestOptionPicker");
            if (waiting is not null) status = ActivityState.AwaitingInput;
        }
        // Runtime flags corroborate requests and also cover desktop-only input/approval forms.
        if (runtime == "active")
        {
            status ??= flags.Contains("waitingOnApproval") ? ActivityState.AwaitingApproval
                : flags.Contains("waitingOnUserInput") ? ActivityState.AwaitingInput : ActivityState.ExecutionEvidence;
        }
        DateTimeOffset? ended = null;
        if (status is null && turnStatus is "completed" or "interrupted")
        {
            status = turnStatus == "completed" ? ActivityState.Completed : ActivityState.Interrupted;
            if (started is { } origin && Number(latest?["durationMs"]) is { } duration && duration >= 0 && duration < TimeSpan.FromDays(365).TotalMilliseconds)
            {
                var endpoint = origin.AddMilliseconds(duration);
                if (endpoint <= now.AddSeconds(5)) ended = endpoint;
            }
            ended ??= turnId == local.TurnId ? local.EndedAt : null;
        }
        if (status is null) return local;
        string? attention = status is ActivityState.AwaitingApproval or ActivityState.AwaitingInput
            ? waiting?["id"]?.ToJsonString() : null;
        // Desktop state can announce a new turn before its local token record arrives. Only an
        // explicit matching turn identity may carry a per-turn counter into that live state.
        string? observedTurnId = String(latest?["turnId"]);
        var tokens = !string.IsNullOrWhiteSpace(observedTurnId)
            && string.Equals(local.TokenUsage.TurnId, observedTurnId, StringComparison.Ordinal)
            ? local.TokenUsage
            : new TaskTokenUsage(turnId,
                TokenUsageSample.Missing(string.IsNullOrWhiteSpace(observedTurnId)
                    ? "本轮身份未确认，Token 暂不可归属" : "本轮尚无 Token 记录"), local.TokenUsage.Thread);
        return local with { State = status.Value, EvidenceAt = now, StartedAt = started, EndedAt = ended,
            TurnId = turnId, AttentionId = attention, Source = "桌面实时状态 + 本地日志", TokenUsage = tokens };
    }

    private static string? String(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static double? Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
    private static DateTimeOffset? Timestamp(JsonNode? n)
    {
        var value = Number(n);
        if (value is >= 0 and <= 253402300799999) return DateTimeOffset.FromUnixTimeMilliseconds((long)value.Value);
        return null;
    }

    private static bool Relevant(IReadOnlyList<string> p)
    {
        if (p.Count == 0) return true;
        return p[0] switch
        {
            "id" or "resumeState" => p.Count == 1,
            "threadRuntimeStatus" => p.Count == 1 || p[1] is "type" or "activeFlags",
            "requests" => p.Count <= 2 || RequestFields.Contains(p[2]) && (p[2] != "params" || p.Count <= 3 || ParameterFields.Contains(p[3])),
            "turns" => p.Count <= 2 || TurnFields.Contains(p[2]),
            "turnHistory" => p.Count <= 1 || p[1] == "kind" || p[1] == "history" && (p.Count <= 2 || p[2] == "entitiesByKey" && (p.Count <= 4 || TurnFields.Contains(p[4]))),
            _ => false
        };
    }

    private static JsonNode? Filter(JsonNode? node, List<string> path)
    {
        if (!Relevant(path) || node is null) return null;
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var property in obj)
            {
                var childPath = new List<string>(path) { property.Key };
                if (Relevant(childPath)) result[property.Key] = Filter(property.Value, childPath);
            }
            return result;
        }
        if (node is JsonArray array)
        {
            if (array.Count > 20000) throw new JsonException("Metadata array limit exceeded.");
            var result = new JsonArray();
            for (int i = 0; i < array.Count; i++) result.Add(Filter(array[i], [.. path, i.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
            return result;
        }
        return node.DeepClone();
    }

    private void Apply(JsonElement patch)
    {
        if (!patch.TryGetProperty("path", out var pathNode) || pathNode.ValueKind != JsonValueKind.Array || pathNode.GetArrayLength() > 64)
            throw new JsonException("Unsupported patch path.");
        List<string> path = [];
        foreach (var segment in pathNode.EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.String) path.Add(segment.GetString()!);
            else if (segment.TryGetInt32(out int index) && index >= 0) path.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else throw new JsonException("Unsupported patch segment.");
        }
        if (!Relevant(path)) return;
        string? operation = DesktopActivityProvider.Text(patch, "op");
        if (operation is not ("add" or "replace" or "remove")) throw new JsonException("Unsupported patch operation.");
        JsonNode? value = null;
        if (operation != "remove")
        {
            if (!patch.TryGetProperty("value", out var raw)) throw new JsonException("Missing patch value.");
            value = Filter(JsonNode.Parse(raw.GetRawText()), path);
        }
        if (path.Count == 0) { _metadata = value; return; }
        JsonNode? parent = _metadata;
        for (int i = 0; i < path.Count - 1; i++) parent = parent is JsonArray array
            ? array[int.Parse(path[i], System.Globalization.CultureInfo.InvariantCulture)] : parent?[path[i]];
        string key = path[^1];
        if (parent is JsonArray list)
        {
            int index = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
            if (operation == "remove") list.RemoveAt(index);
            else if (operation == "add") list.Insert(index, value);
            else list[index] = value;
        }
        else if (parent is JsonObject obj)
        {
            if (operation == "remove") { if (!obj.Remove(key)) throw new JsonException("Missing patch target."); }
            else obj[key] = value;
        }
        else throw new JsonException("Missing patch parent.");
    }
}
