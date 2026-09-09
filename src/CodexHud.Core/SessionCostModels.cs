namespace CodexHud.Core;

/// <summary>USD equivalent of recorded API tokens, not a subscription charge or invoice.</summary>
public sealed record TokenCostResult(decimal? MinimumUsd, decimal? MaximumUsd,
    IReadOnlyList<string> Notes, string? UnpricedReason = null)
{
    public bool IsPriced => MinimumUsd.HasValue;
    public bool IsPartial => !IsPriced || !MaximumUsd.HasValue || MinimumUsd != MaximumUsd
        || UnpricedReason is not null || HasUncertainPricingScope;
    internal bool HasUncertainPricingScope { get; init; }
}

public sealed record SessionCostEstimate(string ThreadId)
{
    public decimal SelfUsd { get; init; }
    public decimal DescendantsUsd { get; init; }
    public decimal SelfUpperUsd { get; init; }
    public decimal DescendantsUpperUsd { get; init; }
    public decimal TotalUsd => SelfUsd + DescendantsUsd;
    public decimal TotalUpperUsd => SelfUpperUsd + DescendantsUpperUsd;
    public long PricedResponses { get; init; }
    public long UnpricedResponses { get; init; }
    public long UnpricedTokens { get; init; }
    public bool IsSubagent { get; init; }
    public int DescendantCount { get; init; }
    public SampleHealth Health { get; init; } = SampleHealth.Loading;
    public DateTimeOffset? LastUsageAt { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record SessionCostSnapshot(DateTimeOffset ObservedAt,
    IReadOnlyDictionary<string, SessionCostEstimate> Tasks, SampleHealth Health, string? Detail = null)
{
    /// <summary>All local ordinary tasks, independently of the legacy task-card projection.</summary>
    public LocalUsageLedgerSnapshot? Ledger { get; init; }
}
