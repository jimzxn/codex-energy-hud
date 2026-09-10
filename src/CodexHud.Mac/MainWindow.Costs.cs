using System.Globalization;
using Avalonia.Controls;
using CodexHud.Core;

namespace CodexHud.Mac;

internal sealed partial class MainWindow
{
    private readonly Dictionary<string, TextBlock> _taskCosts = new(StringComparer.Ordinal);

    private void UpdateSessionCosts()
    {
        if (!_ready || !IsVisible) return;
        foreach (var (id, line) in _taskCosts) RenderSessionCost(line, id);
    }

    // Used by both the local task cards and the independent workload rows.
    private void RenderSessionCost(TextBlock line, string threadId, bool isSubagent = false)
    {
        line.IsVisible = _settings.ShowSessionCost;
        if (!_settings.ShowSessionCost) return;
        var snapshot = _controller.SessionCosts;
        var now = DateTimeOffset.UtcNow;
        var common = "API Token 价格等值估算，不是订阅实际扣款或账单。\n"
            + "会话创建以来的本机记录；主任务包含已确认后代，子代理行显示自身。\n"
            + $"价格表 {ApiPricingCatalog.Version} · 核验 {ApiPricingCatalog.VerifiedOn:yyyy-MM-dd}\n"
            + string.Join("\n", ApiPricingCatalog.Sources);
        if (snapshot is null || !snapshot.Tasks.TryGetValue(threadId, out var estimate))
        {
            line.Text = snapshot?.Health is SampleHealth.Stale or SampleHealth.Unavailable
                ? "累计 — · 暂不可用" : "累计 — · 等待费用记录";
            Hint(line, common + "\n" + snapshot?.Detail);
            return;
        }
        isSubagent |= estimate.IsSubagent;
        var low = isSubagent ? estimate.SelfUsd : estimate.TotalUsd;
        var high = isSubagent ? estimate.SelfUpperUsd : estimate.TotalUpperUsd;
        bool stale = snapshot.Health is SampleHealth.Stale or SampleHealth.Unavailable
            || estimate.Health is SampleHealth.Stale or SampleHealth.Unavailable
            || now < snapshot.ObservedAt || now - snapshot.ObservedAt > TimeSpan.FromSeconds(20);
        bool partial = estimate.Health == SampleHealth.Partial || estimate.UnpricedResponses > 0 || high != low;
        bool hasAmount = estimate.PricedResponses > 0 && (!isSubagent || estimate.DescendantCount == 0)
            || low > 0 || high > 0 || estimate.Health == SampleHealth.Fresh && estimate.UnpricedResponses == 0;
        var amount = !hasAmount ? "—" : estimate.UnpricedResponses > 0 ? "≥ " + SessionUsd(low)
            : "≈ " + SessionCostRange(low, high);
        var state = stale ? " · 已过期" : estimate.Health == SampleHealth.Loading ? " · 历史补齐中" : partial ? " · 部分估算" : "";
        line.Text = $"累计 {amount} · {(isSubagent ? "自身" : "含子代理")}{state}";
        Hint(line, common + $"\n\n自身：{SessionCostRange(estimate.SelfUsd, estimate.SelfUpperUsd)}"
            + $"\n子代理（{estimate.DescendantCount} 项）：{SessionCostRange(estimate.DescendantsUsd, estimate.DescendantsUpperUsd)}"
            + $"\n合计：{SessionCostRange(estimate.TotalUsd, estimate.TotalUpperUsd)}"
            + $"\n已计价响应 {estimate.PricedResponses:N0} · 未计价响应 {estimate.UnpricedResponses:N0} · 未计价 Token {estimate.UnpricedTokens:N0}"
            + $"\n最近用量 {estimate.LastUsageAt?.ToLocalTime():MM-dd HH:mm:ss} · 采集 {snapshot.ObservedAt.ToLocalTime():HH:mm:ss}"
            + "\n" + snapshot.Detail + "\n" + string.Join("\n", estimate.Notes.Distinct(StringComparer.Ordinal))
            + (hasAmount ? "" : "\n尚无可确认的计价金额，缺失不代表零费用。"));
    }

    private static string SessionCostRange(decimal low, decimal high) => high > low
        ? SessionUsd(low) + "–" + SessionUsd(high) : SessionUsd(low);
    private static string SessionUsd(decimal value) => "US$" + value.ToString(value is > 0 and < .0001m
        ? "0.############################" : "#,0.######", CultureInfo.InvariantCulture);
}
