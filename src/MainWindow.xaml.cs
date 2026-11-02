using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace SnipClone;

public enum Tool { Pen, Highlighter, Eraser, Rectangle, Oval, Line, Arrow, Text, Crop }

public sealed record TextItem(string Text, double X, double Y, Color Color, double Size);

/// <summary>Everything an undo step needs: the bitmap underneath, the ink strokes and the text labels.</summary>
public sealed record EditorState(BitmapSource Base, StrokeCollection Strokes, List<TextItem> Texts);

public partial class MainWindow : Window
{
    private static readonly Color[] Palette =
    {
        Colors.Black, Colors.White, Color.FromRgb(232, 17, 35), Color.FromRgb(255, 140, 0), Color.FromRgb(255, 241, 0),
        Color.FromRgb(0, 204, 106), Color.FromRgb(0, 120, 215), Color.FromRgb(136, 23, 152),
    };
    private static readonly (SnipMode Mode, string Label)[] Modes =
    {
        (SnipMode.Rectangle, "Rectangle"), (SnipMode.Window, "Window"), (SnipMode.FullScreen, "Full screen"),
        (SnipMode.Freeform, "Freeform"), (SnipMode.ColorPicker, "Color picker"),
    };
    private static readonly (int Seconds, string Label)[] Delays = { (0, "No delay"), (3, "3 seconds"), (5, "5 seconds"), (10, "10 seconds") };
    private static readonly FontFamily TextFont = new("Segoe UI");

    private readonly Settings _settings = Settings.Load();
    private readonly bool _snipOnStart;
    private readonly Stack<EditorState> _undo = new(), _redo = new();
    private EditorState? _current;
    private bool _dirty, _capturing;
    private Tool _tool = Tool.Pen;
    private Color _color = Palette[2];
    private double _size = 4;
    private Point? _dragStart;
    private Polyline? _preview;
    private Rectangle? _cropRect;
    private Rect? _pendingCrop;
    private string? _hotkey;
    private IntPtr _hwnd;

    public MainWindow(bool snipOnStart = false)
    {
        InitializeComponent();
        _snipOnStart = snipOnStart;

        foreach (var (_, label) in Modes) ModeBox.Items.Add(label);
        ModeBox.SelectedIndex = Array.FindIndex(Modes, m => m.Mode == _settings.Mode);
        foreach (var (_, label) in Delays) DelayBox.Items.Add(label);
        DelayBox.SelectedIndex = Math.Max(0, Array.FindIndex(Delays, d => d.Seconds == _settings.Delay));
        BuildSwatches();

        Ink.StrokeCollected += (_, _) => Commit();
        Ink.StrokeErased += (_, _) => Commit();
        Ink.PreviewMouseLeftButtonDown += Ink_PreviewDown;
        Ink.MouseLeftButtonDown += Ink_Down;
        Ink.MouseMove += Ink_Move;
        Ink.MouseLeftButtonUp += Ink_Up;
        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => RegisterHotkey();
        Loaded += (_, _) => { if (_snipOnStart) StartSnip(); };
        Closing += (_, e) => e.Cancel = !ConfirmDiscard();
        Closed += (_, _) => { if (_hwnd != IntPtr.Zero) Native.UnregisterHotKey(_hwnd, 1); };

        SelectTool(Tool.Pen);
        UpdateUi();
    }

    // ================================================================== capture flow

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    public void StartSnip() => _ = NewSnipAsync();

    private async Task NewSnipAsync()
    {
        if (_capturing || !ConfirmDiscard()) return;
        _capturing = true;
        CommitPendingText();
        Hide();
        try
        {
            var mode = _settings.Mode;
            int delay = mode is SnipMode.FullScreen or SnipMode.ColorPicker ? 0 : _settings.Delay;
            if (delay > 0) await CountdownAsync(delay);
            await Task.Delay(250); // let the compositor remove our window before grabbing the screen

            var windows = WindowList.Visible(new[] { _hwnd });
            var snap = ScreenCapture.CaptureVirtualScreen();
            var result = await new OverlayWindow(snap, windows, mode, _settings.ColorFormat).RunAsync();
            BringToFront();

            if (result.Color != null)
            {
                SetClipboardText(result.Color);
                Status($"Copied color {result.Color} to the clipboard.");
            }
            else if (result.Image != null)
            {
                OnCaptured(result.Image);
            }
            else
            {
                Status("Snip canceled.");
            }
        }
        catch (Exception ex)
        {
            BringToFront();
            Status($"Capture failed: {ex.Message}");
        }
        finally
        {
            _capturing = false;
            if (!IsVisible) BringToFront();
        }
    }

    private static async Task CountdownAsync(int seconds)
    {
        var text = new TextBlock { FontSize = 22, Foreground = Brushes.White, Margin = new Thickness(22, 12, 22, 12) };
        var win = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight, Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
            Content = text, WindowStartupLocation = WindowStartupLocation.Manual, Top = 40,
        };
        win.Left = SystemParameters.PrimaryScreenWidth / 2 - 90;
        win.Show();
        for (int s = seconds; s > 0; s--)
        {
            text.Text = $"Snip in {s}…";
            await Task.Delay(1000);
        }
        win.Close();
    }

    private void OnCaptured(BitmapSource image)
    {
        if (_settings.Outline) image = AddOutline(image);
        LoadImage(image);
        var notes = new List<string> { $"{image.PixelWidth} × {image.PixelHeight}" };
        if (_settings.AutoCopy)
            notes.Add(SetClipboardImage(image) ? "copied to clipboard" : "clipboard busy, not copied");
        if (_settings.AutoSave)
        {
            try
            {
                Directory.CreateDirectory(_settings.SaveFolder);
                var path = System.IO.Path.Combine(_settings.SaveFolder, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
                SaveBitmap(image, path);
                notes.Add($"saved to {path}");
            }
            catch (Exception ex) { notes.Add($"auto-save failed: {ex.Message}"); }
        }
        Status(string.Join(" · ", notes));
    }

    private static BitmapSource AddOutline(BitmapSource image)
    {
        int w = image.PixelWidth + 2, h = image.PixelHeight + 2;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(122, 122, 122)), null, new Rect(0, 0, w, h));
            dc.DrawImage(image, new Rect(1, 1, image.PixelWidth, image.PixelHeight));
        }
        return Render(dv, w, h);
    }

    // ================================================================== editor state and undo

    private void LoadImage(BitmapSource image)
    {
        _undo.Clear();
        _redo.Clear();
        _current = new EditorState(image, new StrokeCollection(), new List<TextItem>());
        Restore(_current);
        _dirty = false;
        SelectTool(Tool.Pen);
        UpdateUi();
    }

    private EditorState CaptureState() => new(
        (BitmapSource)BaseImage.Source, Ink.Strokes.Clone(),
        Ink.Children.OfType<TextBlock>().Select(t => (TextItem)t.Tag).ToList());

    private void Restore(EditorState state)
    {
        BaseImage.Source = state.Base;
        Surface.Width = state.Base.PixelWidth;
        Surface.Height = state.Base.PixelHeight;
        var dpi = VisualTreeHelper.GetDpi(this);
        Surface.LayoutTransform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY); // 1 image px = 1 screen px
        Ink.Strokes = state.Strokes.Clone();
        Ink.Children.Clear();
        foreach (var item in state.Texts) Ink.Children.Add(MakeLabel(item));
        ClearCrop();
    }

    /// <summary>Records the change that just happened as one undo step.</summary>
    private void Commit()
    {
        if (_current == null) return;
        _undo.Push(_current);
        _current = CaptureState();
        _redo.Clear();
        _dirty = true;
        UpdateUi();
    }

    private void Undo()
    {
        CommitPendingText();
        if (_undo.Count == 0 || _current == null) return;
        _redo.Push(_current);
        _current = _undo.Pop();
        Restore(_current);
        _dirty = true;
        UpdateUi();
    }

    private void Redo()
    {
        if (_redo.Count == 0 || _current == null) return;
        _undo.Push(_current);
        _current = _redo.Pop();
        Restore(_current);
        _dirty = true;
        UpdateUi();
    }

    /// <summary>The image with all annotations burned in, at full resolution.</summary>
    private BitmapSource Flatten()
    {
        var b = (BitmapSource)BaseImage.Source;
        int w = b.PixelWidth, h = b.PixelHeight;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(b, new Rect(0, 0, w, h));
            Ink.Strokes.Draw(dc);
            foreach (var t in Ink.Children.OfType<TextBlock>().Select(x => (TextItem)x.Tag))
            {
                var ft = new FormattedText(t.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), t.Size,
                    new SolidColorBrush(t.Color), 1.0);
                dc.DrawText(ft, new Point(t.X, t.Y));
            }
        }
        return Render(dv, w, h);
    }

    private static BitmapSource Render(Visual visual, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    // ================================================================== tools and mouse

    private void SelectTool(Tool tool)
    {
        CommitPendingText();
        if (tool != Tool.Crop) ClearCrop();
        _tool = tool;
        foreach (var b in ToolRow.Children.OfType<ToggleButton>())
            b.IsChecked = (string)b.Tag == tool.ToString();
        ApplyDrawingAttributes();
        Ink.EditingMode = tool switch
        {
            Tool.Pen or Tool.Highlighter => InkCanvasEditingMode.Ink,
            Tool.Eraser => InkCanvasEditingMode.EraseByStroke,
            _ => InkCanvasEditingMode.None,
        };
        Ink.Cursor = tool switch { Tool.Text => Cursors.IBeam, Tool.Pen or Tool.Highlighter or Tool.Eraser => null, _ => Cursors.Cross };
        CropBar.Visibility = tool == Tool.Crop && _current != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyDrawingAttributes()
    {
        bool hl = _tool == Tool.Highlighter;
        Ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _color, Width = hl ? _size * 3 : _size, Height = hl ? _size * 3 : _size,
            IsHighlighter = hl, StylusTip = hl ? StylusTip.Rectangle : StylusTip.Ellipse,
            FitToCurve = !hl, IgnorePressure = true,
        };
    }

    private void Ink_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (_tool != Tool.Eraser) return;
        // The ink eraser only knows strokes; text labels are erased by clicking them.
        var p = e.GetPosition(Ink);
        var label = Ink.Children.OfType<TextBlock>().LastOrDefault(t =>
            new Rect(InkCanvas.GetLeft(t), InkCanvas.GetTop(t), t.ActualWidth, t.ActualHeight).Contains(p));
        if (label == null) return;
        Ink.Children.Remove(label);
        Commit();
        e.Handled = true;
    }

    private void Ink_Down(object sender, MouseButtonEventArgs e)
    {
        if (_current == null) return;
        var p = e.GetPosition(Ink);
        switch (_tool)
        {
            case Tool.Text:
                PlaceTextBox(p);
                e.Handled = true;
                return;
            case Tool.Rectangle or Tool.Oval or Tool.Line or Tool.Arrow or Tool.Crop:
                _dragStart = p;
                Ink.CaptureMouse();
                e.Handled = true;
                return;
        }
    }

    private void Ink_Move(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } s || e.LeftButton != MouseButtonState.Pressed) return;
        var p = ClampToImage(e.GetPosition(Ink));
        if (_tool == Tool.Crop)
        {
            var r = new Rect(s, p);
            _cropRect ??= AddAdorner(new Rectangle
            {
                Stroke = Brushes.White, StrokeThickness = 2 / ScaleFactor(), StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            });
            Canvas.SetLeft(_cropRect, r.X);
            Canvas.SetTop(_cropRect, r.Y);
            _cropRect.Width = r.Width;
            _cropRect.Height = r.Height;
            _pendingCrop = r;
            return;
        }
        _preview ??= AddAdorner(new Polyline
        {
            Stroke = new SolidColorBrush(_color), StrokeThickness = _size, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        _preview.Points = new PointCollection(ShapePoints(_tool, s, p));
    }

    private void Ink_Up(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } s) return;
        Ink.ReleaseMouseCapture();
        _dragStart = null;
        var p = ClampToImage(e.GetPosition(Ink));
        if (_preview != null) { Adorners.Children.Remove(_preview); _preview = null; }
        if (_tool == Tool.Crop) return; // wait for Apply
        if ((p - s).Length < 2) return;
        var stroke = new Stroke(new StylusPointCollection(ShapePoints(_tool, s, p)))
        {
            DrawingAttributes = new DrawingAttributes
            {
                Color = _color, Width = _size, Height = _size, FitToCurve = false, IgnorePressure = true, StylusTip = StylusTip.Ellipse,
            },
        };
        Ink.Strokes.Add(stroke);
        Commit();
    }

    /// <summary>Shapes are stored as ordinary ink strokes so the stroke eraser and undo treat them like pen strokes.</summary>
    private static IEnumerable<Point> ShapePoints(Tool tool, Point a, Point b)
    {
        switch (tool)
        {
            case Tool.Rectangle:
                return new[] { a, new Point(b.X, a.Y), b, new Point(a.X, b.Y), a };
            case Tool.Oval:
                var c = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                double rx = Math.Abs(b.X - a.X) / 2, ry = Math.Abs(b.Y - a.Y) / 2;
                return Enumerable.Range(0, 73).Select(i => new Point(c.X + rx * Math.Cos(i * Math.PI / 36), c.Y + ry * Math.Sin(i * Math.PI / 36)));
            case Tool.Arrow:
                double ang = Math.Atan2(b.Y - a.Y, b.X - a.X), len = Math.Clamp((b - a).Length * 0.25, 10, 40);
                Point Head(double d) => new(b.X - len * Math.Cos(ang + d), b.Y - len * Math.Sin(ang + d));
                return new[] { a, b, Head(0.45), b, Head(-0.45) };
            default:
                return new[] { a, b };
        }
    }

    private T AddAdorner<T>(T element) where T : UIElement
    {
        Adorners.Children.Add(element);
        return element;
    }

    private Point ClampToImage(Point p) =>
        new(Math.Clamp(p.X, 0, Surface.Width), Math.Clamp(p.Y, 0, Surface.Height));

    private double ScaleFactor() => Math.Max(0.01, SurfaceBox.ActualWidth / Math.Max(1, Surface.Width));

    // ---------------------------------------------------------------- text

    private TextBox? _editing;

    private void PlaceTextBox(Point p)
    {
        CommitPendingText();
        var box = new TextBox
        {
            FontFamily = TextFont, FontWeight = FontWeights.SemiBold, FontSize = Math.Max(14, _size * 5),
            Foreground = new SolidColorBrush(_color), Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            MinWidth = 120, AcceptsReturn = true, Padding = new Thickness(0), BorderThickness = new Thickness(1),
            Tag = (p, _color),
        };
        InkCanvas.SetLeft(box, p.X);
        InkCanvas.SetTop(box, p.Y);
        Ink.Children.Add(box);
        _editing = box;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
            {
                CommitPendingText();
                e.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, _) => CommitPendingText();
        Dispatcher.BeginInvoke(() => { box.Focus(); Keyboard.Focus(box); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CommitPendingText()
    {
        if (_editing is not { } box) return;
        _editing = null;
        Ink.Children.Remove(box);
        var (p, color) = ((Point, Color))box.Tag;
        if (string.IsNullOrWhiteSpace(box.Text)) return;
        Ink.Children.Add(MakeLabel(new TextItem(box.Text, p.X, p.Y, color, box.FontSize)));
        Commit();
    }

    private static TextBlock MakeLabel(TextItem t)
    {
        var label = new TextBlock
        {
            Text = t.Text, FontFamily = TextFont, FontWeight = FontWeights.SemiBold, FontSize = t.Size,
            Foreground = new SolidColorBrush(t.Color), Tag = t,
        };
        InkCanvas.SetLeft(label, t.X);
        InkCanvas.SetTop(label, t.Y);
        return label;
    }

    // ---------------------------------------------------------------- crop

    private void ApplyCrop()
    {
        if (_pendingCrop is not { } r || r.Width < 3 || r.Height < 3) return;
        var flat = Flatten();
        var rect = new Int32Rect((int)r.X, (int)r.Y, (int)Math.Min(r.Width, flat.PixelWidth - (int)r.X), (int)Math.Min(r.Height, flat.PixelHeight - (int)r.Y));
        var cropped = new CroppedBitmap(flat, rect);
        cropped.Freeze();
        Ink.Strokes.Clear();
        Ink.Children.Clear();
        BaseImage.Source = cropped;
        Restore(new EditorState(cropped, new StrokeCollection(), new List<TextItem>()));
        Commit();
        SelectTool(Tool.Pen);
    }

    private void ClearCrop()
    {
        _pendingCrop = null;
        if (_cropRect != null) { Adorners.Children.Remove(_cropRect); _cropRect = null; }
    }

    // ================================================================== output

    private bool SetClipboardImage(BitmapSource image)
    {
        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.SetImage(image); return true; }
            catch (COMException) { Thread.Sleep(50); }
        }
        return false;
    }

    private static void SetClipboardText(string text)
    {
        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (COMException) { Thread.Sleep(50); }
        }
    }

    private static void SaveBitmap(BitmapSource image, string path)
    {
        BitmapEncoder encoder = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        BitmapSource frame = encoder is PngBitmapEncoder ? image : new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private void Copy()
    {
        if (_current == null || _editing != null) return;
        Status(SetClipboardImage(Flatten()) ? "Copied to clipboard." : "Clipboard is busy; try again.");
    }

    private bool SaveAs()
    {
        if (_current == null) return false;
        CommitPendingText();
        Directory.CreateDirectory(_settings.SaveFolder);
        var dialog = new SaveFileDialog
        {
            Title = "Save as", InitialDirectory = _settings.SaveFolder, FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}",
            DefaultExt = ".png", Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|BMP (*.bmp)|*.bmp|GIF (*.gif)|*.gif",
        };
        if (dialog.ShowDialog(this) != true) return false;
        SaveBitmap(Flatten(), dialog.FileName);
        _dirty = false;
        Status($"Saved {dialog.FileName}");
        return true;
    }

    private bool ConfirmDiscard()
    {
        CommitPendingText();
        if (_current == null || !_dirty || !_settings.ConfirmUnsaved) return true;
        var answer = MessageBox.Show(this, "Save changes to this screenshot?", "SnipClone", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch { MessageBoxResult.Yes => SaveAs(), MessageBoxResult.No => true, _ => false };
    }

    // ================================================================== chrome

    private void Status(string text) => StatusText.Text = text;

    private void UpdateUi()
    {
        bool has = _current != null;
        CopyButton.IsEnabled = SaveButton.IsEnabled = has;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        SurfaceBox.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        Placeholder.Text = "Press New (Ctrl+N) to take a snip." +
                           (_hotkey != null ? $"\n{_hotkey} works from anywhere while SnipClone is running." : "");
    }

    private void BuildSwatches()
    {
        foreach (var c in Palette)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(c), Stroke = Brushes.Gray, StrokeThickness = 1 };
            var b = new Button { Content = dot, Padding = new Thickness(4), Margin = new Thickness(1, 0, 1, 0), Tag = c, ToolTip = ColorText.Format(c, "HEX"), MinWidth = 0 };
            b.Click += (_, _) => { _color = c; ApplyDrawingAttributes(); HighlightSwatch(); };
            SwatchPanel.Children.Add(b);
        }
        HighlightSwatch();
    }

    private void HighlightSwatch()
    {
        foreach (var b in SwatchPanel.Children.OfType<Button>())
        {
            var dot = (System.Windows.Shapes.Ellipse)b.Content;
            bool on = (Color)b.Tag == _color;
            dot.Stroke = on ? SystemColors.HighlightBrush : Brushes.Gray;
            dot.StrokeThickness = on ? 3 : 1;
        }
    }

    private void RegisterHotkey()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)!.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == Native.WM_HOTKEY) { StartSnip(); handled = true; }
            return IntPtr.Zero;
        });
        uint ctrl = Native.MOD_CONTROL, alt = Native.MOD_ALT, shift = Native.MOD_SHIFT;
        foreach (var (label, mods, vk) in new[]
                 {
                     ("Ctrl+Alt+S", ctrl | alt, (uint)'S'), ("Ctrl+Shift+Alt+S", ctrl | shift | alt, (uint)'S'),
                     ("Ctrl+Alt+Q", ctrl | alt, (uint)'Q'), ("Ctrl+Print Screen", ctrl, 0x2Cu),
                 })
        {
            if (Native.RegisterHotKey(_hwnd, 1, mods | Native.MOD_NOREPEAT, vk)) { _hotkey = label; break; }
        }
        Status(_hotkey != null ? $"Ready. {_hotkey} starts a snip from anywhere." : "Ready. (Every SnipClone hotkey is taken by other apps; use New.)");
        UpdateUi();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (_editing != null) return; // typing in a text box
        if (ctrl && e.Key == Key.N) { StartSnip(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { Copy(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { SaveAs(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (e.Key == Key.Enter && _pendingCrop != null) { ApplyCrop(); e.Handled = true; }
        else if (e.Key == Key.Escape && _tool == Tool.Crop) { SelectTool(Tool.Pen); e.Handled = true; }
    }

    // ---------------------------------------------------------------- XAML handlers

    private void NewSnip_Click(object sender, RoutedEventArgs e) => StartSnip();
    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    private void Copy_Click(object sender, RoutedEventArgs e) => Copy();
    private void Save_Click(object sender, RoutedEventArgs e) => SaveAs();
    private void ApplyCrop_Click(object sender, RoutedEventArgs e) => ApplyCrop();
    private void CancelCrop_Click(object sender, RoutedEventArgs e) => SelectTool(Tool.Pen);
    private void Tool_Click(object sender, RoutedEventArgs e) => SelectTool(Enum.Parse<Tool>((string)((FrameworkElement)sender).Tag));

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _size = e.NewValue;
        if (IsLoaded) ApplyDrawingAttributes();
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeBox.SelectedIndex < 0) return;
        _settings.Mode = Modes[ModeBox.SelectedIndex].Mode;
        _settings.Save();
    }

    private void DelayBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DelayBox.SelectedIndex < 0) return;
        _settings.Delay = Delays[DelayBox.SelectedIndex].Seconds;
        _settings.Save();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SettingsButton, Placement = PlacementMode.Bottom };
        MenuItem Check(string header, bool value, Action<bool> set)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = value };
            item.Click += (_, _) => { set(item.IsChecked); _settings.Save(); };
            return item;
        }
        menu.Items.Add(Check("Automatically copy to clipboard", _settings.AutoCopy, v => _settings.AutoCopy = v));
        menu.Items.Add(Check("Automatically save screenshots", _settings.AutoSave, v => _settings.AutoSave = v));
        menu.Items.Add(Check("Add outline to screenshots", _settings.Outline, v => _settings.Outline = v));
        menu.Items.Add(Check("Ask to save edited screenshots", _settings.ConfirmUnsaved, v => _settings.ConfirmUnsaved = v));
        menu.Items.Add(new Separator());
        var formats = new MenuItem { Header = "Color picker format" };
        foreach (var f in new[] { "HEX", "RGB", "HSL" })
        {
            var item = new MenuItem { Header = f, IsCheckable = true, IsChecked = _settings.ColorFormat == f };
            item.Click += (_, _) => { _settings.ColorFormat = f; _settings.Save(); };
            formats.Items.Add(item);
        }
        menu.Items.Add(formats);
        var folder = new MenuItem { Header = "Change screenshot folder…" };
        folder.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { InitialDirectory = _settings.SaveFolder, Title = "Screenshot folder" };
            if (dialog.ShowDialog(this) == true) { _settings.SaveFolder = dialog.FolderName; _settings.Save(); }
        };
        menu.Items.Add(folder);
        var open = new MenuItem { Header = "Open screenshot folder" };
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(_settings.SaveFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.SaveFolder}\"") { UseShellExecute = true });
        };
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        var about = new MenuItem { Header = "About SnipClone" };
        about.Click += (_, _) => MessageBox.Show(this,
            "SnipClone 2.0: a clean-room screen snipping tool, written in C# / WPF.\n\n" +
            "Built with Claude Code. Snipping Tool was first studied with the REA MCP (Reverse Engineer Anything, " +
            "github.com/morluto/rea); SnipClone was then written from scratch.\n\n" +
            "Not affiliated with Microsoft.\n\ngithub.com/AndroPlus/snipclone", "About SnipClone");
        menu.Items.Add(about);
        menu.IsOpen = true;
    }
}
