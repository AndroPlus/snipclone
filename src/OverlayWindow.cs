using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SnipClone;

public sealed record CaptureResult(BitmapSource? Image, string? Color);

/// <summary>
/// Full-screen capture overlay: a frozen snapshot of every monitor dimmed by a "smoke" layer, with the
/// current selection shown at full brightness through a clip geometry. Coordinates inside <see cref="_stage"/>
/// are physical screen pixels relative to the virtual-screen origin.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private const double Smoke = 0.55;
    private static readonly SolidColorBrush Accent = new(Color.FromRgb(0x4C, 0xC2, 0xFF));

    private readonly ScreenSnapshot _snap;
    private readonly List<WindowInfo> _windows;
    private readonly string _colorFormat;
    private readonly TaskCompletionSource<CaptureResult> _result = new();
    private SnipMode _mode;

    private readonly Canvas _stage = new();
    private readonly Image _bright;
    private readonly Rectangle _outline = new() { Stroke = Accent, StrokeThickness = 2, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Polyline _path = new() { Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Border _badge;
    private readonly TextBlock _badgeText = new() { Foreground = Brushes.White, FontSize = 13 };
    private readonly Rectangle _badgeSwatch = new() { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), Stroke = Brushes.White };
    private readonly Canvas _chrome = new();
    private readonly Dictionary<SnipMode, ToggleButton> _modeButtons = new();

    private Point? _start;
    private Int32Rect? _selection;
    private WindowInfo? _hoverWindow;
    private bool _finished;

    public OverlayWindow(ScreenSnapshot snap, List<WindowInfo> windows, SnipMode mode, string colorFormat)
    {
        _snap = snap;
        _windows = windows;
        _mode = mode;
        _colorFormat = colorFormat;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Title = "SnipClone capture";

        _stage.Width = snap.Width;
        _stage.Height = snap.Height;
        _stage.Background = Brushes.Transparent;
        _stage.Children.Add(new Image { Source = snap.Bitmap, Width = snap.Width, Height = snap.Height });
        _stage.Children.Add(new Rectangle { Width = snap.Width, Height = snap.Height, Fill = Brushes.Black, Opacity = Smoke, IsHitTestVisible = false });
        // An empty Clip means "no clip" in WPF, so the bright layer is hidden rather than clipped to nothing.
        _bright = new Image { Source = snap.Bitmap, Width = snap.Width, Height = snap.Height, IsHitTestVisible = false, Visibility = Visibility.Hidden };
        _stage.Children.Add(_bright);
        _stage.Children.Add(_outline);
        _stage.Children.Add(_path);

        var badgeRow = new StackPanel { Orientation = Orientation.Horizontal };
        badgeRow.Children.Add(_badgeSwatch);
        badgeRow.Children.Add(_badgeText);
        _badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 32, 32, 32)), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4), Child = badgeRow, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        _chrome.Children.Add(_badge);
        _chrome.Children.Add(BuildToolbar());

        var root = new Grid();
        root.Children.Add(_stage);
        root.Children.Add(_chrome);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, snap.X, snap.Y, snap.Width, snap.Height, Native.SWP_SHOWWINDOW);
        };
        Loaded += (_, _) => OnLoaded();
        _stage.MouseLeftButtonDown += OnDown;
        _stage.MouseMove += OnMove;
        _stage.MouseLeftButtonUp += OnUp;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish(new CaptureResult(null, null));
            else if (e.Key == Key.Enter && _selection is { } s) Finish(new CaptureResult(Crop(s), null));
        };
        Closed += (_, _) => _result.TrySetResult(new CaptureResult(null, null));
    }

    public Task<CaptureResult> RunAsync()
    {
        Show();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Focus();
        return _result.Task;
    }

    // ------------------------------------------------------------------ layout

    private double _scale = 1;

    private void OnLoaded()
    {
        // 1 stage unit = 1 physical pixel, whatever the monitor scaling is.
        var dpi = VisualTreeHelper.GetDpi(this);
        _scale = dpi.DpiScaleX;
        _stage.LayoutTransform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
        _chrome.UpdateLayout();
        // Centre the mode toolbar on the primary monitor, which starts at (0,0) in screen coordinates.
        var bar = (FrameworkElement)_chrome.Children[1];
        bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double primaryWidth = SystemParameters.PrimaryScreenWidth;  // DIPs
        Canvas.SetLeft(bar, -_snap.X / _scale + primaryWidth / 2 - bar.DesiredSize.Width / 2);
        Canvas.SetTop(bar, -_snap.Y / _scale + 12);
        SetMode(_mode);
    }

    private FrameworkElement BuildToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, glyph, label) in new[]
                 {
                     (SnipMode.Rectangle, "", "Rectangle"), (SnipMode.Window, "", "Window"),
                     (SnipMode.FullScreen, "", "Full screen"), (SnipMode.Freeform, "", "Freeform"),
                     (SnipMode.ColorPicker, "", "Color picker"),
                 })
        {
            var button = new ToggleButton
            {
                Content = IconLabel(glyph, label), Margin = new Thickness(2), Padding = new Thickness(10, 6, 10, 6),
                Cursor = Cursors.Arrow, ToolTip = label,
            };
            button.Click += (_, _) => SetMode(mode);
            _modeButtons[mode] = button;
            panel.Children.Add(button);
        }
        var close = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["IconFont"] },
            Margin = new Thickness(8, 2, 2, 2), Padding = new Thickness(10, 6, 10, 6), Cursor = Cursors.Arrow, ToolTip = "Cancel (Esc)",
        };
        close.Click += (_, _) => Finish(new CaptureResult(null, null));
        panel.Children.Add(close);
        return new Border
        {
            Child = panel, Padding = new Thickness(4), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(240, 40, 40, 40)),
        };
    }

    internal static StackPanel IconLabel(string glyph, string label)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        });
        sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private void SetMode(SnipMode mode)
    {
        _mode = mode;
        foreach (var (m, b) in _modeButtons) b.IsChecked = m == mode;
        ClearSelection();
        if (mode == SnipMode.FullScreen)
        {
            ShowBright(new Int32Rect(0, 0, _snap.Width, _snap.Height));
            Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(120);
                Finish(new CaptureResult(_snap.Bitmap, null));
            });
        }
    }

    // ------------------------------------------------------------------ selection display

    private void ClearSelection()
    {
        _start = null;
        _selection = null;
        _hoverWindow = null;
        _bright.Visibility = Visibility.Hidden;
        _outline.Visibility = Visibility.Collapsed;
        _path.Points.Clear();
        _badge.Visibility = Visibility.Collapsed;
    }

    private void ShowBright(Int32Rect r, Geometry? shape = null)
    {
        _bright.Clip = shape ?? new RectangleGeometry(new Rect(r.X, r.Y, r.Width, r.Height));
        _bright.Visibility = Visibility.Visible;
        if (shape == null)
        {
            Canvas.SetLeft(_outline, r.X);
            Canvas.SetTop(_outline, r.Y);
            _outline.Width = Math.Max(0, r.Width);
            _outline.Height = Math.Max(0, r.Height);
            _outline.Visibility = Visibility.Visible;
        }
    }

    private void ShowBadge(Point stagePoint, string text, Color? swatch = null)
    {
        _badgeText.Text = text;
        _badgeSwatch.Visibility = swatch.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (swatch.HasValue) _badgeSwatch.Fill = new SolidColorBrush(swatch.Value);
        _badge.Visibility = Visibility.Visible;
        Canvas.SetLeft(_badge, stagePoint.X / _scale + 16);
        Canvas.SetTop(_badge, stagePoint.Y / _scale + 20);
    }

    // ------------------------------------------------------------------ mouse

    private Point StagePoint(MouseEventArgs e)
    {
        var p = e.GetPosition(_stage);
        return new Point(Math.Clamp(p.X, 0, _snap.Width), Math.Clamp(p.Y, 0, _snap.Height));
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = StagePoint(e);
        switch (_mode)
        {
            case SnipMode.Window when _start == null:
                var hit = _windows.FirstOrDefault(w =>
                    p.X + _snap.X >= w.Frame.Left && p.X + _snap.X < w.Frame.Right &&
                    p.Y + _snap.Y >= w.Frame.Top && p.Y + _snap.Y < w.Frame.Bottom);
                if (hit == _hoverWindow) return;
                _hoverWindow = hit;
                if (hit == null) { ClearSelection(); return; }
                var r = ClipToSnap(hit.Frame.Left - _snap.X, hit.Frame.Top - _snap.Y, hit.Frame.Width, hit.Frame.Height);
                _selection = r;
                ShowBright(r);
                break;
            case SnipMode.ColorPicker:
                var c = _snap.GetPixel((int)p.X, (int)p.Y);
                ShowBadge(p, ColorText.Format(c, _colorFormat), c);
                break;
            case SnipMode.Rectangle when _start is { } s && e.LeftButton == MouseButtonState.Pressed:
                var rect = ClipToSnap((int)Math.Min(s.X, p.X), (int)Math.Min(s.Y, p.Y),
                    (int)Math.Abs(p.X - s.X), (int)Math.Abs(p.Y - s.Y));
                _selection = rect;
                ShowBright(rect);
                ShowBadge(new Point(rect.X + rect.Width, rect.Y + rect.Height), $"{rect.Width} × {rect.Height}");
                break;
            case SnipMode.Freeform when _start != null && e.LeftButton == MouseButtonState.Pressed:
                _path.Points.Add(p);
                break;
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = StagePoint(e);
        switch (_mode)
        {
            case SnipMode.Window:
                if (_hoverWindow != null) _ = FinishWindowAsync(_hoverWindow);
                return;
            case SnipMode.ColorPicker:
                Finish(new CaptureResult(null, ColorText.Format(_snap.GetPixel((int)p.X, (int)p.Y), _colorFormat)));
                return;
            case SnipMode.Rectangle:
            case SnipMode.Freeform:
                _start = p;
                _path.Points.Clear();
                _path.Points.Add(p);
                _stage.CaptureMouse();
                return;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start == null) return;
        _stage.ReleaseMouseCapture();
        if (_mode == SnipMode.Rectangle && _selection is { Width: > 2, Height: > 2 } s)
            Finish(new CaptureResult(Crop(s), null));
        else if (_mode == SnipMode.Freeform && _path.Points.Count > 2)
            FinishFreeform();
        else
            ClearSelection();
    }

    // ------------------------------------------------------------------ results

    private Int32Rect ClipToSnap(int x, int y, int w, int h)
    {
        int l = Math.Clamp(x, 0, _snap.Width), t = Math.Clamp(y, 0, _snap.Height);
        int r = Math.Clamp(x + w, 0, _snap.Width), b = Math.Clamp(y + h, 0, _snap.Height);
        return new Int32Rect(l, t, r - l, b - t);
    }

    private BitmapSource Crop(Int32Rect r)
    {
        var c = new CroppedBitmap(_snap.Bitmap, r);
        c.Freeze();
        return c;
    }

    private async Task FinishWindowAsync(WindowInfo w)
    {
        Cursor = Cursors.Wait;
        BitmapSource? image = null;
        try { image = await ScreenCapture.CaptureWindowAsync(w); }
        catch { /* fall back to the visible pixels below */ }
        if (image == null && _selection is { Width: > 0, Height: > 0 } s) image = Crop(s);
        Finish(new CaptureResult(image, null));
    }

    private void FinishFreeform()
    {
        var pts = _path.Points;
        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        var box = ClipToSnap((int)minX, (int)minY, (int)Math.Ceiling(maxX - minX) + 1, (int)Math.Ceiling(maxY - minY) + 1);
        if (box.Width < 3 || box.Height < 3) { ClearSelection(); return; }

        var figure = new PathFigure { StartPoint = new Point(pts[0].X - box.X, pts[0].Y - box.Y), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new PolyLineSegment(pts.Skip(1).Select(p => new Point(p.X - box.X, p.Y - box.Y)), true));
        var shape = new PathGeometry(new[] { figure });

        // Outside the drawn shape becomes white: clipboard bitmaps have no transparency.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, box.Width, box.Height));
            dc.PushClip(shape);
            dc.DrawImage(Crop(box), new Rect(0, 0, box.Width, box.Height));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(box.Width, box.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        Finish(new CaptureResult(rtb, null));
    }


    private void Finish(CaptureResult result)
    {
        if (_finished) return;
        _finished = true;
        _result.TrySetResult(result);
        Close();
    }
}

public static class ColorText
{
    public static string Format(Color c, string format)
    {
        switch (format)
        {
            case "RGB":
                return $"rgb({c.R}, {c.G}, {c.B})";
            case "HSL":
                double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0;
                if (max != min)
                {
                    double d = max - min;
                    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                    h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                    h /= 6;
                }
                return $"hsl({Math.Round(h * 360)}, {Math.Round(s * 100)}%, {Math.Round(l * 100)}%)";
            default:
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
    }
}
