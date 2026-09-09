namespace CodexHud;

public partial class MainWindow
{
    private void AcceptUsageSnapshot(CodexHud.Core.UsageLedgerSnapshot usage)
    {
        // Background reads can finish publishing out of order; an older snapshot must not
        // overwrite the newer ledger state used by recovery and quota pairing.
        if (_usageData is null || usage.ObservedAt >= _usageData.ObservedAt
            || _usageData.ObservedAt > DateTimeOffset.UtcNow.AddSeconds(15)) _usageData = usage;
    }

    private object EstimateDiagnostics()
    {
        var estimate = CurrentEstimate();
        return new
        {
            state = estimate.State.ToString(),
            remainingPercent = _quotaData?.Windows.FirstOrDefault(window => window.Key == _settings.SelectedQuotaKey)?.RemainingPercent,
            estimate.Segments, estimate.ObservedDrop,
            estimate.PendingDrop, estimate.RecoverySamples, estimate.RecoveryRequired,
            estimate.ProgressReason, estimate.LastResetReason, estimate.LastResetAt,
            estimate.Through, estimate.RemainingTokens
        };
    }
}
