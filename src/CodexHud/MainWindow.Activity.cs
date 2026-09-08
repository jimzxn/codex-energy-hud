using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexHud.Core;

namespace CodexHud;

public partial class MainWindow
{
    private readonly TaskNoticeTracker _noticeTracker = new();
    private TaskToastWindow? _taskToast;
    private TaskNotice? _recentTerminal;
    private DateTimeOffset _recentTerminalUntil;
    private int _recentTerminalCount;
    private Color? _signalColor;
    private static readonly SolidColorBrush RunningBrush = FrozenBrush(83, 184, 255);
    private static readonly SolidColorBrush CompletedBrush = FrozenBrush(120, 219, 136);
    private static readonly SolidColorBrush ApprovalBrush = FrozenBrush(244, 174, 69);
    private static readonly SolidColorBrush InputBrush = FrozenBrush(196, 154, 255);
    private static readonly SolidColorBrush InterruptedBrush = FrozenBrush(239, 102, 91);
    private static readonly SolidColorBrush UnknownBrush = FrozenBrush(144, 155, 152);

    private static SolidColorBrush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
    private static SolidColorBrush StateBrush(ActivityState state) => state switch
    {
        ActivityState.ExecutionEvidence => RunningBrush,
        ActivityState.Completed => CompletedBrush,
        ActivityState.AwaitingApproval => ApprovalBrush,
        ActivityState.AwaitingInput => InputBrush,
        ActivityState.Interrupted => InterruptedBrush,
        _ => UnknownBrush
    };

    private void ApplyStatusAccent(ActivityState state)
    {
        var brush = StateBrush(state);
        if (_signalColor == brush.Color) return;
        _signalColor = brush.Color;
        StatusDot.Fill = ActiveNumber.Foreground = ActivityLabel.Foreground = ActivityTopLine.Fill = ActivityCorner.Fill = brush;
        var color = brush.Color;
        ActivityButton.Background = new SolidColorBrush(Color.FromArgb(state == ActivityState.Unconfirmed ? (byte)0 : (byte)18, color.R, color.G, color.B));
    }

    private static TaskNotice[] PrioritizeNotices(IEnumerable<TaskNotice> notices) => notices
        .OrderBy(n => n.State == ActivityState.AwaitingApproval ? 0 : n.State == ActivityState.AwaitingInput ? 1 : n.State == ActivityState.Interrupted ? 2 : 3).ToArray();

    private void HandleTaskNotices(IReadOnlyList<TaskNotice> notices)
    {
        if (notices.Count == 0) return;
        var terminal = notices.Where(n => n.State is ActivityState.Completed or ActivityState.Interrupted).ToArray();
        if (terminal.Length > 0)
        {
            _recentTerminal = terminal.FirstOrDefault(n => n.State == ActivityState.Interrupted) ?? terminal[^1];
            _recentTerminalCount = terminal.Count(n => n.State == _recentTerminal.State);
            _recentTerminalUntil = DateTimeOffset.UtcNow.AddSeconds(60);
        }
        // UI fixtures must never display notifications or play sounds.
        if (_uiCheckDirectory != null || _exiting) return;
        var enabled = PrioritizeNotices(notices.Where(n => n.State is ActivityState.AwaitingApproval or ActivityState.AwaitingInput
            ? _settings.AttentionNotifications : _settings.CompletionNotifications));
        if (enabled.Length == 0) return;
        var first = enabled.FirstOrDefault(n => n.State == ActivityState.AwaitingApproval)
            ?? enabled.FirstOrDefault(n => n.State == ActivityState.AwaitingInput) ?? enabled[0];
        string label = NoticeLabel(first.State);
        string title = enabled.Length == 1 ? label : $"{enabled.Length} 项任务有新状态";
        string body = enabled.Length == 1 ? first.Title
            : string.Join("\n", enabled.Take(2).Select(n => NoticeLabel(n.State) + " · " + n.Title));
        _taskToast?.Close();
        var toast = new TaskToastWindow(this, title, body, StateBrush(first.State).Color, () =>
        {
            RestoreFromTray(); OpenPanel("tasks");
            var row = _taskRows.FirstOrDefault(r => r.Id == first.TaskId);
            if (row != null) { TaskList.SelectedItem = row; TaskList.ScrollIntoView(row); }
        });
        _taskToast = toast;
        toast.Closed += (_, _) => { if (ReferenceEquals(_taskToast, toast)) _taskToast = null; };
        toast.Show();
        if (_settings.NotificationSound)
        {
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        }
    }

    private static string NoticeLabel(ActivityState state) => state switch
    {
        ActivityState.AwaitingApproval => "需要审批",
        ActivityState.AwaitingInput => "等待你的输入",
        ActivityState.Interrupted => "任务已中断",
        _ => "任务已完成"
    };

    private void UpdateAttentionSummary(ActivitySnapshot snapshot, DateTimeOffset now, int active)
    {
        if (_recentTerminal is { } previous && snapshot.Tasks.Any(t => t.Id == previous.TaskId
            && CurrentTaskState(t, snapshot, now) is ActivityState.ExecutionEvidence or ActivityState.AwaitingApproval or ActivityState.AwaitingInput))
            _recentTerminal = null;
        int approvals = snapshot.Tasks.Count(t => CurrentTaskState(t, snapshot, now) == ActivityState.AwaitingApproval);
        int inputs = snapshot.Tasks.Count(t => CurrentTaskState(t, snapshot, now) == ActivityState.AwaitingInput);
        if (approvals + inputs > 0)
        {
            var state = approvals > 0 ? ActivityState.AwaitingApproval : ActivityState.AwaitingInput;
            ApplyStatusAccent(state);
            ActiveNumber.Text = (approvals > 0 ? approvals : inputs).ToString("00");
            ActivityLabel.Text = approvals > 0 ? "需要审批" : "等待输入";
            ActivityHint.Text = approvals > 0 && inputs > 0 ? $"输入 {inputs} · 执行 {active}" : $"{active} 项执行中";
            ActivityButton.ToolTip = "点击查看需要处理的任务；审批与输入仍在 Codex 中完成。";
        }
        else if (_recentTerminal is { } notice && now < _recentTerminalUntil
            && snapshot.Health is SampleHealth.Fresh or SampleHealth.Partial
            && snapshot.ObservedAt <= now && now - snapshot.ObservedAt <= TimeSpan.FromSeconds(120))
        {
            ApplyStatusAccent(notice.State);
            ActiveNumber.Text = _recentTerminalCount.ToString("00");
            ActivityLabel.Text = notice.State == ActivityState.Interrupted ? "已中断" : "已完成";
            ActivityHint.Text = active > 0 ? $"{active} 项仍在执行" : "最近任务";
            ActivityButton.ToolTip = NoticeLabel(notice.State) + " · " + notice.Title + "\n点击查看任务明细。";
        }
        else
        {
            ActivityButton.ToolTip = "点击展开任务明细；再次点击收起。";
            if (snapshot.AppPresent && snapshot.Health is SampleHealth.Fresh or SampleHealth.Partial
                && snapshot.ObservedAt <= now && now - snapshot.ObservedAt <= TimeSpan.FromSeconds(120)
                && snapshot.LiveTaskCount > 0 && snapshot.LiveHealth is SampleHealth.Fresh or SampleHealth.Partial)
            {
                int unknown = snapshot.Tasks.Count(t => CurrentTaskState(t, snapshot, now) == ActivityState.Unconfirmed);
                ActivityHint.Text = unknown > 0 ? $"{unknown} 项待确认" : "桌面实时";
            }
        }
        if (_tray != null) _tray.Text = $"Codex HUD · {ActivityLabel.Text} {ActiveNumber.Text}";
    }

    private void ChangeNotifications(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.AttentionNotifications = AttentionNotifyCheck.IsChecked == true;
        _settings.CompletionNotifications = CompletionNotifyCheck.IsChecked == true;
        _settings.NotificationSound = NotificationSoundCheck.IsChecked == true;
        // The tracker continues consuming events while a setting is off.
        SaveSettings();
    }
}
