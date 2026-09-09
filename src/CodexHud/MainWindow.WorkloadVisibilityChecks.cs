using System.Windows;
namespace CodexHud;
public partial class MainWindow
{
    private async Task RunWorkloadVisibilityCheck(List<object> results)
    {
        var before = Volatile.Read(ref _workloadData);
        var rendered = _renderedWorkload;
        Hide();
        await Task.Delay(2200);
        var after = Volatile.Read(ref _workloadData);
        var skippedDrawing = ReferenceEquals(_renderedWorkload, rendered);
        RestoreFromTray();
        UpdateLayout();
        results.Add(new
        {
            scenario = "workload-hidden-keeps-sampling-without-drawing",
            passed = before != null && after != null && after.ObservedAt > before.ObservedAt
                && skippedDrawing && ReferenceEquals(_renderedWorkload, after) && IsVisible,
            before = before?.ObservedAt, after = after?.ObservedAt, skippedDrawing
        });
    }
}
