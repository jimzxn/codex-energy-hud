using CodexHud.Core;

namespace CodexHud.Mac;

internal sealed partial class MainWindow
{
    private void UpdateResetCredits(DateTimeOffset now)
    {
        var credits = _controller.Quota?.ResetCredits ?? ResetCreditSample.Missing;
        bool available = credits.AvailableCount is >= 0;
        bool stale = credits.Health is SampleHealth.Stale or SampleHealth.Unavailable
            || credits.ObservedAt is not { } observed || observed > now || now - observed > TimeSpan.FromSeconds(150);
        _resetCredits.Text = "重置卡  " + (available ? $"{credits.AvailableCount} 张" : "—")
            + (available && stale ? " · 已过期" : credits.Health == SampleHealth.Partial ? " · 部分数据" : "");
        _resetCredits.Opacity = available && stale ? .45 : 1;
        var expiries = credits.Credits.Where(c => c.ExpiresAt.HasValue).Select(c => c.ExpiresAt!.Value).Order().ToArray();
        Hint(_resetCredits, "账户返回的可用重置卡数量；仅显示，不兑换重置卡。\n"
            + (credits.ObservedAt is { } at ? $"采集 {at.ToLocalTime():MM-dd HH:mm:ss}" : "尚未取得有效读数")
            + (expiries.Length > 0 ? $"\n最早到期 {expiries[0].ToLocalTime():yyyy-MM-dd HH:mm}" : "")
            + (available && stale ? "\n显示上次读数，等待新查询确认。" : ""));
    }
}
