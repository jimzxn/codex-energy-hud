using System.Runtime.CompilerServices;

namespace CodexHud.Core;

/// <summary>Projects verified local responses and turn intervals into an explicit half-open period.</summary>
public static class PeriodUsageCalculator
{
    // Immutable response objects are shared between reads. Weak keys avoid retaining old ledgers.
    private static readonly ConditionalWeakTable<LocalUsageResponse, TokenCostResult> PriceCache = new();
    private sealed record Interval(DateTimeOffset From, DateTimeOffset Through);
    private sealed record Timing(Interval[] Intervals, int Unknown, bool Partial);

    public static PeriodUsageSnapshot Build(LocalUsageLedgerSnapshot ledger, UsagePeriod period,
        DateTimeOffset now, ActivitySnapshot? activity = null)
    {
        var end = period.EndsAt is { } finish && finish < now ? finish : now;
        var allResponses = ledger.Responses.GroupBy(r => (r.ThreadId, r.ResponseId)).ToArray();
        var duplicateConflict = allResponses.Any(g => g.Distinct().Skip(1).Any());
        var dated = allResponses.Select(g => g.First()).Where(r => r.RecordedAt is { } at
            && at >= period.StartedAt && (period.EndsAt == null || at < period.EndsAt) && at <= now).ToArray();
        var undated = allResponses.Select(g => g.First()).Where(r => r.RecordedAt == null).ToArray();
        var turns = ledger.Turns.GroupBy(t => (t.ThreadId, t.TurnId))
            .Select(g => g.OrderByDescending(t => t.EndedAt.HasValue).ThenByDescending(t => t.Health == SampleHealth.Fresh).First())
            .Select(t => Overlay(t, activity)).Where(t => Relevant(t, period, now)).ToArray();
        var metadata = ledger.Tasks.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (string id in dated.Select(r => r.ThreadId).Concat(turns.Select(t => t.ThreadId)).Concat(undated.Select(r => r.ThreadId)).Distinct())
            if (!metadata.ContainsKey(id)) metadata[id] = new(id, "未命名任务", null, false, false, SampleHealth.Partial, ["缺少任务元数据"]);
        var needed = dated.Select(r => r.ThreadId).Concat(turns.Select(t => t.ThreadId))
            .Concat(undated.Select(r => r.ThreadId)).ToHashSet(StringComparer.Ordinal);
        foreach (string id in needed.ToArray())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            var at = id;
            while (metadata.TryGetValue(at, out var task) && task.ParentThreadId is { } parent
                && metadata.ContainsKey(parent) && seen.Add(parent))
            { needed.Add(parent); at = parent; }
        }
        var parents = needed.ToDictionary(id => id, id => metadata[id].ParentThreadId is { } p
            && needed.Contains(p) ? p : null, StringComparer.Ordinal);
        var broken = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in needed)
        {
            var path = new HashSet<string>(StringComparer.Ordinal);
            string? at = id;
            while (at != null && path.Add(at)) at = parents.GetValueOrDefault(at);
            if (at != null) foreach (string member in path) broken.Add(member);
        }
        foreach (string id in broken) parents[id] = null;
        var children = parents.Where(p => p.Value != null).GroupBy(p => p.Value!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToArray(), StringComparer.Ordinal);
        var responseGroups = dated.GroupBy(r => r.ThreadId).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var turnGroups = turns.GroupBy(t => t.ThreadId).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var unknownIds = undated.Select(r => r.ThreadId).ToHashSet(StringComparer.Ordinal);
        static TokenCostResult Price(LocalUsageResponse r) => PriceCache.GetValue(r,
            response => ApiPricingCatalog.Price(response.Counts, response.Model, response.ServiceTier, response.Provider));
        var rows = new Dictionary<string, PeriodTaskUsage>(StringComparer.Ordinal);
        var timings = new Dictionary<string, Timing>(StringComparer.Ordinal);
        var allByGroup = new Dictionary<string, LocalUsageResponse[]>(StringComparer.Ordinal);
        var groupTurns = new Dictionary<string, LocalTaskTurn[]>(StringComparer.Ordinal);
        PeriodTaskUsage Row(string id)
        {
            var meta = metadata[id];
            var ownResponses = responseGroups.GetValueOrDefault(id) ?? [];
            var ownTurns = turnGroups.GetValueOrDefault(id) ?? [];
            var descendants = (children.GetValueOrDefault(id) ?? []).Select(Row).ToArray();
            var all = ownResponses.Concat(descendants.SelectMany(c => allByGroup[c.ThreadId])).ToArray();
            var allTurns = ownTurns.Concat(descendants.SelectMany(c => groupTurns[c.ThreadId])).ToArray();
            var ownTiming = Time(ownTurns, period.StartedAt, end, now, activity, ledger.ObservedAt);

            if (ownResponses.Length > 0 && ownTurns.Length == 0)
            { ownTiming = ownTiming with { Unknown = 1, Partial = true }; }
            var groupTiming = new Timing(Merge(ownTiming.Intervals.Concat(descendants.SelectMany(c => timings[c.ThreadId].Intervals))),
                ownTiming.Unknown + descendants.Sum(c => timings[c.ThreadId].Unknown),
                ownTiming.Partial || descendants.Any(c => timings[c.ThreadId].Partial));
            var notes = meta.Notes.ToHashSet(StringComparer.Ordinal);
            foreach (var r in all) { var price = Price(r); foreach (var note in price.Notes) notes.Add(note); if (price.UnpricedReason != null) notes.Add(price.UnpricedReason); }
            if (unknownIds.Contains(id)) notes.Add("存在没有时间戳的响应，无法归入周期，未计入本期数值");
            if (broken.Contains(id)) notes.Add("父子关系存在循环，已单独列出并去重");
            if (meta.IsSubagent && parents[id] == null) notes.Add("子代理归属未确认，单独计入总计");
            if (ownTiming.Unknown > 0) notes.Add("部分轮次时长未知，显示已确认小计");
            if (groupTiming.Partial && groupTiming.Unknown == 0) notes.Add("运行状态缺少新鲜证据，时长截至最后证据");
            var selfHealth = Combine([meta.Health, ledger.Health], unknownIds.Contains(id) || broken.Contains(id));
            var self = Sum(ownResponses, ownTiming, selfHealth, Price);
            var group = Sum(all, groupTiming, Combine(descendants.Select(c => c.Group.Health).Append(selfHealth), false), Price);
            var models = ownResponses.GroupBy(r => r.Model ?? "未知模型", StringComparer.Ordinal)
                .Select(g => new PeriodModelUsage(g.Key, Sum(g.ToArray(), new([], 0, false), group.Health, Price)))
                .OrderByDescending(m => m.Totals.MinimumUsd ?? -1).ToArray();
            allByGroup[id] = all; groupTurns[id] = allTurns; timings[id] = groupTiming;
            return rows[id] = new(id, meta.Title, meta.IsSubagent, meta.Archived, self, group,
                Array.AsReadOnly(descendants), Array.AsReadOnly(models), Array.AsReadOnly(ownTurns),
                Array.AsReadOnly(notes.Order(StringComparer.Ordinal).ToArray()));
        }
        var roots = parents.Where(p => p.Value == null).Select(p => Row(p.Key))
            .OrderByDescending(r => r.Group.MinimumUsd ?? -1).ThenBy(r => r.Title, StringComparer.Ordinal).ToArray();
        var overallTiming = new Timing([], roots.Sum(r => r.Group.UnknownTurns), roots.Any(r => r.Group.Health != SampleHealth.Fresh));
        var total = Sum(dated, overallTiming, Combine(roots.Select(r => r.Group.Health).Append(ledger.Health),
            duplicateConflict || undated.Length > 0 || period.IsPending), Price);
        var knownDurations = roots.Where(r => r.Group.Duration.HasValue).Select(r => r.Group.Duration!.Value.Ticks).ToArray();
        long durationTicks = 0;
        foreach (long ticks in knownDurations) durationTicks = Add(durationTicks, ticks);
        total = total with { Duration = knownDurations.Length > 0 ? TimeSpan.FromTicks(durationTicks)
            : roots.Length == 0 && ledger.Health == SampleHealth.Fresh ? TimeSpan.Zero : null };
        var detail = "本机全部普通任务；API 等效估算，不代表订阅扣款。组内并行只计一次，不同任务并行分别累计。";
        if (undated.Length > 0) detail += $" {undated.Length} 条响应缺少时间，未归入周期。";
        if (duplicateConflict) detail += " 重复响应内容冲突，仅计一次。";
        if (ledger.Detail != null) detail += " " + ledger.Detail;
        if (period.IsPending) detail += " 额度恢复待确认。";
        else if (period.IsEstimated) detail += " 周期起点为估计，可在报告中校正。";
        return new(ledger.ObservedAt, period, total, Array.AsReadOnly(roots), total.Health, detail);
    }

    private static LocalTaskTurn Overlay(LocalTaskTurn turn, ActivitySnapshot? activity)
    {
        var live = activity?.Tasks.FirstOrDefault(t => t.Id == turn.ThreadId && t.TurnId == turn.TurnId);
        if (live == null || turn.EndedAt.HasValue) return turn;
        return turn with { StartedAt = turn.StartedAt ?? live.StartedAt, EndedAt = live.EndedAt,
            EvidenceAt = live.EvidenceAt ?? turn.EvidenceAt, State = live.State,
            Health = activity!.Health };
    }

    private static bool Relevant(LocalTaskTurn turn, UsagePeriod period, DateTimeOffset now)
    {
        if (turn.StartedAt is { } start && (start >= period.EndsAt || start > now)) return false;
        if (turn.EndedAt is { } end && end <= period.StartedAt) return false;
        if (turn.EndedAt == null && turn.EvidenceAt is { } evidence && evidence < period.StartedAt
            && turn.State == ActivityState.Unconfirmed) return false;
        return true;
    }

    private static Timing Time(LocalTaskTurn[] turns, DateTimeOffset start, DateTimeOffset end,
        DateTimeOffset now, ActivitySnapshot? activity, DateTimeOffset observedAt)
    {
        var intervals = new List<Interval>(); int unknown = 0; bool partial = false;
        foreach (var turn in turns)
        {
            DateTimeOffset? through = turn.EndedAt;
            if (through == null && turn.StartedAt is { } begun)
            {
                var fakeTask = new TaskActivity(turn.ThreadId, "", turn.State, turn.EvidenceAt, begun, "", turn.EndedAt);
                var snapshot = new ActivitySnapshot(observedAt, activity?.AppPresent == true, [],
                    Combine([turn.Health, activity?.Health ?? SampleHealth.Partial], false));
                var display = TaskTiming.Describe(fakeTask, snapshot, now);
                through = display.Elapsed is { } elapsed ? begun + elapsed : null;
                partial |= !display.IsAdvancing;
            }
            if (turn.StartedAt is not { } from || through is not { } to || to < from || to > now)
            { unknown++; partial = true; continue; }
            if (turn.Health is SampleHealth.Stale or SampleHealth.Partial or SampleHealth.Unavailable) partial = true;
            from = from < start ? start : from; to = to > end ? end : to;
            if (to > from) intervals.Add(new(from, to));
        }
        return new(Merge(intervals), unknown, partial);
    }

    private static Interval[] Merge(IEnumerable<Interval> intervals)
    {
        var merged = new List<Interval>();
        foreach (var interval in intervals.OrderBy(i => i.From).ThenBy(i => i.Through))
        {
            if (merged.Count == 0 || merged[^1].Through < interval.From) merged.Add(interval);
            else if (interval.Through > merged[^1].Through) merged[^1] = merged[^1] with { Through = interval.Through };
        }
        return merged.ToArray();
    }

    private static PeriodUsageTotals Sum(LocalUsageResponse[] responses, Timing timing, SampleHealth health,
        Func<LocalUsageResponse, TokenCostResult> price)
    {
        long? SumCount(Func<TokenCounts, long?> selector)
        {
            long sum = 0; bool any = responses.Length == 0 && health == SampleHealth.Fresh;
            foreach (var response in responses)
                if (selector(response.Counts) is { } value && value >= 0) { sum = Add(sum, value); any = true; }
            return any ? sum : null;
        }
        var counts = new TokenCounts(SumCount(c => c.InputTokens), SumCount(c => c.CachedInputTokens),
            SumCount(c => c.CacheWriteInputTokens), SumCount(c => c.OutputTokens), SumCount(c => c.ReasoningOutputTokens),
            SumCount(c => c.InputTokens is { } input && c.OutputTokens is { } output ? Add(input, output) : c.TotalTokens));
        decimal low = 0, high = 0; long priced = 0, unpriced = 0, unpricedTokens = 0; bool partial = timing.Partial;
        foreach (var response in responses)
        {
            var cost = price(response);
            if (cost.IsPriced) { low += cost.MinimumUsd!.Value; high += cost.MaximumUsd ?? cost.MinimumUsd.Value; priced++; }
            else { unpriced++; unpricedTokens = Add(unpricedTokens, response.Counts.TotalTokens ?? Add(response.Counts.InputTokens ?? 0, response.Counts.OutputTokens ?? 0)); }
            partial |= cost.IsPartial || response.Counts.InputTokens == null || response.Counts.OutputTokens == null;
        }
        long duration = 0;
        foreach (var interval in timing.Intervals) duration = Add(duration, (interval.Through - interval.From).Ticks);
        bool knownEmpty = responses.Length == 0 && health == SampleHealth.Fresh;
        return new(counts, priced > 0 || knownEmpty ? low : null, priced > 0 || knownEmpty ? high : null,
            priced, unpriced, unpricedTokens, timing.Intervals.Length > 0 || timing.Unknown == 0 ? TimeSpan.FromTicks(duration) : null,
            timing.Unknown, Combine([health], partial));
    }

    private static long Add(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
    private static SampleHealth Combine(IEnumerable<SampleHealth> values, bool partial)
    {
        var states = values.ToArray();
        if (states.Contains(SampleHealth.Stale)) return SampleHealth.Stale;
        if (states.Contains(SampleHealth.Loading)) return SampleHealth.Loading;
        if (states.Contains(SampleHealth.Unavailable)) return SampleHealth.Partial;
        return partial || states.Contains(SampleHealth.Partial) ? SampleHealth.Partial : SampleHealth.Fresh;
    }
}
