using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CodexHud;

internal sealed class TaskToastWindow : Window
{
    private readonly Window _placementOwner;
    private readonly Action _activated;
    private readonly DispatcherTimer _timeout;
    private readonly Grid _shell;
    private readonly System.Windows.Shapes.Path _outline;
    private HwndSource? _source;
    private bool _closed;
    private bool _positionQueued;
    private bool _activatedOnce;

    public TaskToastWindow(Window owner, string title, string message, Color accent, Action activated)
    {
        _placementOwner = owner;
        _activated = activated;
        Title = "Codex 任务提醒";
        Width = 320;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

        var accentBrush = new SolidColorBrush(accent);
        accentBrush.Freeze();
        _shell = new Grid();
        _outline = new System.Windows.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(248, 24, 31, 27)),
            Stroke = new SolidColorBrush(Color.FromRgb(84, 99, 76)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        _shell.Children.Add(_outline);
        _shell.Children.Add(new Rectangle
        {
            Fill = accentBrush, Width = 52, Height = 2,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 0, 0, 0), IsHitTestVisible = false
        });
        var open = MakeButton("TaskToastOpen", "查看任务");
        open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        open.Padding = new Thickness(16, 14, 16, 12);
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title, Foreground = accentBrush, FontSize = 12, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 24, 8)
        });
        content.Children.Add(new TextBlock
        {
            Text = message, Foreground = new SolidColorBrush(Color.FromRgb(241, 243, 236)), FontSize = 12,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 40
        });
        content.Children.Add(new TextBlock
        {
            Text = "CODEX / 查看任务", Foreground = new SolidColorBrush(Color.FromRgb(144, 155, 152)),
            FontFamily = new FontFamily("Bahnschrift, Microsoft YaHei UI"), FontSize = 8,
            Margin = new Thickness(0, 9, 0, 0)
        });
        open.Content = content;
        open.Click += (_, _) =>
        {
            if (_activatedOnce || _closed) return;
            _activatedOnce = true;
            try { _activated(); }
            finally { Close(); }
        };
        _shell.Children.Add(open);
        var dismiss = MakeButton("TaskToastDismiss", "关闭提醒");
        dismiss.Width = 24;
        dismiss.Height = 24;
        dismiss.HorizontalAlignment = HorizontalAlignment.Right;
        dismiss.VerticalAlignment = VerticalAlignment.Top;
        dismiss.Margin = new Thickness(0, 8, 10, 0);
        dismiss.Content = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 7,7 M 7,0 L 0,7"),
            Stroke = new SolidColorBrush(Color.FromRgb(170, 182, 171)), StrokeThickness = 1.2,
            Width = 8, Height = 8
        };
        dismiss.Click += (_, _) => Close();
        _shell.Children.Add(dismiss);
        _shell.SizeChanged += (_, _) => UpdateOutline();
        Content = _shell;

        _timeout = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(6) };
        _timeout.Tick += Timeout;
        SourceInitialized += InitializeSource;
        Loaded += (_, _) => { PositionNotice(); _timeout.Start(); };
        SizeChanged += (_, _) => QueuePosition();
        // Do not set Window.Owner: hiding the HUD must not hide its notification.
        _placementOwner.Closed += OwnerClosed;
        Closed += (_, _) =>
        {
            _closed = true;
            _timeout.Stop();
            _timeout.Tick -= Timeout;
            _placementOwner.Closed -= OwnerClosed;
            _source?.RemoveHook(WindowMessage);
            _source = null;
        };
    }

    public void ShowNotice()
    {
        if (_closed) return;
        new WindowInteropHelper(this).EnsureHandle();
        PositionNotice();
        Show();
    }

    private static Button MakeButton(string name, string accessibleName)
    {
        var button = new Button
        {
            Name = name, Cursor = Cursors.Hand, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Center
        };
        var border = new FrameworkElementFactory(typeof(Border), "ButtonSurface");
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Button.Background)) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding(nameof(Button.Padding)) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new System.Windows.Data.Binding(nameof(Button.HorizontalContentAlignment)) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(36, 95, 125, 101)), "ButtonSurface"));
        template.Triggers.Add(hover);
        button.Template = template;
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    private void UpdateOutline()
    {
        var width = _shell.ActualWidth;
        var height = _shell.ActualHeight;
        if (width <= 1 || height <= 1) return;
        var cut = Math.Min(11, Math.Min(width, height) / 3);
        var outline = new StreamGeometry();
        using (var context = outline.Open())
        {
            context.BeginFigure(new Point(.5, .5), true, true);
            context.PolyLineTo(new[]
            {
                new Point(width - cut, .5), new Point(width - .5, cut),
                new Point(width - .5, height - .5), new Point(cut, height - .5),
                new Point(.5, height - cut)
            }, true, false);
        }
        outline.Freeze();
        _outline.Data = outline;
        _shell.Clip = outline;
    }

    private void InitializeSource(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessage);
        var extendedStyle = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr(extendedStyle | 0x08000000 | 0x00000080)); // NOACTIVATE | TOOLWINDOW
        PositionNotice();
    }

    private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (message == 0x02E0 || message == 0x007E) QueuePosition(); // DPI or display change
        return IntPtr.Zero;
    }

    private void QueuePosition()
    {
        if (_closed || _positionQueued) return;
        _positionQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _positionQueued = false;
            if (!_closed) PositionNotice();
        }));
    }

    private void PositionNotice()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds)) return;
        var ownerHandle = new WindowInteropHelper(_placementOwner).Handle;
        var work = System.Windows.Forms.Screen.FromHandle(ownerHandle != IntPtr.Zero ? ownerHandle : handle).WorkingArea;
        var dpi = GetDpiForWindow(handle);
        var margin = (int)Math.Round(18 * (dpi == 0 ? 1 : dpi / 96d));
        var x = Math.Max(work.Left, work.Right - (bounds.Right - bounds.Left) - margin);
        var y = Math.Max(work.Top, work.Bottom - (bounds.Bottom - bounds.Top) - margin);
        if (bounds.Left != x || bounds.Top != y)
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE
    }

    private void Timeout(object? sender, EventArgs e) => Close();
    private void OwnerClosed(object? sender, EventArgs e) => Close();

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
