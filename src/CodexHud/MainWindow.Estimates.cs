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
        if (estimate.State is EstimateState.Unavailable or EstimateState.Stale) return estimate;
        if (_usageData is { Health: not SampleHealth.Fresh } usage)
            return estimate with { State = EstimateState.Incomplete, RemainingTokens = null,
                LowerTokens = null, UpperTokens = null, Detail = usage.Detail ?? "本机 Token 数据不完整" };
        if (_sampledUsageEpoch != null && _usageData?.Epoch != _sampledUsageEpoch)
            return estimate with { State = EstimateState.Calibrating, RemainingTokens = null,
                LowerTokens = null, UpperTokens = null, Segments = 0, ObservedDrop = 0,
                Detail = "Token 采样已重新建立，等待下一次额度配对" };
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
        EstimateMeta.Text = hasValue
            ? (range == null ? "" : range + " · ") + $"{estimate.Segments} 段采样" + (estimate.State == EstimateState.Variable ? " · 波动较大" : "")
            : estimate.State == EstimateState.Calibrating
                ? $"校准 {Math.Min(estimate.Segments, 3)}/3 · 已下降 {estimate.ObservedDrop:0.#} 个百分点"
                : "等待有效采样";
        var period = estimate.From is { } from && estimate.Through is { } through
            ? $"\n采样 {from.ToLocalTime():MM-dd HH:mm}–{through.ToLocalTime():MM-dd HH:mm}" : "";
        string hint = "按本机近期模型和 Token 结构估算；不包含未观测设备或云端的消耗。"
            + "\n公式：同期新增 Token ÷ 额度下降百分点 × 当前剩余百分比。"
            + "\n范围来自已观察区间的波动，不是统计置信区间。"
            + period + $"\n有效区间 {estimate.Segments} · 额度下降 {estimate.ObservedDrop:0.##} 个百分点"
            + $"\n{estimate.Detail}";
        EstimateText.ToolTip = hint;
        EstimateCard.ToolTip = hint;
    }

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
        RenderEstimate(estimated with { State = EstimateState.Calibrating, RemainingTokens = null, Segments = 1, ObservedDrop = 2 });
        results.Add(new { scenario = "estimate-insufficient-no-number", passed = EstimateText.Text == "Token · 校准中"
            && EstimateMeta.Text.Contains("1/3") && !EstimatePanelValue.Text.Contains("0M") });
        RenderEstimate(estimated with { State = EstimateState.Incomplete });
        results.Add(new { scenario = "estimate-incomplete-hides-projection", passed = EstimateText.Text == "Token · 数据不全" });
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
