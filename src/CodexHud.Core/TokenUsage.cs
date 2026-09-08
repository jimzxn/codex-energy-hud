using System.Text.Json;

namespace CodexHud.Core;

/// <summary>Raw recorded tokens. Cached input is part of input; reasoning is part of output.</summary>
public sealed record TokenCounts(long? InputTokens, long? CachedInputTokens, long? CacheWriteInputTokens,
    long? OutputTokens, long? ReasoningOutputTokens, long? TotalTokens);

public sealed record TokenUsageSample(TokenCounts? Counts, DateTimeOffset? ObservedAt, SampleHealth Health,
    string? Detail = null)
{
    public static TokenUsageSample Missing(string detail) => new(null, null, SampleHealth.Unavailable, detail);
}

public sealed record TaskTokenUsage(string? TurnId, TokenUsageSample CurrentTurn, TokenUsageSample Thread)
{
    public const string Scope = "本地 Token 记录；仅任务自身，不含独立子代理；非额度百分比";
    public static TaskTokenUsage Missing { get; } = new(null,
        TokenUsageSample.Missing("本轮暂未读到 Token 记录"), TokenUsageSample.Missing("任务暂未读到 Token 记录"));
}

/// <summary>
/// Keeps bounded identity/numeric metadata only. Uses explicit cumulative counters, never sums
/// snapshots, token_count notifications, inherited messages, or cached/reasoning subsets.
/// </summary>
internal sealed class TokenUsageTracker(string? threadId)
{
    private const int RecentTurns = 16;
    private const int RecentResponses = 256;
    private readonly Dictionary<string, TokenUsageSample> _turns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _responses = new(StringComparer.Ordinal);
    private readonly Queue<string> _responseOrder = new();
    private TokenUsageSample _thread = TokenUsageSample.Missing("任务尚无 Token 记录");

    public void Reset()
    {
        _turns.Clear();
        _responses.Clear();
        _responseOrder.Clear();
        _thread = TokenUsageSample.Missing("任务尚无 Token 记录");
    }

    public void MarkGap()
    {
        static TokenUsageSample Mark(TokenUsageSample value) => value.Counts is null ? value
            : value with { Health = SampleHealth.Stale, Detail = "日志增量存在间隔；保留最近 Token 记录" };
        _thread = Mark(_thread);
        foreach (var key in _turns.Keys.ToArray()) _turns[key] = Mark(_turns[key]);
    }

    public void Observe(JsonElement payload, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(threadId) || payload.ValueKind != JsonValueKind.Object
            || Id(payload, "thread_id") != threadId || Id(payload, "turn_id") is not { } turn
            || Id(payload, "response_id") is not { } response) return;
        // Replayed records cannot increment counters or refresh their evidence timestamp.
        if (!_responses.Add(response)) return;
        _responseOrder.Enqueue(response);
        if (_responseOrder.Count > RecentResponses) _responses.Remove(_responseOrder.Dequeue());

        _turns.TryGetValue(turn, out var previousTurn);
        _turns[turn] = Update(previousTurn ?? TokenUsageSample.Missing("本轮尚无 Token 记录"),
            ReadCounts(payload, "turn_token_usage"), timestamp);
        _thread = Update(_thread, ReadCounts(payload, "thread_token_usage"), timestamp);
        if (_turns.Count > RecentTurns)
        {
            var oldest = _turns.Where(item => item.Key != turn)
                .MinBy(item => item.Value.ObservedAt ?? DateTimeOffset.MinValue).Key;
            _turns.Remove(oldest);
        }
    }

    public TaskTokenUsage Snapshot(string? turnId, DateTimeOffset now, bool sourceComplete, bool unfinished)
    {
        var turn = turnId is not null && _turns.TryGetValue(turnId, out var match)
            ? match : TokenUsageSample.Missing(turnId is null ? "本轮身份未确认，Token 暂不可归属" : "本轮尚无 Token 记录");
        return new(turnId, ForDisplay(turn, now, sourceComplete, unfinished),
            ForDisplay(_thread, now, sourceComplete, unfinished));
    }

    private static TokenUsageSample ForDisplay(TokenUsageSample sample, DateTimeOffset now,
        bool sourceComplete, bool unfinished)
    {
        if (sample.Counts is null) return sample;
        if (!sourceComplete) return sample with { Health = SampleHealth.Stale, Detail = "日志暂不可完整读取；保留最近 Token 记录" };
        if (unfinished && sample.ObservedAt is { } time && now - time > TimeSpan.FromSeconds(120))
            return sample with { Health = SampleHealth.Stale, Detail = "截至最近 Token 记录；超过 120 秒尚无新用量记录" };
        return sample;
    }

    private static TokenUsageSample Update(TokenUsageSample previous, TokenUsageSample candidate, DateTimeOffset timestamp)
    {
        if (previous.ObservedAt is { } recorded && timestamp < recorded) return previous;
        if (candidate.Counts is null) return previous.Counts is null ? candidate
            : previous with { Health = SampleHealth.Stale, Detail = "新的 Token 字段缺失或无效；保留最近有效记录" };
        if (previous.Counts is { } before && Regressed(before, candidate.Counts))
            return previous with { Health = SampleHealth.Stale, Detail = "累计 Token 出现回退；保留最近有效记录" };
        return candidate with { ObservedAt = timestamp };
    }

    private static bool Regressed(TokenCounts before, TokenCounts after) =>
        Lower(before.InputTokens, after.InputTokens) || Lower(before.OutputTokens, after.OutputTokens)
        || Lower(before.TotalTokens, after.TotalTokens) || Lower(before.CachedInputTokens, after.CachedInputTokens)
        || Lower(before.ReasoningOutputTokens, after.ReasoningOutputTokens)
        || Lower(before.CacheWriteInputTokens, after.CacheWriteInputTokens);
    private static bool Lower(long? before, long? after) => before.HasValue && after.HasValue && after.Value < before.Value;

    internal static TokenUsageSample ReadCounts(JsonElement payload, string property)
    {
        if (!payload.TryGetProperty(property, out var group) || group.ValueKind != JsonValueKind.Object)
            return TokenUsageSample.Missing("Token 字段缺失");
        bool invalid = false;
        long? Read(string name)
        {
            if (!group.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long count) && count >= 0) return count;
            invalid = true;
            return null;
        }
        var counts = new TokenCounts(Read("input_tokens"), Read("cached_input_tokens"), Read("cache_write_input_tokens"),
            Read("output_tokens"), Read("reasoning_output_tokens"), Read("total_tokens"));
        if (counts.InputTokens is { } input && counts.OutputTokens is { } output)
        {
            if (input > long.MaxValue - output || counts.TotalTokens is { } total && input + output != total) invalid = true;
        }
        if (counts.CachedInputTokens > counts.InputTokens || counts.CacheWriteInputTokens > counts.InputTokens
            || counts.ReasoningOutputTokens > counts.OutputTokens) invalid = true;
        if (invalid) return TokenUsageSample.Missing("Token 计数无效或溢出");
        if (counts.InputTokens is null && counts.OutputTokens is null && counts.TotalTokens is null)
            return TokenUsageSample.Missing("Token 计数缺失");
        bool complete = counts.InputTokens.HasValue && counts.CachedInputTokens.HasValue && counts.CacheWriteInputTokens.HasValue
            && counts.OutputTokens.HasValue && counts.ReasoningOutputTokens.HasValue && counts.TotalTokens.HasValue;
        return new(counts, null, complete ? SampleHealth.Fresh : SampleHealth.Partial,
            complete ? TaskTokenUsage.Scope : "部分 Token 字段缺失；" + TaskTokenUsage.Scope);
    }

    private static string? Id(JsonElement payload, string key) =>
        payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 and <= 256 } id ? id : null;
}
