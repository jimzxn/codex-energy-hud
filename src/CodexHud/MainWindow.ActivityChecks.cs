using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexHud.Core;
using CodexHud.Controls;

namespace CodexHud;

public partial class MainWindow
{
    private void RunTaskStatusUiChecks(List<object> results, string directory)
    {
        var original = _activityData;
        var originalNotice = _recentTerminal;
        var originalUntil = _recentTerminalUntil;
        var originalCount = _recentTerminalCount;
        var originalScale = _settings.Scale;
        var originalValue = Ring.Value;
        var originalStale = Ring.IsStale;
        var now = DateTimeOffset.UtcNow;
        var fixtures = new[]
        {
            new TaskActivity("approval-fixture", "部署脚本 · 等待确认权限", ActivityState.AwaitingApproval, now, now.AddMinutes(-3), "界面测试")
                { TurnId = "approval-turn", AttentionId = "approval-1" },
            new TaskActivity("input-fixture", "页面方案 · 等待选择样式", ActivityState.AwaitingInput, now, now.AddMinutes(-2), "界面测试")
                { TurnId = "input-turn", AttentionId = "input-1" },
            new TaskActivity("running-fixture", "单元测试 · 执行中", ActivityState.ExecutionEvidence, now, now.AddMinutes(-1), "界面测试")
                { TurnId = "running-turn" },
            new TaskActivity("completed-fixture", "磁盘监控 · 验证完成", ActivityState.Completed, now, now.AddMinutes(-5), "界面测试", now)
                { TurnId = "completed-turn" },
            new TaskActivity("interrupted-fixture", "旧方案 · 已中断", ActivityState.Interrupted, now, now.AddMinutes(-4), "界面测试", now)
                { TurnId = "interrupted-turn" }
        };
        try
        {
            var orderedNotices = PrioritizeNotices(new[] { new TaskNotice("a", "完成1", ActivityState.Completed, "a"),
                new TaskNotice("b", "完成2", ActivityState.Completed, "b"), new TaskNotice("c", "审批", ActivityState.AwaitingApproval, "c") });
            results.Add(new { scenario = "mixed-notice-batch-prioritizes-action", passed = orderedNotices[0].TaskId == "c" });
            var snapshot = new ActivitySnapshot(now, true, fixtures, SampleHealth.Partial)
                { LiveHealth = SampleHealth.Fresh, LiveTaskCount = fixtures.Length };
            _activityData = snapshot;
            _recentTerminal = null;
            UpdateActivity(); OpenPanel("tasks");
            results.Add(new { scenario = "approval-priority-and-row-colors", passed = ActivityLabel.Text == "需要审批"
                && ActiveNumber.Text == "01" && ReferenceEquals(StatusDot.Fill, ApprovalBrush)
                && _taskRows[0].StatusText == "等待审批" && ReferenceEquals(_taskRows[0].StatusBrush, ApprovalBrush)
                && ReferenceEquals(_taskRows[3].StatusBrush, CompletedBrush) && ReferenceEquals(_taskRows[4].StatusBrush, InterruptedBrush)
                && _taskRows[0].DurationText.StartsWith("本轮已用") });
            CollapseDetails(this, new RoutedEventArgs());
            Capture(Path.Combine(directory, "status-approval.png"));
            foreach (var scale in new[] { 1d, 1.5d, 2d })
            {
                _settings.Scale = scale; ApplyAppearance(); OpenPanel("tasks"); UpdateLayout();
                Capture(Path.Combine(directory, $"status-tasks-{scale * 100:0}.png"));
                results.Add(new { scenario = $"status-tasks-scale-{scale}", passed = Math.Abs(ActualWidth - 560 * scale) < 2
                    && ActualHeight <= WindowPlacementService.WorkingHeightDip(this, _settings)
                    && _taskRows.All(r => !string.IsNullOrWhiteSpace(r.StatusText)) });
            }
            _settings.Scale = 1; ApplyAppearance();
            _activityData = snapshot with { Tasks = fixtures.Skip(1).ToArray() };
            UpdateActivitySummary(_activityData, now);
            results.Add(new { scenario = "input-wait-summary", passed = ActivityLabel.Text == "等待输入"
                && ReferenceEquals(StatusDot.Fill, InputBrush) });
            _activityData = snapshot with { Tasks = fixtures.Skip(2).ToArray() };
            HandleTaskNotices([new TaskNotice("completed-fixture", fixtures[3].Title, ActivityState.Completed, "completed-check")]);
            UpdateActivitySummary(_activityData, now);
            results.Add(new { scenario = "completion-banner-with-parallel-running", passed = ActivityLabel.Text == "已完成"
                && ActivityHint.Text == "1 项仍在执行" && ReferenceEquals(StatusDot.Fill, CompletedBrush) });
            CollapseDetails(this, new RoutedEventArgs());
            Capture(Path.Combine(directory, "status-completed.png"));
            _recentTerminalUntil = now.AddSeconds(-1);
            UpdateActivitySummary(_activityData, now);
            results.Add(new { scenario = "completion-banner-expires", passed = ActivityLabel.Text == "执行迹象"
                && ReferenceEquals(StatusDot.Fill, RunningBrush) });
            _recentTerminal = new TaskNotice("completed-fixture", fixtures[3].Title, ActivityState.Completed, "restart-check");
            _recentTerminalUntil = now.AddSeconds(60);
            _activityData = snapshot with { Tasks = [fixtures[3] with { State = ActivityState.ExecutionEvidence, StartedAt = now, EndedAt = null, TurnId = "restarted-turn" }] };
            UpdateActivitySummary(_activityData, now);
            results.Add(new { scenario = "restarted-task-clears-completion-banner", passed = _recentTerminal == null && ActivityLabel.Text == "执行迹象" });
            _activityData = snapshot with { Health = SampleHealth.Stale };
            UpdateActivitySummary(_activityData, now);
            results.Add(new { scenario = "stale-does-not-claim-approval", passed = ActivityLabel.Text == "状态待确认"
                && ActiveNumber.Text == "—" && ReferenceEquals(StatusDot.Fill, UnknownBrush) && ActivityHint.Text != "桌面实时" });
            _activityData = snapshot;
            UpdateActivitySummary(_activityData, now.AddSeconds(121));
            results.Add(new { scenario = "waiting-expires-without-new-snapshot", passed = ActivityLabel.Text == "状态待确认"
                && ReferenceEquals(StatusDot.Fill, UnknownBrush) });
            foreach (var point in new[] { (Value: 100d, Color: Color.FromRgb(83, 184, 255)),
                (Value: 75d, Color: Color.FromRgb(102, 202, 196)), (Value: 50d, Color: Color.FromRgb(120, 219, 136)),
                (Value: 37.5d, Color: Color.FromRgb(180, 160, 114)), (Value: 25d, Color: Color.FromRgb(239, 102, 91)),
                (Value: 10d, Color: Color.FromRgb(239, 102, 91)) })
            {
                Ring.Value = double.NaN; Ring.Value = point.Value; Ring.IsStale = false;
                Ring.UpdateLayout();
                var target = new RenderTargetBitmap(80, 80, 96, 96, PixelFormats.Pbgra32); target.Render(Ring);
                var drawing = VisualTreeHelper.GetDrawing(Ring);
                var brush = (SolidColorBrush)drawing.Children.OfType<GeometryDrawing>().First().Pen.Brush;
                results.Add(new { scenario = $"ring-color-{point.Value}", passed = brush.Color == point.Color && brush.Opacity == 1 });
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
                using var stream = File.Create(Path.Combine(directory, $"ring-{point.Value:0}.png")); encoder.Save(stream);
            }
            Ring.IsStale = true;
            Ring.UpdateLayout();
            var staleTarget = new RenderTargetBitmap(80, 80, 96, 96, PixelFormats.Pbgra32); staleTarget.Render(Ring);
            var staleBrush = (SolidColorBrush)VisualTreeHelper.GetDrawing(Ring).Children.OfType<GeometryDrawing>().First().Pen.Brush;
            results.Add(new { scenario = "ring-stale-dims-gradient", color = staleBrush.Color.ToString(), opacity = staleBrush.Opacity, passed = staleBrush.Color == Color.FromRgb(239, 102, 91) && staleBrush.Opacity == .45 });
            var attentionOption = _settings.AttentionNotifications;
            var completionOption = _settings.CompletionNotifications;
            var soundOption = _settings.NotificationSound;
            AttentionNotifyCheck.IsChecked = !attentionOption;
            CompletionNotifyCheck.IsChecked = !completionOption;
            NotificationSoundCheck.IsChecked = !soundOption;
            var saved = _store.Load();
            results.Add(new { scenario = "notification-settings-persist", passed = saved.AttentionNotifications == !attentionOption
                && saved.CompletionNotifications == !completionOption && saved.NotificationSound == !soundOption });
            AttentionNotifyCheck.IsChecked = attentionOption;
            CompletionNotifyCheck.IsChecked = completionOption;
            NotificationSoundCheck.IsChecked = soundOption;
            OpenPanel("settings");
            Capture(Path.Combine(directory, "settings-notifications.png"));
        }
        finally
        {
            _activityData = original;
            _recentTerminal = originalNotice; _recentTerminalUntil = originalUntil; _recentTerminalCount = originalCount;
            _settings.Scale = originalScale; ApplyAppearance();
            Ring.Value = double.NaN; Ring.Value = originalValue; Ring.IsStale = originalStale;
            UpdateActivity();
        }
    }
}
