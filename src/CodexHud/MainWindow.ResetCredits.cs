using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CodexHud.Core;

namespace CodexHud;

public partial class MainWindow
{
    private void UpdateResetCredits()
    {
        if (!_ready) return;
        RenderResetCredits(_quotaData?.ResetCredits ?? new ResetCreditSample(null, null, SampleHealth.Unavailable), DateTimeOffset.UtcNow);
    }

    private void RenderResetCredits(ResetCreditSample sample, DateTimeOffset now)
    {
        var hasCount = sample.AvailableCount is >= 0;
        var stale = hasCount && (sample.Health != SampleHealth.Fresh || sample.ObservedAt is not { } observed
            || now - observed > TimeSpan.FromSeconds(150) || observed - now > TimeSpan.FromSeconds(15));
        ResetCreditsValue.Text = hasCount ? sample.AvailableCount!.Value.ToString(CultureInfo.InvariantCulture) + " 张" : "—";
        ResetCreditsValue.Opacity = stale ? .45 : 1;
        ResetCreditsValue.Foreground = hasCount
            ? new SolidColorBrush(Color.FromRgb(184, 197, 144))
            : new SolidColorBrush(Color.FromRgb(144, 155, 152));
        ResetCreditsStatus.Text = stale ? "已过期" : "";
        ResetCreditsStatus.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        ResetCreditsCard.ToolTip = sample.ObservedAt is { } at && hasCount
            ? $"账户重置卡 · 更新 {at.ToLocalTime():MM-dd HH:mm:ss}" + (stale ? " · 已过期" : "")
            : "账户重置卡数量暂不可用";
    }

    private void RunResetCreditUiChecks(List<object> results, string directory)
    {
        var originalQuota = _quotaData;
        var originalSelection = _settings.SelectedQuotaKey;
        var originalScale = _settings.Scale;
        var now = DateTimeOffset.UtcNow;
        try
        {
            RenderResetCredits(new(3, now, SampleHealth.Fresh), now);
            results.Add(new { scenario = "reset-credits-three-visible", passed = ResetCreditsValue.Text == "3 张"
                && ResetCreditsValue.Opacity == 1 && ResetCreditsStatus.Visibility == Visibility.Collapsed });
            RenderResetCredits(new(0, now, SampleHealth.Fresh), now);
            results.Add(new { scenario = "reset-credits-zero-visible", passed = ResetCreditsValue.Text == "0 张"
                && ResetCreditsValue.Opacity == 1 && ResetCreditsStatus.Visibility == Visibility.Collapsed });
            RenderResetCredits(new(null, null, SampleHealth.Unavailable), now);
            results.Add(new { scenario = "reset-credits-missing-is-not-zero", passed = ResetCreditsValue.Text == "—"
                && ResetCreditsStatus.Visibility == Visibility.Collapsed });
            RenderResetCredits(new(-1, now, SampleHealth.Fresh), now);
            results.Add(new { scenario = "reset-credits-invalid-is-missing", passed = ResetCreditsValue.Text == "—" });
            RenderResetCredits(new(3, now.AddSeconds(-151), SampleHealth.Fresh), now);
            results.Add(new { scenario = "reset-credits-age-expires", passed = ResetCreditsValue.Text == "3 张"
                && ResetCreditsValue.Opacity == .45 && ResetCreditsStatus.Text == "已过期"
                && ResetCreditsStatus.Visibility == Visibility.Visible });
            RenderResetCredits(new(3, now, SampleHealth.Stale), now);
            results.Add(new { scenario = "reset-credits-failure-keeps-reading", passed = ResetCreditsValue.Text == "3 张"
                && ResetCreditsValue.Opacity == .45 && ResetCreditsStatus.Text == "已过期" });

            var first = new QuotaWindow("ui-reset-primary", "codex", "测试额度 · 每小时", "primary", 60, 84, now.AddHours(1));
            var second = new QuotaWindow("ui-reset-secondary", "codex", "测试额度 · 每周", "secondary", 10080, 65, now.AddDays(7));
            _quotaData = new QuotaSnapshot(now, [first, second], SampleHealth.Fresh)
            {
                AccountKey = "ui-reset-credits-fixture",
                ResetCredits = new(3, now, SampleHealth.Fresh)
            };
            _settings.SelectedQuotaKey = first.Key;
            UpdateResetCredits();
            var firstValue = ResetCreditsValue.Text;
            _settings.SelectedQuotaKey = second.Key;
            UpdateResetCredits();
            results.Add(new { scenario = "reset-credits-independent-of-quota-selection", passed = firstValue == "3 张"
                && ResetCreditsValue.Text == firstValue });
            _settings.SelectedQuotaKey = first.Key;
            UpdateQuota();
            OpenPanel("quotas");
            foreach (var scale in new[] { 1d, 2d })
            {
                _settings.Scale = scale;
                ApplyAppearance();
                UpdateLayout();
                results.Add(new { scenario = $"reset-credits-panel-scale-{scale}", passed = ResetCreditsCard.ActualWidth >= 460
                    && ResetCreditsCard.ActualHeight <= 40 && ResetCreditsValue.ActualWidth > 0
                    && ResetCreditsValue.Text == "3 张" && ActualHeight <= WindowPlacementService.WorkingHeightDip(this, _settings),
                    cardWidth = ResetCreditsCard.ActualWidth, cardHeight = ResetCreditsCard.ActualHeight });
                Capture(Path.Combine(directory, $"reset-credits-fixture-{scale * 100:0}.png"));
            }
        }
        finally
        {
            _quotaData = originalQuota;
            _settings.SelectedQuotaKey = originalSelection;
            _settings.Scale = originalScale;
            ApplyAppearance();
            UpdateQuota();
            UpdateResetCredits();
            OpenPanel("tasks");
        }
    }
}
