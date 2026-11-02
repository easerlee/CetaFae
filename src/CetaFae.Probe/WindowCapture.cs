using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace CetaFae.Probe;

/// <summary>One HWND capture session in the owned worker; never creates a monitor capture item.</summary>
internal static class WindowCapture
{
    internal sealed record Frame(BitmapSource Image, Rect Bounds);

    [SupportedOSPlatform("windows10.0.18362")]
    internal static async Task<Frame> ReadAsync(IntPtr window, CancellationToken token)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new InvalidDataException("capture_backend_unavailable");
        using var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem", typeof(ICaptureItemInterop).GUID);
        var interop = (ICaptureItemInterop)Marshal.GetObjectForIUnknown(factory.ThisPtr);
        var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        GraphicsCaptureItem item;
        try
        {
            Marshal.ThrowExceptionForHR(interop.CreateForWindow(window, ref itemId, out var pointer));
            try { item = WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
        }
        finally { Marshal.ReleaseComObject(interop); }
        _ = MapBounds(window, item.Size.Width, item.Size.Height);
        using var device = CreateDevice();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) session.IsCursorCaptureEnabled = false;
        // The system's own capture border remains enabled; never suppress capture protection.
        session.StartCapture();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 1200)
        {
            token.ThrowIfCancellationRequested();
            using var frame = pool.TryGetNextFrame();
            if (frame is null) { await Task.Delay(20, token).ConfigureAwait(false); continue; }
            var bounds = MapBounds(window, frame.ContentSize.Width, frame.ContentSize.Height);
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied)
                .AsTask(token).ConfigureAwait(false);
            if (bitmap.PixelWidth != frame.ContentSize.Width || bitmap.PixelHeight != frame.ContentSize.Height)
                throw new InvalidDataException("capture_dimensions");
            var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
            bitmap.CopyToBuffer(pixels.AsBuffer());
            var image = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96,
                PixelFormats.Bgra32, null, pixels, bitmap.PixelWidth * 4);
            image.Freeze();
            return new Frame(image, bounds);
        }
        throw new InvalidDataException("capture_backend_unavailable");
    }

    internal static Rect MapBounds(IntPtr window, int width, int height)
    {
        var frame = GuideObservation.WindowRect(window);
        Native.GetWindowRect(window, out var outer);
        return MapBounds(frame, new Rect(outer.Left, outer.Top,
            Math.Max(0, outer.Right - outer.Left), Math.Max(0, outer.Bottom - outer.Top)), width, height);
    }

    internal static Rect MapBounds(Rect visible, Rect outer, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 16_000_000)
            throw new InvalidDataException("capture_dimensions");
        if (visible.Width == width && visible.Height == height) return visible;
        if (outer.Width == width && outer.Height == height) return outer;
        throw new InvalidDataException("capture_dimensions");
    }

    [SupportedOSPlatform("windows10.0.18362")]
    static IDirect3DDevice CreateDevice()
    {
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20,
            IntPtr.Zero, 0, 7, out var device, out _, out var context));
        var dxgi = IntPtr.Zero;
        var inspectable = IntPtr.Zero;
        try
        {
            var deviceId = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, ref deviceId, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != IntPtr.Zero) Marshal.Release(inspectable);
            if (dxgi != IntPtr.Zero) Marshal.Release(dxgi);
            if (context != IntPtr.Zero) Marshal.Release(context);
            if (device != IntPtr.Zero) Marshal.Release(device);
        }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr item);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr item);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr device);
}
