using System.Diagnostics;
using System.Text.Json;
using CodexHud.Core;

/// <summary>Opt-in local-only numeric diagnostic; never accesses quota redemption or user HUD settings.</summary>
public static class LiveBillingCheck
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Value(string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        int seconds = int.TryParse(Value("--seconds"), out int requested) ? Math.Clamp(requested, 1, 600) : 90;
        var startedAt = DateTimeOffset.UtcNow;
        string outputDirectory;
        try
        {
            outputDirectory = Path.GetFullPath(Value("--output")
                ?? Path.Combine("artifacts", "validation", "live-billing-" + startedAt.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { check = "live-local-billing", passed = false,
                exceptionType = error.GetType().Name, phase = "output-directory" }));
            return 1;
        }
        string reportPath = Path.Combine(outputDirectory, "live-billing.json");
        string temporaryLedger = Path.Combine(outputDirectory, ".live-billing-ledger-" + Guid.NewGuid().ToString("N") + ".json");
        var period = new UsagePeriod
        {
            Id = "live-diagnostic-last-seven-days", WindowKey = "diagnostic/local/seven-days",
            Label = "诊断自定义最近 7 天", StartedAt = startedAt.AddDays(-7), EndsAt = startedAt,
            IsCurrent = false, Reason = "Manual", IsEstimated = false, ObservedAt = startedAt
        };
        var elapsed = Stopwatch.StartNew();
        var samples = new List<object>();
        var exceptions = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        SessionCostProvider? provider = null;
        LocalUsageLedgerSnapshot? previous = null;
        NumericSummary? previousSummary = null;
        NumericSummary? lastSummary = null;
        var mismatches = new HashSet<string>(StringComparer.Ordinal);
        int negativeValues = 0, stableReads = 0, stableConsecutive = 0, repeatedTotalChanges = 0,
            responseCountRegressions = 0, partialSamples = 0, unknownModelSamples = 0;
        long maxReadMilliseconds = 0;
        bool reachedDeadline = false, apparentComplete = false, temporaryLedgerRemoved = false;
        string completion = "not-started";
        try
        {
            string home = CodexLocator.ResolveHome(Environment.GetEnvironmentVariable("CODEX_HOME"));
            provider = new SessionCostProvider(home, temporaryLedger);
            while (!deadline.IsCancellationRequested)
            {
                var readWatch = Stopwatch.StartNew();
                var session = await provider.ReadAsync(deadline.Token).ConfigureAwait(false);
                readWatch.Stop();
                maxReadMilliseconds = Math.Max(maxReadMilliseconds, readWatch.ElapsedMilliseconds);
                var ledger = session.Ledger ?? throw new InvalidOperationException("Ledger unavailable");
                // Freeze the reporting endpoint: new activity during this diagnostic is not silently
                // added to the seven-day sample, and repeated aggregation can be compared directly.
                var usage = PeriodUsageCalculator.Build(ledger, period, startedAt);
                var rows = Flatten(usage.Tasks).ToArray();
                int loadingTasks = ledger.Tasks.Values.Count(t => t.Health == SampleHealth.Loading);
                int rawNegatives = ledger.Responses.Sum(r => Negative(r.Counts));
                int aggregateNegatives = Negative(usage.Totals) + rows.Sum(r => Negative(r.Self) + Negative(r.Group));
                negativeValues += rawNegatives + aggregateNegatives;
                CheckTotals(usage.Totals, usage.Tasks.Select(r => r.Group).ToArray(), "roots", true, mismatches);
                foreach (var row in rows)
                    CheckTotals(row.Group, row.Children.Select(c => c.Group).Prepend(row.Self).ToArray(), "parent", false, mismatches);

                var periodResponses = ledger.Responses.Where(r => r.RecordedAt >= period.StartedAt
                    && r.RecordedAt < period.EndsAt).GroupBy(r => (r.ThreadId, r.ResponseId)).Select(g => g.First()).ToArray();
                decimal pricedTokens = 0;
                foreach (var response in periodResponses)
                    if (ApiPricingCatalog.Price(response.Counts, response.Model, response.ServiceTier, response.Provider).IsPriced)
                        pricedTokens += ResponseTotal(response.Counts);
                var summary = new NumericSummary(ledger.ObservedAt, ledger.Health.ToString(), usage.Health.ToString(),
                    ledger.Tasks.Count, ledger.Responses.Count, ledger.Turns.Count, loadingTasks,
                    ledger.Responses.LongCount(r => r.RecordedAt == null),
                    ledger.Responses.Count - ledger.Responses.Select(r => (r.ThreadId, r.ResponseId)).Distinct().Count(),
                    usage.Tasks.Count, rows.Length, periodResponses.LongLength, pricedTokens,
                    usage.Totals.Counts, usage.Totals.MinimumUsd, usage.Totals.MaximumUsd,
                    usage.Totals.PricedResponses, usage.Totals.UnpricedResponses, usage.Totals.UnpricedTokens,
                    usage.Totals.Duration?.TotalSeconds, usage.Totals.UnknownTurns);
                bool sameInput = previous != null && SameInput(previous, ledger);
                bool sameTotals = previousSummary != null && SameTotals(previousSummary, summary);
                if (sameInput)
                {
                    stableReads++;
                    stableConsecutive++;
                    if (!sameTotals) repeatedTotalChanges++;
                }
                else stableConsecutive = 0;
                if (previousSummary != null && summary.ResponseCount < previousSummary.ResponseCount)
                    responseCountRegressions++;
                if (ledger.Health != SampleHealth.Fresh || usage.Health != SampleHealth.Fresh) partialSamples++;
                if (usage.Totals.UnpricedResponses > 0) unknownModelSamples++;
                samples.Add(new { sequence = samples.Count + 1, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    readMilliseconds = readWatch.ElapsedMilliseconds, sameInput, sameTotals,
                    negativeValues = rawNegatives + aggregateNegatives, mismatchKinds = mismatches.Count, summary });
                previous = ledger;
                previousSummary = lastSummary = summary;
                if (negativeValues > 0 || mismatches.Count > 0)
                { completion = "invariant-failed"; break; }
                // Partial can mean incomplete discovery, even when no individual task says Loading.
                // Only a fresh ledger and an unchanged repeat justify ending before the deadline.
                if (ledger.Health == SampleHealth.Fresh && loadingTasks == 0 && stableConsecutive >= 1)
                { apparentComplete = true; completion = "fresh-ledger-stable-repeat"; break; }
                await Task.Delay(25, deadline.Token).ConfigureAwait(false);
            }
            if (deadline.IsCancellationRequested)
            { reachedDeadline = true; completion = "time-budget"; }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { reachedDeadline = true; completion = "time-budget"; }
        catch (Exception error)
        {
            // Exception messages can contain source paths or malformed log content. Keep type only.
            exceptions.Add(error.GetType().Name);
            completion = "exception";
        }
        finally
        {
            try { provider?.Dispose(); }
            catch (Exception error) { exceptions.Add(error.GetType().Name); }
            // Dispose may flush metadata to this private file. Remove only our exact GUID paths,
            // leaving the requested output directory and every user-owned ledger untouched.
            try
            {
                if (File.Exists(temporaryLedger)) File.Delete(temporaryLedger);
                if (File.Exists(temporaryLedger + ".tmp")) File.Delete(temporaryLedger + ".tmp");
                temporaryLedgerRemoved = !File.Exists(temporaryLedger) && !File.Exists(temporaryLedger + ".tmp");
            }
            catch (Exception error) { exceptions.Add(error.GetType().Name); }
        }
        elapsed.Stop();
        bool passed = exceptions.Count == 0 && negativeValues == 0 && mismatches.Count == 0;
        var report = new
        {
            check = "live-local-billing", startedAt, completedAt = DateTimeOffset.UtcNow,
            scope = "Fixed diagnostic seven-day interval [from, through); local logs only; not an account billing period.",
            period = new { from = period.StartedAt, through = period.EndsAt, isCurrent = false },
            requestedSeconds = seconds, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
            completion, reachedDeadline, apparentComplete, sampleCount = samples.Count,
            maxReadMilliseconds, stableReads, repeatedTotalChanges, responseCountRegressions,
            repeatDedupVerified = stableReads > 0 && repeatedTotalChanges == 0,
            partialSamples, unknownModelSamples, negativeValues,
            mismatchKinds = mismatches.Order(StringComparer.Ordinal).ToArray(),
            exceptionTypes = exceptions, temporaryLedgerRemoved, passed,
            assertionScope = "Failure means exception, negative numeric values, or root/parent additive totals mismatch. Partial and unknown pricing are coverage observations.",
            final = lastSummary, samples
        };
        try
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { report.check, report.passed, report.completion,
                report.elapsedSeconds, report.sampleCount, report.stableReads, report.negativeValues,
                mismatchKinds = mismatches.Count, exceptionCount = exceptions.Count,
                ledgerHealth = lastSummary?.LedgerHealth, periodHealth = lastSummary?.PeriodHealth,
                taskCount = lastSummary?.TaskCount, responseCount = lastSummary?.ResponseCount,
                turnCount = lastSummary?.TurnCount, output = reportPath }));
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { check = "live-local-billing", passed = false,
                exceptionType = error.GetType().Name, phase = "report-write" }));
            return 1;
        }
        return passed ? 0 : 1;
    }

    private static IEnumerable<PeriodTaskUsage> Flatten(IEnumerable<PeriodTaskUsage> roots)
    {
        var pending = new Stack<PeriodTaskUsage>(roots);
        while (pending.TryPop(out var row))
        {
            yield return row;
            foreach (var child in row.Children) pending.Push(child);
        }
    }

    private static bool SameInput(LocalUsageLedgerSnapshot previous, LocalUsageLedgerSnapshot current) =>
        previous.Responses.SequenceEqual(current.Responses) && previous.Turns.SequenceEqual(current.Turns)
        && previous.Tasks.Count == current.Tasks.Count && previous.Tasks.All(pair =>
            current.Tasks.TryGetValue(pair.Key, out var task) && pair.Value.ParentThreadId == task.ParentThreadId
            && pair.Value.IsSubagent == task.IsSubagent && pair.Value.Health == task.Health)
        && previous.Health == current.Health;

    private static bool SameTotals(NumericSummary previous, NumericSummary current) =>
        previous.PeriodCounts == current.PeriodCounts && previous.MinimumUsd == current.MinimumUsd
        && previous.MaximumUsd == current.MaximumUsd && previous.PricedResponses == current.PricedResponses
        && previous.UnpricedResponses == current.UnpricedResponses && previous.UnpricedTokens == current.UnpricedTokens
        && previous.DurationSeconds == current.DurationSeconds && previous.UnknownTurns == current.UnknownTurns;

    private static decimal ResponseTotal(TokenCounts counts) => counts.InputTokens is { } input && counts.OutputTokens is { } output
        ? (decimal)input + output : counts.TotalTokens ?? 0;
    private static long?[] Values(TokenCounts counts) => [counts.InputTokens, counts.CachedInputTokens,
        counts.CacheWriteInputTokens, counts.OutputTokens, counts.ReasoningOutputTokens, counts.TotalTokens];
    private static int Negative(TokenCounts counts) => Values(counts).Count(value => value < 0);
    private static int Negative(PeriodUsageTotals totals) => Negative(totals.Counts)
        + (totals.MinimumUsd < 0 ? 1 : 0) + (totals.MaximumUsd < 0 ? 1 : 0)
        + (totals.PricedResponses < 0 ? 1 : 0) + (totals.UnpricedResponses < 0 ? 1 : 0)
        + (totals.UnpricedTokens < 0 ? 1 : 0) + (totals.Duration < TimeSpan.Zero ? 1 : 0)
        + (totals.UnknownTurns < 0 ? 1 : 0);

    private static void CheckTotals(PeriodUsageTotals actual, IReadOnlyList<PeriodUsageTotals> parts,
        string scope, bool compareDuration, HashSet<string> mismatches)
    {
        void Compare(string name, decimal? total, IEnumerable<decimal?> values, bool saturating = false)
        {
            var known = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            if (total == null || known.Length == 0) return;
            decimal sum = known.Sum();
            if (saturating) sum = Math.Min(sum, long.MaxValue);
            if (total.Value != sum) mismatches.Add(scope + ":" + name);
        }
        var tokens = Values(actual.Counts);
        for (int i = 0; i < tokens.Length; i++)
        {
            int column = i;
            Compare("token-" + column, tokens[column], parts.Select(p => (decimal?)Values(p.Counts)[column]), true);
        }
        Compare("minimum-usd", actual.MinimumUsd, parts.Select(p => p.MinimumUsd));
        Compare("maximum-usd", actual.MaximumUsd, parts.Select(p => p.MaximumUsd));
        Compare("priced-responses", actual.PricedResponses, parts.Select(p => (decimal?)p.PricedResponses), true);
        Compare("unpriced-responses", actual.UnpricedResponses, parts.Select(p => (decimal?)p.UnpricedResponses), true);
        Compare("unpriced-tokens", actual.UnpricedTokens, parts.Select(p => (decimal?)p.UnpricedTokens), true);
        Compare("unknown-turns", actual.UnknownTurns, parts.Select(p => (decimal?)p.UnknownTurns));
        if (compareDuration)
            Compare("duration-ticks", actual.Duration?.Ticks, parts.Select(p => (decimal?)p.Duration?.Ticks), true);
    }

    private sealed record NumericSummary(DateTimeOffset ObservedAt, string LedgerHealth, string PeriodHealth,
        int TaskCount, int ResponseCount, int TurnCount, int LoadingTaskCount, long UndatedResponseCount,
        int DuplicateResponseCount, int RootTaskCount, int PeriodTaskCount, long PeriodResponseCount,
        decimal PricedTokens, TokenCounts PeriodCounts, decimal? MinimumUsd, decimal? MaximumUsd,
        long PricedResponses, long UnpricedResponses, long UnpricedTokens, double? DurationSeconds, int UnknownTurns);
}
