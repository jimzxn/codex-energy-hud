namespace CodexHud.Core;

/// <summary>Display rates only. Cached input remains a subset of input; these never replace raw counts.</summary>
public sealed record TokenRates(double? InputTokens, double? CachedInputTokens, double? OutputTokens,
    double? TotalTokens);
public sealed record UsageRateTick(DateTimeOffset TickStart, TokenRates? Rates, SampleHealth Health);

/// <summary>
/// A trailing average of observed arrivals, not an estimate of token generation time.
/// Each arrival contributes one twentieth of its count for exactly twenty completed seconds.
/// </summary>
public static class UsageSmoothing
{
    public const int WindowSeconds = 20;
    public const int DisplaySeconds = 60;
    public const int RetainedSeconds = DisplaySeconds + WindowSeconds - 1;

    /// <summary>
    /// Calculates one display point per chronological completed-second input bucket.
    /// Missing seconds keep their elapsed-time positions and make a window incomplete.
    /// An incomplete field exposes only a positive observed lower bound, never an invented zero.
    /// </summary>
    public static IReadOnlyList<UsageRateTick> Calculate(IReadOnlyList<UsageTick> history)
    {
        var result = new UsageRateTick[history.Count];
        for (int index = 0; index < history.Count; index++)
        {
            var input = new RateAccumulator();
            var cache = new RateAccumulator();
            var output = new RateAccumulator();
            var total = new RateAccumulator();
            bool partial = false, stale = false;
            int sourceIndex = index;
            var at = history[index].TickStart;
            for (int second = 0; second < WindowSeconds; second++)
            {
                var expected = at.AddSeconds(-second);
                while (sourceIndex >= 0 && history[sourceIndex].TickStart > expected) sourceIndex--;
                UsageTick? tick = sourceIndex >= 0 && history[sourceIndex].TickStart == expected
                    ? history[sourceIndex--] : null;
                var counts = tick?.Counts;
                bool fresh = tick?.Health == SampleHealth.Fresh;
                stale |= tick?.Health == SampleHealth.Stale;
                partial |= tick is null || tick.Health is SampleHealth.Loading or SampleHealth.Partial or SampleHealth.Unavailable
                    || counts?.InputTokens is null || counts?.CachedInputTokens is null || counts?.OutputTokens is null;
                input.Add(counts?.InputTokens, fresh);
                cache.Add(counts?.CachedInputTokens, fresh);
                output.Add(counts?.OutputTokens, fresh);
                // Do not add cache hits again or trust an inconsistent supplied TotalTokens field.
                total.Add(counts?.InputTokens is { } inCount && counts.OutputTokens is { } outCount
                    ? (double)inCount + outCount : null, fresh);
            }
            var inputRate = input.Value;
            var outputRate = output.Value;
            var totalRate = inputRate is { } knownInput && outputRate is { } knownOutput
                ? knownInput + knownOutput : total.Value;
            var rates = new TokenRates(inputRate, cache.Value, outputRate, totalRate);
            bool available = rates.InputTokens is not null || rates.CachedInputTokens is not null
                || rates.OutputTokens is not null || rates.TotalTokens is not null;
            result[index] = new(at, available ? rates : null, !available ? SampleHealth.Unavailable
                : partial ? SampleHealth.Partial : stale ? SampleHealth.Stale : SampleHealth.Fresh);
        }
        return result;
    }

    private struct RateAccumulator
    {
        private double _sum;
        private int _completeSeconds;
        public readonly double? Value => _completeSeconds == WindowSeconds || _sum > 0
            ? _sum / WindowSeconds : null;
        public void Add(double? value, bool fresh)
        {
            if (value is not { } count) return;
            _sum += count;
            if (fresh) _completeSeconds++;
        }
    }
}
