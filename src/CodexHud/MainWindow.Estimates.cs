using System.Globalization;
using System.IO;
using System.Windows;
using CodexHud.Core;

namespace CodexHud;

public partial class MainWindow
{
    private string? _sampledUsageEpoch;

    private QuotaTokenEstimate CurrentEstimate()
    {
        var selected = _quotaData?.Windows.FirstOrDefault(w => w.Key == _settings.SelectedQuotaKey);
        var estimate = SelectEstimate(_tokenEstimator, selected, _settings.SelectedQuotaKey, DateTimeOffset.UtcNow);
        return ApplyUsageAlignment(estimate, _usageData, _sampledUsageEpoch);
    }

    private static QuotaTokenEstimate SelectEstimate(QuotaTokenEstimator estimator,
        QuotaWindow? selected, string? selectedKey, DateTimeOffset now) => selected is null
            ? estimator.GetSavedHistory(selectedKey, now) : estimator.Get(selected, now);
    private static QuotaTokenEstimate ApplyUsageAlignment(QuotaTokenEstimate estimate,
        UsageLedgerSnapshot? usage, string? sampledEpoch)
    {
        if (estimate.State is EstimateState.Unavailable or EstimateState.Stale) return estimate;
        if (usage is { Health: not SampleHealth.Fresh })
            return estimate with { State = EstimateState.Incomplete, RemainingTokens = null,
                LowerTokens = null, UpperTokens = null, RecoverySamples = 0,
                RecoveryRequired = estimate.Segments > 0 ? 2 : estimate.RecoveryRequired,
                ProgressReason = usage.Detail ?? "本机 Token 采样暂停，等待重新对齐",
                Detail = usage.Detail ?? "本机 Token 数据不完整" };
        if (sampledEpoch != null && usage?.Epoch != sampledEpoch)
            return estimate with { State = EstimateState.Calibrating, RemainingTokens = null,
                LowerTokens = null, UpperTokens = null, RecoverySamples = 0,
                RecoveryRequired = estimate.Segments > 0 ? 2 : estimate.RecoveryRequired,
                ProgressReason = "Token 采样已重建，等待连续健康配对",
                Detail = "Token 采样已重新建立；保留累计进度，等待两次时间不同的健康配对" };
        return estimate;
    }

    private void UpdateEstimate()
    {
        if (!_ready) return;
        RenderEstimate(CurrentEstimate());
    }

    private void RenderEstimate(QuotaTokenEstimate estimate)
    {
        var recovering = estimate.RecoveryRequired > 0;
        bool hasValue = !recovering && (estimate.State is EstimateState.Estimated or EstimateState.Variable)
            && estimate.RemainingTokens is >= 0 && double.IsFinite(estimate.RemainingTokens.Value);
        string status = recovering && (estimate.State is EstimateState.Calibrating or EstimateState.Estimated or EstimateState.Variable)
            ? "恢复中" : estimate.State switch
        {
            EstimateState.Estimated => "近期估算",
            EstimateState.Variable => "波动较大",
            EstimateState.Calibrating => "校准中",
            EstimateState.Incomplete => "数据不全",
            EstimateState.Stale => "已过期",
            _ => "暂不可估"
        };
        var number = FormatEstimatedTokens(estimate.RemainingTokens);
        EstimateText.Text = hasValue ? $"预估 ≈ {number} Token" : $"Token · {status}";
        EstimatePanelValue.Text = hasValue ? $"≈ {number}" : status;
        var range = hasValue && estimate.LowerTokens is >= 0 && estimate.UpperTokens is >= 0
            && FormatEstimatedTokens(estimate.LowerTokens) != FormatEstimatedTokens(estimate.UpperTokens)
            ? $"范围 {FormatEstimatedTokens(estimate.LowerTokens)}–{FormatEstimatedTokens(estimate.UpperTokens)}" : null;
        var kept = $"已保留 {Math.Max(estimate.Segments, 0)} 段 · 累计下降 {FormatEstimateProgress(estimate.ObservedDrop)} 个百分点";
        EstimateMeta.Text = (range == null ? "" : range + " · ") + kept
            + (estimate.State == EstimateState.Variable ? " · 波动较大" : "");
        var pending = FormatEstimateProgress(estimate.PendingDrop);
        var paused = estimate.State is EstimateState.Stale or EstimateState.Incomplete;
        var recoveryProgress = recovering
            ? $"恢复 {Math.Clamp(estimate.RecoverySamples, 0, estimate.RecoveryRequired)}/{estimate.RecoveryRequired} 次健康采样"
            : "";
        EstimateProgress.Text = estimate.State == EstimateState.Unavailable
            ? "等待可用额度和 Token 采样"
            : recovering
                ? (paused ? "暂停 · " : "") + recoveryProgress + $" · 已累计下降 {pending} 个百分点"
                : paused
                    ? $"当前段暂停 · 已累计下降 {pending} 个百分点"
                    : $"当前段下降 {pending} / 2 个百分点" + (hasValue ? "" : " · 首次校准需 3 段 / 6 个百分点");
        string? reason = estimate.ProgressReason;
        if (string.IsNullOrWhiteSpace(reason) && !hasValue)
            reason = estimate.State == EstimateState.Calibrating ? "等待同步采样" : estimate.Detail;
        string? resetReason = null;
        if (!hasValue && !string.IsNullOrWhiteSpace(estimate.LastResetReason)
            && !string.Equals(reason, estimate.LastResetReason, StringComparison.Ordinal))
        {
            var resetTime = estimate.LastResetAt is { } resetAt ? $" {resetAt.ToLocalTime():HH:mm}" : "";
            resetReason = $"最近调整{resetTime} · {estimate.LastResetReason}";
        }
        EstimateReason.Text = reason ?? "";
        EstimateReason.Visibility = string.IsNullOrWhiteSpace(reason) ? Visibility.Collapsed : Visibility.Visible;
        EstimateReason.ToolTip = reason;
        EstimateResetReason.Text = resetReason ?? "";
        EstimateResetReason.Visibility = string.IsNullOrWhiteSpace(resetReason) ? Visibility.Collapsed : Visibility.Visible;
        EstimateResetReason.ToolTip = resetReason;
        var period = estimate.From is { } from && estimate.Through is { } through
            ? $"\n采样 {from.ToLocalTime():MM-dd HH:mm}–{through.ToLocalTime():MM-dd HH:mm}" : "";
        string hint = "按本机近期模型和 Token 结构估算；不包含未观测设备或云端的消耗。"
            + "\n公式：同期新增 Token ÷ 额度下降百分点 × 当前剩余百分比。"
            + "\n范围来自已观察区间的波动，不是统计置信区间。"
            + period + $"\n有效区间 {estimate.Segments} · 额度下降 {estimate.ObservedDrop:0.##} 个百分点"
            + $"\n当前未完成段下降 {FormatEstimateProgress(estimate.PendingDrop)} 个百分点"
            + (string.IsNullOrWhiteSpace(estimate.ProgressReason) ? "" : $"\n当前状态：{estimate.ProgressReason}")
            + (string.IsNullOrWhiteSpace(estimate.LastResetReason) ? "" : $"\n最近采样调整：{estimate.LastResetReason}"
                + (estimate.LastResetAt is { } reset ? $"（{reset.ToLocalTime():MM-dd HH:mm:ss}）" : ""))
            + (recovering ? $"\n{recoveryProgress}；需连续、时间不同的健康配对，无需额外额度下降。" : "")
            + $"\n{estimate.Detail}";
        if (estimate.Diagnostics is { Count: > 0 })
            hint += "\n最近采样事件\n" + string.Join("\n", estimate.Diagnostics.OrderByDescending(item => item.At).Take(4).Select(item =>
                $"{item.At.ToLocalTime():MM-dd HH:mm:ss} · {item.Kind} · {item.Reason}"
                + (item.PreviousRemaining != null || item.Remaining != null
                    ? $" · 额度 {FormatDiagnosticRemaining(item.PreviousRemaining)} → {FormatDiagnosticRemaining(item.Remaining)}" : "")
                + (item.AcceptedResetAt != item.ReportedResetAt
                    ? $" · 已接受重置 {item.AcceptedResetAt?.ToLocalTime():MM-dd HH:mm:ss} / 本次报告 {item.ReportedResetAt?.ToLocalTime():MM-dd HH:mm:ss}" : "")));
        EstimateText.ToolTip = hint;
        EstimateCard.ToolTip = hint;
    }

    private static string FormatDiagnosticRemaining(double? value)
        => value is >= 0 and <= 100 && double.IsFinite(value.Value) ? FormatEstimateProgress(value.Value) + "%" : "—";

    private static string FormatEstimateProgress(double value)
        => double.IsFinite(value) && value >= 0 ? value.ToString("0.##", CultureInfo.InvariantCulture) : "—";

    private static string FormatEstimatedTokens(double? value)
    {
        if (value is not >= 0 || !double.IsFinite(value.Value)) return "—";
        return value switch
        {
            >= 1_000_000_000 => (value.Value / 1_000_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "B",
            >= 1_000_000 => (value.Value / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M",
            >= 1_000 => (value.Value / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "K",
            _ => value.Value.ToString("0", CultureInfo.InvariantCulture)
        };
    }

    private void RunEstimateUiChecks(List<object> results, string directory)
    {
        var originalQuota = _quotaData;
        var originalSelection = _settings.SelectedQuotaKey;
        var now = DateTimeOffset.UtcNow;
        var window = new QuotaWindow("ui-estimate", "codex", "测试额度 · 1 小时", "primary", 60, 90, now.AddHours(1));
        var historyPath = Path.Combine(directory, $"saved-estimate-{Guid.NewGuid():N}.json");
        var estimator = new QuotaTokenEstimator(historyPath);
        QuotaSnapshot latest = new(now, [window], SampleHealth.Fresh) { AccountKey = "ui-fixture-account" };
        for (int i = 0; i <= 3; i++)
        {
            var at = now.AddMinutes(i - 3);
            window = window with { RemainingPercent = 90 - 2 * i };
            latest = new(at, [window], SampleHealth.Fresh) { AccountKey = "ui-fixture-account" };
            estimator.Observe(latest, new UsageLedgerSnapshot(at, "ui-session",
                new TokenCounts(i * 180_000L, i * 120_000L, 0, i * 20_000L, 0, i * 200_000L),
                SampleHealth.Fresh, 4, "fixture-model"));
        }
        var savedOnly = SelectEstimate(new QuotaTokenEstimator(historyPath), null, window.Key, now);
        RenderEstimate(savedOnly);
        results.Add(new { scenario = "saved-calibration-visible-before-first-quota", passed = savedOnly.Segments == 3
            && savedOnly.ObservedDrop == 6 && savedOnly.RemainingTokens is null
            && EstimateMeta.Text.Contains("已保留 3 段") && EstimateMeta.Text.Contains("下降 6 个百分点")
            && !EstimateText.Text.Contains("≈") && !EstimateProgress.Text.Contains("首次校准") });
        var estimated = estimator.Get(window, now);
        _quotaData = latest;
        _settings.SelectedQuotaKey = window.Key;
        UpdateQuota();
        RenderEstimate(estimated);
        OpenPanel("quotas");
        results.Add(new { scenario = "estimate-calibrates-and-renders", passed = estimated.State is EstimateState.Estimated or EstimateState.Variable
            && Math.Abs((estimated.RemainingTokens ?? -1) - 8_400_000) < 1 && EstimateText.Text.Contains("≈ 8.4M")
            && EstimatePanelValue.Text == "≈ 8.4M" && EstimateMeta.Text.Contains("3 段") });
        foreach (var scale in new[] { 1d, 1.5d, 2d })
        {
            _settings.Scale = scale; ApplyAppearance(); UpdateLayout();
            results.Add(new { scenario = $"estimate-panel-scale-{scale}", width = ActualWidth, height = ActualHeight,
                passed = EstimateCard.ActualWidth >= 460 && EstimateText.ActualWidth > 0
                    && ActualHeight <= WindowPlacementService.WorkingHeightDip(this, _settings) });
            Capture(Path.Combine(directory, $"estimate-fixture-{scale * 100:0}.png"));
        }
        _settings.Scale = 1; ApplyAppearance();
        RenderEstimate(estimated with { State = EstimateState.Variable });
        results.Add(new { scenario = "estimate-variation-visible", passed = EstimateMeta.Text.Contains("波动较大") });
        RenderEstimate(estimated with { State = EstimateState.Calibrating, RemainingTokens = null, Segments = 1, ObservedDrop = 2,
            PendingDrop = 1.25, ProgressReason = "正在累计有效变化", LastResetReason = null, LastResetAt = null, RecoverySamples = 0, RecoveryRequired = 0 });
        results.Add(new { scenario = "estimate-insufficient-no-number", passed = EstimateText.Text == "Token · 校准中"
            && EstimateMeta.Text.Contains("已保留 1 段") && EstimateProgress.Text.Contains("3 段 / 6 个百分点")
            && !EstimatePanelValue.Text.Contains("0M") });
        results.Add(new { scenario = "estimate-pending-segment-visible", passed = EstimateProgress.Text.Contains("1.25 / 2")
            && EstimateReason.Text == "正在累计有效变化" && !EstimateText.Text.Contains("1.25") });
        RenderEstimate(estimated with { State = EstimateState.Incomplete, PendingDrop = 1.5, RecoverySamples = 0, RecoveryRequired = 2,
            ProgressReason = "额度与 Token 时间未对齐", LastResetReason = "休眠恢复", LastResetAt = now });
        results.Add(new { scenario = "estimate-incomplete-hides-projection", passed = EstimateText.Text == "Token · 数据不全"
            && !EstimatePanelValue.Text.Contains("≈") });
        results.Add(new { scenario = "estimate-paused-retains-history-and-reason", passed = EstimateMeta.Text.Contains("已保留 3 段")
            && EstimateMeta.Text.Contains("下降 6 个百分点") && EstimateProgress.Text.Contains("暂停")
            && EstimateProgress.Text.Contains("累计下降 1.5") && EstimateProgress.Text.Contains("恢复 0/2 次健康采样")
            && EstimateReason.Text.Contains("时间未对齐") && EstimateResetReason.Text.Contains("休眠恢复") });
        var pausedByLedger = ApplyUsageAlignment(estimated with { PendingDrop = 1.5 },
            new UsageLedgerSnapshot(now, "ui-session", new TokenCounts(0, 0, 0, 0, 0, 0), SampleHealth.Partial, 4, "fixture-model"),
            "ui-session");
        results.Add(new { scenario = "estimate-ledger-pause-retains-pending", passed = pausedByLedger.PendingDrop == 1.5
            && pausedByLedger.Segments == estimated.Segments && pausedByLedger.ObservedDrop == estimated.ObservedDrop
            && pausedByLedger.State == EstimateState.Incomplete && pausedByLedger.RemainingTokens == null
            && pausedByLedger.RecoverySamples == 0 && pausedByLedger.RecoveryRequired == 2 });
        var realigning = ApplyUsageAlignment(estimated with { PendingDrop = 1.5 },
            new UsageLedgerSnapshot(now, "ui-restarted-session", new TokenCounts(0, 0, 0, 0, 0, 0), SampleHealth.Fresh, 4, "fixture-model"),
            "ui-session");
        RenderEstimate(realigning);
        results.Add(new { scenario = "estimate-epoch-change-retains-history", passed = realigning.Segments == estimated.Segments
            && realigning.ObservedDrop == estimated.ObservedDrop && realigning.From == estimated.From
            && realigning.PendingDrop == 1.5 && realigning.RemainingTokens == null && realigning.RecoveryRequired == 2 && realigning.RecoverySamples == 0
            && EstimateMeta.Text.Contains("已保留 3 段") && EstimateReason.Text.Contains("连续健康配对")
            && EstimateText.Text == "Token · 恢复中" && !EstimateProgress.Text.Contains("首次校准") });
        var recoveringEstimate = estimated with { State = EstimateState.Calibrating, RemainingTokens = null, LowerTokens = null, UpperTokens = null,
            PendingDrop = 1, RecoverySamples = 1, RecoveryRequired = 2,
            ProgressReason = "采样已重新对齐，等待第二次健康采样", LastResetReason = "额度采样中断", LastResetAt = now,
            Diagnostics = [new QuotaCalibrationEvent(now, "quarantined", "Token 未同步变化，已隔离当前段", 84, 82, now.AddHours(1), now.AddHours(1))] };
        RenderEstimate(recoveringEstimate);
        results.Add(new { scenario = "estimate-recovery-needs-healthy-samples", passed = EstimateMeta.Text.Contains("已保留 3 段")
            && EstimateMeta.Text.Contains("下降 6 个百分点") && EstimateProgress.Text.Contains("恢复 1/2 次健康采样")
            && EstimateProgress.Text.Contains("累计下降 1 个百分点") && !EstimateProgress.Text.Contains("首次校准")
            && EstimateReason.Text.Contains("等待第二次健康采样") && EstimateResetReason.Text.Contains("额度采样中断")
            && EstimatePanelValue.Text == "恢复中" && !EstimateText.Text.Contains("≈") });
        results.Add(new { scenario = "estimate-diagnostics-tooltip-only", passed = EstimateCard.ToolTip is string diagnosticHint
            && diagnosticHint.Contains("已隔离当前段") && diagnosticHint.Contains("84% → 82%")
            && !EstimateMeta.Text.Contains("隔离") && !EstimateProgress.Text.Contains("隔离") && !EstimateText.Text.Contains("隔离") });
        UpdateLayout();
        var reasonBottom = EstimateResetReason.TranslatePoint(new Point(0, EstimateResetReason.ActualHeight), EstimateCard).Y;
        results.Add(new { scenario = "estimate-realignment-reason-visible", passed = EstimateResetReason.IsVisible
            && EstimateResetReason.ActualHeight >= 9 && reasonBottom <= EstimateCard.ActualHeight,
            reasonHeight = EstimateResetReason.ActualHeight, reasonBottom, cardHeight = EstimateCard.ActualHeight });
        Capture(Path.Combine(directory, "estimate-realignment-fixture.png"));
        RenderEstimate(estimated with { RecoverySamples = 1, RecoveryRequired = 2 });
        results.Add(new { scenario = "estimate-recovery-hides-old-projection", passed = EstimateText.Text == "Token · 恢复中"
            && !EstimatePanelValue.Text.Contains("≈") && !EstimateProgress.Text.Contains("首次校准") });
        RenderEstimate(estimated with { RecoverySamples = 2, RecoveryRequired = 0, PendingDrop = 1 });
        results.Add(new { scenario = "estimate-recovery-resumes-without-extra-drop", passed = EstimateText.Text.Contains("≈ 8.4M")
            && EstimatePanelValue.Text == "≈ 8.4M" && EstimateProgress.Text.Contains("当前段下降 1 / 2")
            && !EstimateProgress.Text.Contains("恢复") && !EstimateProgress.Text.Contains("首次校准") });
        RenderEstimate(estimator.Get(window, now.AddSeconds(151)));
        results.Add(new { scenario = "estimate-expires-without-snapshot", passed = EstimateText.Text == "Token · 已过期" });
        RenderEstimate(estimated with { RemainingTokens = 0, LowerTokens = 0, UpperTokens = 0 });
        results.Add(new { scenario = "estimate-real-zero-visible", passed = EstimatePanelValue.Text == "≈ 0" });
        _quotaData = originalQuota; _settings.SelectedQuotaKey = originalSelection; UpdateQuota();
        OpenPanel("tasks");
        results.Add(new { scenario = "compact-panels-preserve-hints", passed = TaskStatusText.Text.Length < 65
            && GpuDetail.ToolTip != null && DiskDetail.ToolTip != null && !TaskStatusText.Text.Contains("审批") });
    }
}
