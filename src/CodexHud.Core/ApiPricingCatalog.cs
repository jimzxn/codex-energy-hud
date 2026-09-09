namespace CodexHud.Core;

/// <summary>
/// A versioned public OpenAI API price snapshot. Each call prices one response using the
/// current catalog, including any historical response; it never prices cumulative counters.
/// Prices are USD per million tokens. Tool fees and regional uplifts are outside this estimate.
/// </summary>
public static class ApiPricingCatalog
{
    public const string Version = "openai-api-2026-09-08";
    public static DateOnly VerifiedOn { get; } = new(2026, 9, 8);
    public static IReadOnlyList<string> Sources { get; } = Array.AsReadOnly(new[]
    {
        "https://developers.openai.com/api/docs/pricing",
        "https://developers.openai.com/api/docs/guides/prompt-caching",
        "https://developers.openai.com/api/docs/guides/reasoning",
        "https://developers.openai.com/api/docs/models/gpt-6-astra",
        "https://developers.openai.com/api/docs/models/gpt-5.6-sol",
        "https://developers.openai.com/api/docs/models/gpt-5.6-terra",
        "https://developers.openai.com/api/docs/models/gpt-5.6-luna",
        "https://developers.openai.com/api/docs/models/gpt-5.5",
        "https://developers.openai.com/api/docs/models/gpt-5.4",
        "https://developers.openai.com/api/docs/models/gpt-5.4-mini",
        "https://developers.openai.com/api/docs/models/gpt-5.3-codex",
        "https://developers.openai.com/api/docs/guides/fast-mode",
        "https://developers.openai.com/api/docs/guides/latest-model?model=gpt-5.4",
        "https://openai.com/api-fast-mode/"
    });

    private const long LongContextThreshold = 272_000;
    private const decimal TokensPerMillion = 1_000_000m;
    private sealed record ModelPrice(decimal Input, decimal CachedInput, decimal Output,
        decimal FastMultiplier, bool HasCacheWriteRate = false, bool HasLongContextRate = false,
        bool FastSupportsLongContext = true, bool FastFallsBackToStandard = false,
        bool LongContextScopeUncertain = false);

    private static readonly ModelPrice Sol = new(4m, .4m, 20m, 2m, true, true);
    private static readonly ModelPrice Gpt55 = new(5m, .5m, 30m, 2.5m,
        HasLongContextRate: true, FastSupportsLongContext: false, LongContextScopeUncertain: true);
    private static readonly ModelPrice Gpt54 = new(2.5m, .25m, 15m, 2m,
        HasLongContextRate: true, FastSupportsLongContext: false, FastFallsBackToStandard: true);
    private static readonly IReadOnlyDictionary<string, ModelPrice> Models =
        new Dictionary<string, ModelPrice>(StringComparer.Ordinal)
        {
            ["gpt-6-astra"] = new(10m, 1m, 50m, 2m, true, true),
            ["gpt-5.6-sol"] = Sol,
            ["gpt-5.6"] = Sol,
            ["gpt-5.6-terra"] = new(2m, .2m, 12m, 2m, true, true),
            ["gpt-5.6-luna"] = new(.2m, .02m, 1.2m, 2m, true, true),
            ["gpt-5.5"] = Gpt55,
            ["gpt-5.5-2026-04-23"] = Gpt55,
            ["gpt-5.4"] = Gpt54,
            ["gpt-5.4-2026-03-05"] = Gpt54,
            ["gpt-5.4-mini"] = new(.75m, .075m, 4.5m, 2m, FastSupportsLongContext: false),
            ["gpt-5.3-codex"] = new(1.75m, .175m, 14m, 2m)
        };

    public static TokenCostResult Price(TokenCounts counts, string? model, string? serviceTier,
        string? provider = null)
    {
        if (provider is not null && !string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
            return Unpriced("服务商不是已核实的 OpenAI API");
        if (model is null || !Models.TryGetValue(model, out var rates))
            return Unpriced(model == "gpt-5.3-codex-spark"
                ? "GPT-5.3-Codex-Spark 尚无已公开的 API 数值价格" : "模型缺失或不在已核实的 API 价格表中");
        if (!Valid(counts)) return Unpriced("Token 计数缺失、无效或不一致");

        var notes = new List<string>();
        bool fast;
        switch (serviceTier)
        {
            case null:
                fast = false;
                notes.Add("未记录服务层级；按 Standard API 价格估算");
                break;
            case "default":
            case "standard":
                fast = false;
                break;
            case "priority":
            case "fast":
                fast = true;
                break;
            default:
                return Unpriced("服务层级不在已核实的 Standard / Fast 价格表中");
        }

        long input = counts.InputTokens!.Value;
        long cached = counts.CachedInputTokens!.Value;
        long output = counts.OutputTokens!.Value;
        bool aboveThreshold = input > LongContextThreshold;
        if (fast && aboveThreshold && !rates.FastSupportsLongContext)
        {
            if (!rates.FastFallsBackToStandard)
                return Unpriced("该模型的 Fast API 价格不支持超过 272K 输入的请求");
            fast = false;
            notes.Add("GPT-5.4 的 Fast 长上下文请求按官方规则回退至 Standard 价格");
        }

        bool longContext = aboveThreshold && rates.HasLongContextRate;
        decimal tierMultiplier = fast ? rates.FastMultiplier : 1m;
        decimal inputRate = rates.Input * tierMultiplier * (longContext ? 2m : 1m);
        decimal cachedRate = rates.CachedInput * tierMultiplier * (longContext ? 2m : 1m);
        decimal outputRate = rates.Output * tierMultiplier * (longContext ? 1.5m : 1m);
        decimal baseCost = ((input - cached) * inputRate + cached * cachedRate + output * outputRate)
            / TokensPerMillion;

        decimal minimum = baseCost;
        decimal maximum = baseCost;
        if (rates.HasCacheWriteRate)
        {
            // Base cost already includes uncached input once. Cache writes replace that rate
            // with 1.25x; only the 0.25x premium remains to add, never another 1.25x charge.
            if (counts.CacheWriteInputTokens is { } written)
                minimum = maximum = baseCost + written * inputRate * .25m / TokensPerMillion;
            else if (input > cached)
            {
                maximum = baseCost + (input - cached) * inputRate * .25m / TokensPerMillion;
                notes.Add("缓存写入 Token 未记录；范围按零写入至全部未缓存输入写入计算");
            }
        }

        bool uncertainScope = longContext && rates.LongContextScopeUncertain;
        if (uncertainScope)
            notes.Add("GPT-5.5 长上下文按单次请求估算，官方计费范围表述尚有歧义");
        return new(minimum, maximum, notes.ToArray()) { HasUncertainPricingScope = uncertainScope };
    }

    private static TokenCostResult Unpriced(string reason) => new(null, null, [], reason);

    private static bool Valid(TokenCounts? counts)
    {
        if (counts?.InputTokens is not { } input || counts.CachedInputTokens is not { } cached
            || counts.OutputTokens is not { } output) return false;
        if (input < 0 || cached < 0 || output < 0 || counts.CacheWriteInputTokens < 0
            || counts.ReasoningOutputTokens < 0 || counts.TotalTokens < 0) return false;
        if (cached > input || counts.CacheWriteInputTokens > input - cached
            || counts.ReasoningOutputTokens > output || input > long.MaxValue - output) return false;
        return counts.TotalTokens is null || counts.TotalTokens == input + output;
    }
}
