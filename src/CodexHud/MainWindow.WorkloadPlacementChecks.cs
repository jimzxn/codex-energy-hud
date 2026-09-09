using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace CodexHud;

public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WorkloadPlacementRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWorkloadPlacementRect(IntPtr window, out WorkloadPlacementRect rect);

    private void RunWorkloadPlacementChecks(List<object> results)
    {
        var originalX = _settings.X;
        var originalY = _settings.Y;
        var originalScale = _settings.Scale;
        var originalDetails = Details.Visibility;
        var originalPanel = _openPanel;
        var originalScrollOffset = DetailsScroll.VerticalOffset;
        try
        {
            _settings.Scale = 1;
            Details.Visibility = Visibility.Collapsed;
            _openPanel = null;
            ApplyAppearance();
            results.Add(new
            {
                scenario = "workload-placement-default-size", width = ActualWidth, height = ActualHeight,
                passed = Math.Abs(ActualWidth - 654) < 2 && Math.Abs(ActualHeight - 134) < 2
            });

            var handle = new WindowInteropHelper(this).Handle;
            var screenIndex = 0;
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var area = screen.WorkingArea;
                // Enter the target monitor first so the edge check uses its current DPI.
                _settings.X = area.Left + Math.Min(24, Math.Max(0, area.Width - 1));
                _settings.Y = area.Top + Math.Min(24, Math.Max(0, area.Height - 1));
                WindowPlacementService.Restore(this, _settings);
                UpdateLayout();

                var requestedX = area.Right - 1;
                var requestedY = area.Bottom - 1;
                _settings.X = requestedX;
                _settings.Y = requestedY;
                WindowPlacementService.Restore(this, _settings);
                UpdateLayout();

                var scenario = $"workload-placement-full-bounds-{screenIndex++}";
                if (handle == IntPtr.Zero || !GetWorkloadPlacementRect(handle, out var rect))
                {
                    results.Add(new { scenario, passed = false, reason = "physical_window_bounds_unavailable" });
                    continue;
                }

                var windowWidth = rect.Right - rect.Left;
                var windowHeight = rect.Bottom - rect.Top;
                var bounds = new { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom };
                var workingArea = new { left = area.Left, top = area.Top, right = area.Right, bottom = area.Bottom };
                if (windowWidth > area.Width || windowHeight > area.Height)
                {
                    results.Add(new
                    {
                        scenario, status = "unsupported_fit", supported = false,
                        reason = "display_work_area_smaller_than_default_hud", requestedX, requestedY,
                        windowWidth, windowHeight, bounds, workingArea
                    });
                    continue;
                }

                results.Add(new
                {
                    scenario, status = "checked", supported = true, requestedX, requestedY,
                    windowWidth, windowHeight, bounds, workingArea,
                    passed = windowWidth > 0 && windowHeight > 0
                        && rect.Left >= area.Left && rect.Top >= area.Top
                        && rect.Right <= area.Right && rect.Bottom <= area.Bottom
                        && Math.Abs(ActualWidth - 654) < 2 && Math.Abs(ActualHeight - 134) < 2
                });
            }
        }
        finally
        {
            _settings.X = originalX;
            _settings.Y = originalY;
            _settings.Scale = originalScale;
            Details.Visibility = originalDetails;
            _openPanel = originalPanel;
            ApplyAppearance();
            WindowPlacementService.Restore(this, _settings);
            DetailsScroll.ScrollToVerticalOffset(originalScrollOffset);
            // LocationChanged can remember a clamped position during restoration.
            _settings.X = originalX;
            _settings.Y = originalY;
        }
    }
}
