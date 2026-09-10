using CodexHud.Core;

public static class UsageSmoothingTests
{
    public static int Run()
    {
        int checks = 0, failures = 0;
        void Check(bool valid, string scenario)
        {
            checks++;
            if (valid) return;
            failures++;
            Console.Error.WriteLine("FAIL smoothing: " + scenario);
        }
        static bool Near(double? actual, double expected) => actual is { } value && Math.Abs(value - expected) < 1e-9;
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        TokenCounts Counts(long? input = 0, long? cache = 0, long? output = 0, long? total = null)
            => new(input, cache, 0, output, 0, total ?? (input + output));
        UsageTick[] History(int seconds) => Enumerable.Range(0, seconds)
            .Select(second => new UsageTick(start.AddSeconds(second), Counts(), SampleHealth.Fresh)).ToArray();
        UsageTick With(UsageTick tick, long input, long cache, long output)
            => tick with { Counts = Counts(input, cache, output) };

        Check(UsageSmoothing.Calculate([]).Count == 0, "empty history has no fabricated display points");
        Check(UsageSmoothing.WindowSeconds == 20 && UsageSmoothing.DisplaySeconds == 60
            && UsageSmoothing.RetainedSeconds == 79, "retention includes the full nineteen-second warmup");

        var burst = History(65);
        burst[20] = With(burst[20], 4000, 3000, 2000);
        var before = burst.ToArray();
        var smoothed = UsageSmoothing.Calculate(burst);
        Check(smoothed.Count == burst.Length && smoothed.Select(tick => tick.TickStart).SequenceEqual(burst.Select(tick => tick.TickStart)),
            "output points preserve source count and timestamps");
        Check(smoothed.Skip(20).Take(20).All(tick => Near(tick.Rates?.TotalTokens, 300) && tick.Health == SampleHealth.Fresh)
            && Near(smoothed[19].Rates?.TotalTokens, 0) && Near(smoothed[40].Rates?.TotalTokens, 0),
            "isolated six-thousand burst contributes exactly three hundred per second for twenty ticks");
        Check(Near(smoothed.Sum(tick => tick.Rates?.TotalTokens ?? 0), 6000),
            "the complete smoothed burst integrates to the raw six-thousand tokens");
        Check(burst.SequenceEqual(before) && ReferenceEquals(burst[20].Counts, before[20].Counts),
            "calculation leaves all raw counts and sample records untouched");
        Check(smoothed.Skip(19).All(tick => tick.Rates?.TotalTokens == tick.Rates?.InputTokens + tick.Rates?.OutputTokens)
            && Near(smoothed[20].Rates?.CachedInputTokens, 150),
            "total is input plus output while cache remains a subset of input");

        var overlap = burst.ToArray();
        overlap[27] = With(overlap[27], 1000, 500, 1000);
        var combined = UsageSmoothing.Calculate(overlap);
        Check(Near(combined[26].Rates?.TotalTokens, 300)
            && combined.Skip(27).Take(13).All(tick => Near(tick.Rates?.TotalTokens, 400))
            && combined.Skip(40).Take(7).All(tick => Near(tick.Rates?.TotalTokens, 100))
            && Near(combined[47].Rates?.TotalTokens, 0),
            "overlapping arrivals add and expire on their own twenty-second boundaries");
        Check(Near(combined.Sum(tick => tick.Rates?.InputTokens ?? 0), 5000)
            && Near(combined.Sum(tick => tick.Rates?.CachedInputTokens ?? 0), 3500)
            && Near(combined.Sum(tick => tick.Rates?.OutputTokens ?? 0), 3000)
            && Near(combined.Sum(tick => tick.Rates?.TotalTokens ?? 0), 8000),
            "overlapping curves conserve each component without adding cache to total");

        var secondTask = History(65);
        secondTask[27] = With(secondTask[27], 1000, 500, 1000);
        var secondTaskRates = UsageSmoothing.Calculate(secondTask);
        Check(Enumerable.Range(19, combined.Count - 19).All(index =>
                Near(combined[index].Rates?.TotalTokens, smoothed[index].Rates!.TotalTokens!.Value
                    + secondTaskRates[index].Rates!.TotalTokens!.Value)),
            "smoothing individual task rates sums to the aggregate when their observation windows are complete");
        var large = History(20).Select(tick => With(tick, long.MaxValue, 0, 0)).ToArray();
        Check(UsageSmoothing.Calculate(large)[19].Rates?.InputTokens == (double)long.MaxValue,
            "summing large counters cannot overflow an intermediate integer accumulator");
        var small = History(45);
        small[20] = With(small[20], 1, 1, 2);
        var fractional = UsageSmoothing.Calculate(small);
        Check(Near(fractional[20].Rates?.InputTokens, .05) && Near(fractional[20].Rates?.CachedInputTokens, .05)
            && Near(fractional[20].Rates?.OutputTokens, .1) && Near(fractional[20].Rates?.TotalTokens, .15)
            && Near(fractional.Sum(tick => tick.Rates?.TotalTokens ?? 0), 3),
            "low token counts retain fractional rates and integrated usage");
        var misleadingTotal = small.ToArray();
        misleadingTotal[20] = misleadingTotal[20] with { Counts = Counts(1, 1, 2, 999) };
        Check(Near(UsageSmoothing.Calculate(misleadingTotal)[20].Rates?.TotalTokens, .15),
            "the displayed total derives from input plus output even if supplied total disagrees");

        var startup = History(21);
        startup[0] = With(startup[0], 6000, 0, 0);
        var warming = UsageSmoothing.Calculate(startup);
        Check(warming.Take(19).All(tick => Near(tick.Rates?.TotalTokens, 300) && tick.Health == SampleHealth.Partial)
            && warming[0].Rates?.OutputTokens is null && warming[0].Rates?.CachedInputTokens is null,
            "startup uses the fixed divisor and labels a positive lower bound without inventing unknown zeros");
        Check(warming[19].Health == SampleHealth.Fresh && Near(warming[19].Rates?.TotalTokens, 300)
            && warming[20].Health == SampleHealth.Fresh && Near(warming[20].Rates?.TotalTokens, 0),
            "a full fresh window becomes authoritative and the startup arrival expires after twenty seconds");
        var quiet = UsageSmoothing.Calculate(History(20));
        Check(quiet.Take(19).All(tick => tick.Rates is null && tick.Health == SampleHealth.Unavailable)
            && quiet[19].Health == SampleHealth.Fresh && Near(quiet[19].Rates?.TotalTokens, 0),
            "unobserved pre-start time is unavailable until twenty known zero seconds cover the window");
        var unknown = History(25).Select(tick => tick with { Counts = null, Health = SampleHealth.Unavailable }).ToArray();
        Check(UsageSmoothing.Calculate(unknown).All(tick => tick.Rates is null && tick.Health == SampleHealth.Unavailable),
            "fully unknown histories never produce zero rates");

        var gap = overlap.Where((_, index) => index != 28).ToArray();
        var gaps = UsageSmoothing.Calculate(gap);
        UsageRateTick At(IReadOnlyList<UsageRateTick> points, int second) => points.Single(tick => tick.TickStart == start.AddSeconds(second));
        Check(At(gaps, 30).Health == SampleHealth.Partial && Near(At(gaps, 30).Rates?.TotalTokens, 400),
            "a missing timestamp preserves known arrivals as a partial lower bound");
        Check(At(gaps, 47).Rates is null && At(gaps, 47).Health == SampleHealth.Unavailable
            && At(gaps, 48).Health == SampleHealth.Fresh && Near(At(gaps, 48).Rates?.TotalTokens, 0),
            "missing seconds age out by elapsed time without being compressed into later windows");
        var suspended = UsageSmoothing.Calculate([startup[0], new(start.AddHours(12), Counts(), SampleHealth.Fresh)]);
        Check(suspended[1].Rates is null && suspended[1].Health == SampleHealth.Unavailable,
            "long clock gaps do not keep ancient arrivals alive");

        var partial = burst.ToArray();
        partial[25] = partial[25] with { Counts = null, Health = SampleHealth.Partial };
        var partialRates = UsageSmoothing.Calculate(partial);
        Check(partialRates[25].Health == SampleHealth.Partial && Near(partialRates[25].Rates?.TotalTokens, 300)
            && partialRates[40].Rates is null && partialRates[45].Health == SampleHealth.Fresh,
            "explicit missing buckets preserve observed positives and recover only when the gap expires");
        var stale = burst.ToArray();
        stale[20] = stale[20] with { Health = SampleHealth.Stale };
        var staleRates = UsageSmoothing.Calculate(stale);
        Check(staleRates[20].Health == SampleHealth.Stale && Near(staleRates[20].Rates?.TotalTokens, 300)
            && staleRates[40].Health == SampleHealth.Fresh,
            "stale numeric evidence is never promoted to fresh while it remains in the window");
        var missingCache = burst.ToArray();
        missingCache[20] = missingCache[20] with { Counts = Counts(4000, null, 2000) };
        var cacheRates = UsageSmoothing.Calculate(missingCache);
        Check(cacheRates[20].Health == SampleHealth.Partial && cacheRates[20].Rates?.CachedInputTokens is null
            && Near(cacheRates[20].Rates?.InputTokens, 200) && Near(cacheRates[20].Rates?.TotalTokens, 300),
            "a missing cache field does not erase known input and output or invent cache zero");
        var missingOutput = burst.ToArray();
        missingOutput[20] = missingOutput[20] with { Counts = Counts(4000, 3000, null) };
        var outputRates = UsageSmoothing.Calculate(missingOutput);
        Check(outputRates[20].Health == SampleHealth.Partial && outputRates[20].Rates?.OutputTokens is null
            && outputRates[20].Rates?.TotalTokens is null && Near(outputRates[20].Rates?.InputTokens, 200),
            "a missing output field cannot turn an incomplete total into a known rate");

        var retained = History(UsageSmoothing.RetainedSeconds);
        retained[0] = With(retained[0], 6000, 0, 0);
        var visible = UsageSmoothing.Calculate(retained).TakeLast(UsageSmoothing.DisplaySeconds).ToArray();
        Check(visible.Length == 60 && visible[0].TickStart == start.AddSeconds(19)
            && visible[0].Health == SampleHealth.Fresh && Near(visible[0].Rates?.TotalTokens, 300)
            && Near(visible[1].Rates?.TotalTokens, 0),
            "the oldest retained bucket supplies full warmup to the first of sixty visible points");

        Console.WriteLine($"Usage smoothing: {checks} checks, {failures} failure(s)");
        return failures;
    }
}
