using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace SnipClone;

/// <summary>A frozen BGR32 copy of every monitor, in physical pixels. X/Y is the virtual-screen origin.</summary>
public sealed class ScreenSnapshot
{
    public required int X { get; init; }
    public required int Y { get; init; }
    public required BitmapSource Bitmap { get; init; }
    public required byte[] Pixels { get; init; }
    public int Width => Bitmap.PixelWidth;
    public int Height => Bitmap.PixelHeight;

    public Color GetPixel(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        int i = (y * Width + x) * 4;
        return Color.FromRgb(Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }
}

internal static class ScreenCapture
{
    /// <summary>GDI copy of the whole virtual screen. Used for the frozen overlay and rectangle/freeform snips.</summary>
    public static ScreenSnapshot CaptureVirtualScreen()
    {
        var (x, y, w, h) = Native.VirtualScreen();
        IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen);
        IntPtr bmp = Native.CreateCompatibleBitmap(screen, w, h), old = Native.SelectObject(mem, bmp);
        try
        {
            Native.BitBlt(mem, 0, 0, w, h, screen, x, y, Native.SRCCOPY | Native.CAPTUREBLT);
            Native.SelectObject(mem, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var bgr = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
            var pixels = new byte[w * h * 4];
            bgr.CopyPixels(pixels, w * 4, 0);
            var frozen = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, w * 4);
            frozen.Freeze();
            return new ScreenSnapshot { X = x, Y = y, Bitmap = frozen, Pixels = pixels };
        }
        finally
        {
            Native.DeleteObject(bmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ------------------------------------------------------------------ Windows.Graphics.Capture

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint levels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static IDirect3DDevice CreateDevice()
    {
        const int HARDWARE = 1, WARP = 5;
        const uint BGRA_SUPPORT = 0x20, SDK_VERSION = 7;
        int hr = D3D11CreateDevice(IntPtr.Zero, HARDWARE, IntPtr.Zero, BGRA_SUPPORT, IntPtr.Zero, 0, SDK_VERSION, out var d3d, out _, out var ctx);
        if (hr < 0)
            hr = D3D11CreateDevice(IntPtr.Zero, WARP, IntPtr.Zero, BGRA_SUPPORT, IntPtr.Zero, 0, SDK_VERSION, out d3d, out _, out ctx);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var iid = DxgiDeviceIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally
        {
            Marshal.Release(ctx);
            Marshal.Release(d3d);
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interop = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem").AsInterface<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        IntPtr ptr = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    /// <summary>
    /// Captures one window's own surface with Windows.Graphics.Capture, so windows overlapping it don't appear
    /// in the snip. The capture border is turned off where Windows allows it. Returns null if unsupported.
    /// </summary>
    public static async Task<BitmapSource?> CaptureWindowAsync(WindowInfo window)
    {
        if (!GraphicsCaptureSession.IsSupported()) return null;
        try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless); } catch { /* older Windows */ }

        var item = CreateItemForWindow(window.Handle);
        using var device = CreateDevice();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        try { session.IsBorderRequired = false; } catch { /* needs Windows 11 or the borderless permission */ }
        try { session.IsCursorCaptureEnabled = false; } catch { }

        var arrived = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (p, _) =>
        {
            var f = p.TryGetNextFrame();
            if (f != null && !arrived.TrySetResult(f)) f.Dispose();
        };
        session.StartCapture();
        if (await Task.WhenAny(arrived.Task, Task.Delay(3000)) != arrived.Task) return null;

        using var frame = arrived.Task.Result;
        using var soft = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
        int w = soft.PixelWidth, h = soft.PixelHeight;
        var buffer = new global::Windows.Storage.Streams.Buffer((uint)(w * h * 4));
        soft.CopyToBuffer(buffer);
        var bytes = buffer.ToArray();
        // Window content is opaque; force alpha so clipboard/JPEG consumers never see holes.
        for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;

        // The surface may include the invisible resize border around the DWM frame; trim to the visible frame.
        Native.GetWindowRect(window.Handle, out var outer);
        int cw = frame.ContentSize.Width, ch = frame.ContentSize.Height;
        int ox = 0, oy = 0, fw = Math.Min(cw, w), fh = Math.Min(ch, h);
        if (cw == outer.Width && ch == outer.Height && (window.Frame.Width < cw || window.Frame.Height < ch))
        {
            ox = window.Frame.Left - outer.Left;
            oy = window.Frame.Top - outer.Top;
            fw = window.Frame.Width;
            fh = window.Frame.Height;
        }
        var full = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bytes, w * 4);
        var cropped = new CroppedBitmap(full, new Int32Rect(Math.Max(0, ox), Math.Max(0, oy),
            Math.Min(fw, w - Math.Max(0, ox)), Math.Min(fh, h - Math.Max(0, oy))));
        cropped.Freeze();
        return cropped;
    }
}
