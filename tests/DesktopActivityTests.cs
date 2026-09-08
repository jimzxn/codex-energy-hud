using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHud.Core;

public static class DesktopActivityTests
{
    private const string ThreadId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset Origin = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
    private static TaskActivity Local() => new(ThreadId, "fixture", ActivityState.Unconfirmed, Origin, Origin.AddSeconds(-10), "local")
        { TurnId = "turn-1" };
    private static JsonElement Element(string json) => JsonSerializer.SerializeToElement(JsonNode.Parse(json));
    private static JsonElement Snapshot(string flags = "[]", string requests = "[]", long revision = 1, bool canonical = false,
        string status = "inProgress", string runtime = "active", string duration = "null")
    {
        var turn = new { turnId = "turn-1", status, turnStartedAtMs = Origin.AddSeconds(-10).ToUnixTimeMilliseconds(),
            durationMs = JsonNode.Parse(duration), items = new[] { new { type = "agentMessage", text = "private_reply" } },
            @params = new { input = "private_prompt" } };
        object? history = canonical ? new { kind = "canonical", history = new { entitiesByKey =
            new Dictionary<string, object> { ["turn:turn-1"] = turn } } } : null;
        return JsonSerializer.SerializeToElement(new { type = "snapshot", revision, conversationState = new {
            id = ThreadId, threadRuntimeStatus = new { type = runtime, activeFlags = JsonNode.Parse(flags) },
            resumeState = "resumed", title = "private_title", turns = canonical ? Array.Empty<object>() : new object[] { turn },
            turnHistory = history, requests = JsonNode.Parse(requests) } });
    }
    private static string Approval(string id = "request-1", string turn = "turn-1", string method = "item/commandExecution/requestApproval") =>
        JsonSerializer.Serialize(new[] { new { id, method, @params = new { threadId = ThreadId, turnId = turn,
            itemId = "item-1", command = "private_command", reason = "private_reason" } } });
    private static JsonElement Patch(string patches, long basis = 1, long revision = 2) =>
        JsonSerializer.SerializeToElement(new { type = "patches", baseRevision = basis, revision, patches = JsonNode.Parse(patches) });
    public static int Run()
    {
        int failures = 0, checks = 0;
        void Check(bool pass, string name)
        { checks++; if (!pass) { failures++; Console.Error.WriteLine("FAIL desktop activity: " + name); } }
        var state = new DesktopTaskProjection(ThreadId);
        Check(state.Accept(Snapshot(requests: Approval(), canonical: true), "owner"), "canonical snapshot accepted");
        var waiting = state.Enrich(Local(), Origin);
        Check(waiting.State == ActivityState.AwaitingApproval && waiting.TurnId == "turn-1"
            && waiting.AttentionId == "\"request-1\"", "explicit pending request associated to current task and turn");
        Check(!state.RetainedJson.Contains("private_", StringComparison.Ordinal), "prompt command reason title and item contents discarded");
        Check(state.Enrich(Local(), Origin.AddMinutes(10)).State == ActivityState.AwaitingApproval,
            "healthy subscribed snapshot remains pending without inactivity guessing");
        Check(state.Accept(Patch("[{\"op\":\"remove\",\"path\":[\"requests\",0]}]"), "owner")
            && state.Enrich(Local(), Origin).State == ActivityState.ExecutionEvidence, "request removal resolves waiting");
        Check(state.Accept(Patch("[{\"op\":\"add\",\"path\":[\"requests\",0],\"value\":" + Approval("request-2")[1..^1] + "}]", 2, 3), "owner")
            && state.Enrich(Local(), Origin).AttentionId == "\"request-2\"", "new request preserves distinct reminder identity");
        Check(!state.Accept(Patch("[]", 1, 4), "owner"), "revision gap requests new snapshot");
        Check(!state.Accept(Patch("[]", 3, 4), "another-owner"), "patch cannot switch owner");
        Check(!state.Accept(Element("{\"type\":\"newProtocol\",\"revision\":1}"), "owner"), "unknown stream kind rejected");
        Check(!new DesktopTaskProjection("different").Accept(Snapshot(), "owner"), "snapshot identity must match task");
        state = new(ThreadId);
        Check(!state.Accept(Patch("[]"), "owner"), "patch without baseline rejected");
        Check(state.Accept(Snapshot(flags: "[\"waitingOnApproval\"]"), "owner")
            && state.Enrich(Local(), Origin).State == ActivityState.AwaitingApproval, "explicit approval runtime flag supported");
        Check(state.Enrich(Local(), Origin).AttentionId is null, "flag-only waits use transition episodes rather than a permanent turn key");
        state.Accept(Snapshot(flags: "[\"waitingOnUserInput\"]"), "owner");
        Check(state.Enrich(Local(), Origin).State == ActivityState.AwaitingInput, "explicit input runtime flag supported");
        state.Accept(Snapshot(requests: Approval(method: "item/tool/requestUserInput")), "owner");
        Check(state.Enrich(Local(), Origin).State == ActivityState.AwaitingInput, "input request supported");
        state.Accept(Snapshot(requests: Approval(method: "item/permissions/requestApproval")), "owner");
        Check(state.Enrich(Local(), Origin).State == ActivityState.AwaitingApproval, "permission request supported");
        state.Accept(Snapshot(requests: Approval(turn: "older-turn")), "owner");
        Check(state.Enrich(Local(), Origin).State == ActivityState.ExecutionEvidence, "historical turn request cannot turn task orange");
        state.Accept(Snapshot(requests: Approval(method: "item/tool/call")), "owner");
        Check(state.Enrich(Local(), Origin).State == ActivityState.ExecutionEvidence, "ordinary tool request is not approval");
        state.Accept(Snapshot(runtime: "notLoaded"), "owner");
        Check(state.Enrich(Local(), Origin).Source == "local", "notLoaded preserves local inference");
        state.Accept(Snapshot(runtime: "idle", status: "completed", duration: "10000", canonical: true), "owner");
        var completed = state.Enrich(Local(), Origin);
        Check(completed.State == ActivityState.Completed && completed.EndedAt == Origin,
            "completion uses explicit start plus duration endpoint");
        state.Accept(Snapshot(runtime: "idle", status: "completed"), "owner");
        Check(state.Enrich(Local(), Origin).EndedAt is null, "missing completion time never replaced by receive time");
        state.Accept(Snapshot(runtime: "idle", status: "interrupted", duration: "10000"), "owner");
        Check(state.Enrich(Local(), Origin).State == ActivityState.Interrupted, "interruption kept distinct");
        state.Accept(Snapshot(), "owner");
        Check(state.Accept(Patch("[{\"op\":\"replace\",\"path\":[\"turns\",0,\"items\",0,\"text\"],\"value\":\"private_new_reply\"}]"), "owner")
            && !state.RetainedJson.Contains("private_"), "content patches advance revision without retention");
        Check(state.Accept(Patch("[{\"op\":\"replace\",\"path\":[\"threadRuntimeStatus\",\"activeFlags\"],\"value\":[\"waitingOnApproval\"]}]", 2, 3), "owner")
            && state.Enrich(Local(), Origin).State == ActivityState.AwaitingApproval, "next metadata patch applies after discarded content patch");
        Check(!state.Accept(Patch("[{\"op\":\"remove\",\"path\":[\"requests\",99]}]", 3, 4), "owner"), "bad relevant patch invalidates snapshot");
        Check(state.Enrich(Local(), Origin).Source == "local", "invalidated projection cannot preserve approval");
        state = new(ThreadId);
        state.Accept(Snapshot(requests: Approval()), "owner");
        var newerLocal = Local() with { TurnId = "turn-2", StartedAt = Origin, State = ActivityState.ExecutionEvidence };
        Check(ReferenceEquals(state.Enrich(newerLocal, Origin), newerLocal), "new local turn cannot regress to older desktop pending approval");
        var endedLocal = Local() with { State = ActivityState.Completed, EndedAt = Origin };
        Check(ReferenceEquals(state.Enrich(endedLocal, Origin), endedLocal), "explicit same-turn local completion outranks delayed active snapshot");
        var interruptedLocal = Local() with { State = ActivityState.Interrupted, EndedAt = Origin };
        Check(ReferenceEquals(state.Enrich(interruptedLocal, Origin), interruptedLocal), "explicit same-turn interruption outranks delayed active snapshot");
        var currentTokens = new TokenUsageSample(new TokenCounts(60, 0, 0, 40, 0, 100), Origin, SampleHealth.Fresh);
        var threadTokens = new TokenUsageSample(new TokenCounts(600, 0, 0, 400, 0, 1000), Origin, SampleHealth.Fresh);
        var tokenLocal = Local() with { TokenUsage = new TaskTokenUsage("turn-1", currentTokens, threadTokens) };
        state = new(ThreadId);
        state.Accept(Snapshot(), "owner");
        Check(ReferenceEquals(state.Enrich(tokenLocal, Origin).TokenUsage, tokenLocal.TokenUsage),
            "same explicit turn preserves the complete token object");
        state.Accept(Patch("[{\"op\":\"replace\",\"path\":[\"turns\",0,\"turnId\"],\"value\":\"turn-2\"},"
            + "{\"op\":\"replace\",\"path\":[\"turns\",0,\"turnStartedAtMs\"],\"value\":"
            + Origin.ToUnixTimeMilliseconds() + "}]"), "owner");
        var desktopAhead = state.Enrich(tokenLocal, Origin);
        Check(desktopAhead.TurnId == "turn-2" && desktopAhead.TokenUsage.TurnId == "turn-2"
            && desktopAhead.TokenUsage.CurrentTurn.Counts is null && desktopAhead.TokenUsage.CurrentTurn.ObservedAt is null
            && desktopAhead.TokenUsage.CurrentTurn.Health == SampleHealth.Unavailable
            && desktopAhead.TokenUsage.CurrentTurn.Detail == "本轮尚无 Token 记录",
            "desktop-first new turn cannot inherit the previous turn token counter");
        Check(ReferenceEquals(desktopAhead.TokenUsage.Thread, threadTokens), "new-turn reset preserves thread cumulative tokens");
        var matchingAhead = tokenLocal with { TokenUsage = tokenLocal.TokenUsage with { TurnId = "turn-2" } };
        Check(ReferenceEquals(state.Enrich(matchingAhead, Origin).TokenUsage, matchingAhead.TokenUsage),
            "token record already matching desktop turn remains unchanged");
        var unidentifiedTokens = tokenLocal with { TokenUsage = tokenLocal.TokenUsage with { TurnId = null } };
        Check(state.Enrich(unidentifiedTokens, Origin).TokenUsage.CurrentTurn.Counts is null,
            "unidentified per-turn token record cannot be assigned to a known desktop turn");
        var unidentifiedSnapshot = JsonNode.Parse(Snapshot().GetRawText())!;
        unidentifiedSnapshot["conversationState"]!["turns"] = new JsonArray();
        state.Accept(JsonSerializer.SerializeToElement(unidentifiedSnapshot), "owner");
        var noDesktopTurn = state.Enrich(tokenLocal, Origin);
        Check(noDesktopTurn.TokenUsage.CurrentTurn.Counts is null && ReferenceEquals(noDesktopTurn.TokenUsage.Thread, threadTokens),
            "desktop without turn identity cannot confirm a local per-turn token counter");
        var noTurn = state.Enrich(tokenLocal with { TurnId = null }, Origin);
        Check(noTurn.TokenUsage.TurnId is null && noTurn.TokenUsage.CurrentTurn.Counts is null
            && noTurn.TokenUsage.CurrentTurn.Detail == "本轮身份未确认，Token 暂不可归属",
            "missing merged turn identity retains unknown token attribution");
        if (OperatingSystem.IsWindows())
        {
            try { RunTransportAsync(Check).GetAwaiter().GetResult(); RunHandshakeRecoveryAsync(Check).GetAwaiter().GetResult(); }
            catch (Exception e) { Check(false, "transport fixture: " + e); }
        }
        Console.WriteLine($"Desktop activity: {checks} checks, {failures} failure(s)");
        return failures;
    }

    private static async Task RunTransportAsync(Action<bool, string> check)
    {
        string pipeName = "codex-hud-test-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var observer = new DesktopActivityProvider(pipeName);
        ActivitySnapshot local = new(Origin, true, [Local()], SampleHealth.Partial);
        await observer.EnrichAsync(local, timeout.Token);
        await server.WaitForConnectionAsync(timeout.Token);
        async Task<JsonElement> Read(string phase)
        {
            try
            {
                byte[] header = new byte[4]; await server.ReadExactlyAsync(header, timeout.Token);
                byte[] body = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header)];
                await server.ReadExactlyAsync(body, timeout.Token);
                return Element(Encoding.UTF8.GetString(body));
            }
            catch (OperationCanceledException) { throw new TimeoutException("desktop fixture read: " + phase); }
        }
        async Task Write(object value)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(value), header = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
            await server.WriteAsync(header.AsMemory(0, 2), timeout.Token);
            await server.WriteAsync(header.AsMemory(2, 2), timeout.Token);
            await server.WriteAsync(body, timeout.Token);
        }
        var initialize = await Read("initialize");
        check(DesktopActivityProvider.Text(initialize, "method") == "initialize", "wire only starts with initialize");
        await Write(new { type = "response", method = "initialize", requestId = DesktopActivityProvider.Text(initialize, "requestId"),
            resultType = "success", result = new { clientId = "observer" } });
        var follow = await Read("subscribe");
        check(DesktopActivityProvider.Text(follow, "method") == "thread-stream-following-changed"
            && follow.GetProperty("params").GetProperty("following").GetBoolean(), "only announces passive following");
        await Write(new { type = "broadcast", method = "thread-stream-state-changed", version = 11, sourceClientId = "owner",
            @params = new { conversationId = ThreadId, hostId = "local", change = Snapshot(requests: Approval()) } });
        ActivitySnapshot observed = local;
        for (int i = 0; i < 50; i++)
        {
            observed = await observer.EnrichAsync(local, timeout.Token);
            if (observed.LiveTaskCount > 0) break;
            await Task.Delay(20, timeout.Token);
        }
        check(observed.Tasks[0].State == ActivityState.AwaitingApproval && observed.LiveTaskCount == 1,
            "fragmented frames produce one live pending task");
        await Write(new { type = "client-discovery-request", requestId = "approval-operation", request = new { method = "thread-follower-command-approval-decision" } });
        var refusal = await Read("refuse discovery");
        check(!refusal.GetProperty("response").GetProperty("canHandle").GetBoolean(), "observer refuses approval-operation discovery");
        await Write(new { type = "broadcast", method = "client-status-changed", version = 0, sourceClientId = "owner",
            @params = new { clientId = "owner", status = "disconnected" } });
        for (int i = 0; i < 50; i++)
        {
            observed = await observer.EnrichAsync(local, timeout.Token);
            if (observed.LiveTaskCount == 0) break;
            await Task.Delay(20, timeout.Token);
        }
        check(observed.LiveTaskCount == 0 && observed.Tasks[0].State == ActivityState.Unconfirmed,
            "owner disconnect revokes waiting even while router stays connected");
        var unfollowRead = Read("unsubscribe");
        observed = await observer.EnrichAsync(local with { AppPresent = false }, timeout.Token);
        check(observed.LiveTaskCount == 0 && observed.LiveHealth == SampleHealth.Unavailable, "application exit revokes realtime coverage");
        var unfollow = await unfollowRead;
        check(DesktopActivityProvider.Text(unfollow, "method") == "thread-stream-following-changed"
            && !unfollow.GetProperty("params").GetProperty("following").GetBoolean(), "application exit cancels subscription");
    }

    private static async Task RunHandshakeRecoveryAsync(Action<bool, string> check)
    {
        string pipeName = "codex-hud-handshake-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var observer = new DesktopActivityProvider(pipeName, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(30));
        ActivitySnapshot local = new(Origin, true, [Local()], SampleHealth.Partial);
        async Task<JsonElement> Read(NamedPipeServerStream server)
        {
            byte[] header = new byte[4]; await server.ReadExactlyAsync(header, timeout.Token);
            byte[] body = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header)];
            await server.ReadExactlyAsync(body, timeout.Token);
            return Element(Encoding.UTF8.GetString(body));
        }
        using (var silent = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            await observer.EnrichAsync(local, timeout.Token);
            await silent.WaitForConnectionAsync(timeout.Token);
            await Read(silent);
            await Task.Delay(180, timeout.Token);
            var expired = await observer.EnrichAsync(local, timeout.Token);
            check(expired.LiveHealth == SampleHealth.Unavailable && observer.HandshakeCount == 0,
                "unanswered initial handshake expires without a client ID");
        }
        using var recovered = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await Task.Delay(100, timeout.Token);
        await observer.EnrichAsync(local, timeout.Token);
        await recovered.WaitForConnectionAsync(timeout.Token);
        var initialize = await Read(recovered);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { type = "response", method = "initialize",
            requestId = DesktopActivityProvider.Text(initialize, "requestId"), resultType = "success", result = new { clientId = "recovered" } });
        byte[] frame = new byte[body.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await recovered.WriteAsync(frame, timeout.Token);
        var follow = await Read(recovered);
        check(observer.HandshakeCount == 1 && DesktopActivityProvider.Text(follow, "method") == "thread-stream-following-changed",
            "observer reconnects and resubscribes after failed initial handshake");
    }
    public static async Task<int> LiveAsync()
    {
        using var local = new ActivityProvider();
        await using var desktop = new DesktopActivityProvider();
        ActivitySnapshot? result = null;
        var identities = new HashSet<string>();
        int peakCoverage = 0;
        for (int i = 0; i < 40; i++)
        {
            result = await desktop.EnrichAsync(await local.ReadAsync(true));
            if (desktop.ConnectionIdentity is { } identity) identities.Add(identity);
            peakCoverage = Math.Max(peakCoverage, result.LiveTaskCount);
            await Task.Delay(1000);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { observedAt = DateTimeOffset.UtcNow, result!.LiveHealth,
            result.LiveTaskCount, taskCount = result.Tasks.Count,
            states = result.Tasks.GroupBy(t => t.State).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            metadataOnly = true, approvalDecisionsSent = 0, durationSeconds = 40,
            desktop.HandshakeCount, connectionIdentitiesObserved = identities.Count, peakCoverage }));
        return result.LiveTaskCount > 0 ? 0 : 1;
    }
}
