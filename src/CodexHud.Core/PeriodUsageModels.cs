namespace CodexHud.Core;

public sealed record LocalUsageResponse(string ThreadId, string ResponseId, string? TurnId,
    DateTimeOffset? RecordedAt, TokenCounts Counts, string? Model, string? ServiceTier, string? Provider);

public sealed record LocalUsageTask(string ThreadId, string Title, string? ParentThreadId,
    bool IsSubagent, bool Archived, SampleHealth Health, IReadOnlyList<string> Notes);

public sealed record LocalTaskTurn(string ThreadId, string TurnId, DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt, DateTimeOffset? EvidenceAt, ActivityState State, SampleHealth Health);

public sealed record LocalUsageLedgerSnapshot(DateTimeOffset ObservedAt,
    IReadOnlyDictionary<string, LocalUsageTask> Tasks, IReadOnlyList<LocalUsageResponse> Responses,
    IReadOnlyList<LocalTaskTurn> Turns, SampleHealth Health, string? Detail = null);

public sealed record PeriodUsageTotals(TokenCounts Counts, decimal? MinimumUsd, decimal? MaximumUsd,
    long PricedResponses, long UnpricedResponses, long UnpricedTokens,
    TimeSpan? Duration, int UnknownTurns, SampleHealth Health);

public sealed record PeriodModelUsage(string Model, PeriodUsageTotals Totals);

public sealed record PeriodTaskUsage(string ThreadId, string Title, bool IsSubagent, bool Archived,
    PeriodUsageTotals Self, PeriodUsageTotals Group, IReadOnlyList<PeriodTaskUsage> Children,
    IReadOnlyList<PeriodModelUsage> Models, IReadOnlyList<LocalTaskTurn> Turns, IReadOnlyList<string> Notes);

public sealed record PeriodUsageSnapshot(DateTimeOffset ObservedAt, UsagePeriod Period,
    PeriodUsageTotals Totals, IReadOnlyList<PeriodTaskUsage> Tasks, SampleHealth Health, string? Detail = null);
