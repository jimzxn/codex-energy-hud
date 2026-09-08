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
        var estimate = _tokenEstimator.Get(selected, DateTimeOffset.UtcNow);
        return ApplyUsageAlignment(estimate, _usageData, _sampledUsageEpoch);
    }

    private static QuotaTokenEstimate ApplyUsageAlignment(QuotaTokenEstimate estimate,
        UsageLedgerSnapshot? usage, string? sampledEpoch)
    {
        if (estimate.State is EstimateState.Unavailable or EstimateState.Stale) return estimate;
        if (usage is { Health: not SampleHealth.Fresh })
            return estimate with { State = EstimateState.Incomplete, RemainingTokens = null,
                LowerTokens = null, UpperTokens = null, PendingDrop = 0,
                ProgressReason = usage.Detail ?? "本机 Token 采样暂停，等待重新对齐",
                Detail = usage.Detail ?? "本机 Token 数据不完整" };
        if (sampledEpoch != null && usage?.Epoch != sampledEpoch)
            return estimate with { State = EstimateState.Calibrating, RemainingTokens = null,
                LowerTokens = null, UpperTokens = null, PendingDrop = 0,
                ProgressReason = "Token 采样已重建，等待下一次同步配对",
                Detail = "Token 采样已重新建立，等待同步配对和新的有效段" };
        return estimate;
    }

    private void UpdateEstimate()
    {
        if (!_ready) return;
        RenderEstimate(CurrentEstimate());
    }

    private void RenderEstimate(QuotaTokenEstimate estimate)
    {
        bool hasValue = estimate.State is EstimateState.Estimated or EstimateState.Variable
            && estimate.RemainingTokens is >= 0 && double.IsFinite(estimate.RemainingTokens.Value);
        string status = estimate.State switch
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
        EstimateProgress.Text = estimate.State switch
        {
            EstimateState.Stale or EstimateState.Incomplete => "当前段暂停 · 等待重新配对",
            EstimateState.Unavailable => "等待可用额度和 Token 采样",
            _ => $"当前段下降 {FormatEstimateProgress(estimate.PendingDrop)} / 2 个百分点"
                + (hasValue ? "" : " · 校准需 3 段 / 6 个百分点")
        };
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
            + $"\n{estimate.Detail}";
        EstimateText.ToolTip = hint;
        EstimateCard.ToolTip = hint;
    }

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
        var estimator = new QuotaTokenEstimator();
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
            PendingDrop = 1.25, ProgressReason = "正在累计有效变化", LastResetReason = null, LastResetAt = null });
        results.Add(new { scenario = "estimate-insufficient-no-number", passed = EstimateText.Text == "Token · 校准中"
            && EstimateMeta.Text.Contains("已保留 1 段") && EstimateProgress.Text.Contains("3 段 / 6 个百分点")
            && !EstimatePanelValue.Text.Contains("0M") });
        results.Add(new { scenario = "estimate-pending-segment-visible", passed = EstimateProgress.Text.Contains("1.25 / 2")
            && EstimateReason.Text == "正在累计有效变化" && !EstimateText.Text.Contains("1.25") });
        RenderEstimate(estimated with { State = EstimateState.Incomplete, PendingDrop = 0,
            ProgressReason = "额度与 Token 时间未对齐", LastResetReason = "休眠恢复", LastResetAt = now });
        results.Add(new { scenario = "estimate-incomplete-hides-projection", passed = EstimateText.Text == "Token · 数据不全"
            && !EstimatePanelValue.Text.Contains("≈") });
        results.Add(new { scenario = "estimate-paused-retains-history-and-reason", passed = EstimateMeta.Text.Contains("已保留 3 段")
            && EstimateMeta.Text.Contains("下降 6 个百分点") && EstimateProgress.Text.Contains("暂停")
            && EstimateReason.Text.Contains("时间未对齐") && EstimateResetReason.Text.Contains("休眠恢复") });
        var realigning = ApplyUsageAlignment(estimated with { PendingDrop = 1.5 },
            new UsageLedgerSnapshot(now, "ui-restarted-session", new TokenCounts(0, 0, 0, 0, 0, 0), SampleHealth.Fresh, 4, "fixture-model"),
            "ui-session");
        RenderEstimate(realigning);
        results.Add(new { scenario = "estimate-epoch-change-retains-history", passed = realigning.Segments == estimated.Segments
            && realigning.ObservedDrop == estimated.ObservedDrop && realigning.From == estimated.From
            && realigning.PendingDrop == 0 && realigning.RemainingTokens == null
            && EstimateMeta.Text.Contains("已保留 3 段") && EstimateReason.Text.Contains("下一次同步配对")
            && EstimateText.Text == "Token · 校准中" });
        RenderEstimate(estimated with { State = EstimateState.Calibrating, RemainingTokens = null, LowerTokens = null, UpperTokens = null,
            PendingDrop = 1, ProgressReason = "采样已重新对齐，等待一段新的有效变化", LastResetReason = "额度采样中断", LastResetAt = now });
        results.Add(new { scenario = "estimate-restored-history-needs-confirmation", passed = EstimateMeta.Text.Contains("已保留 3 段")
            && EstimateMeta.Text.Contains("下降 6 个百分点") && EstimateProgress.Text.Contains("1 / 2")
            && EstimateReason.Text.Contains("等待一段新的有效变化") && EstimateResetReason.Text.Contains("额度采样中断")
            && EstimatePanelValue.Text == "校准中" && !EstimateText.Text.Contains("≈") });
        UpdateLayout();
        var reasonBottom = EstimateResetReason.TranslatePoint(new Point(0, EstimateResetReason.ActualHeight), EstimateCard).Y;
        results.Add(new { scenario = "estimate-realignment-reason-visible", passed = EstimateResetReason.IsVisible
            && EstimateResetReason.ActualHeight >= 9 && reasonBottom <= EstimateCard.ActualHeight,
            reasonHeight = EstimateResetReason.ActualHeight, reasonBottom, cardHeight = EstimateCard.ActualHeight });
        Capture(Path.Combine(directory, "estimate-realignment-fixture.png"));
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
