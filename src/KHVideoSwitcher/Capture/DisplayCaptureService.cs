using System.Runtime.InteropServices;
using System.Text;
using DirectN;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT;
using IDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace KHVideoSwitcher.Capture;

public sealed record CaptureTarget(string Name, IntPtr Handle, bool IsMonitor, int Width = 0, int Height = 0)
{
    public override string ToString() => (IsMonitor ? "Display: " : "Window: ") + Name;
}

/// <summary>
/// Captures a window (JW Library's media window) or a monitor via
/// Windows.Graphics.Capture into a CPU BGRA buffer.
///
/// Threading: Start/Stop/PumpFrames must be called from one thread (the
/// capture/compose thread is fine); TryCopyLatestFrame from anywhere.
/// PumpFrames drains the frame pool and updates the latest-frame buffer —
/// call it once per compose tick.
/// </summary>
public sealed class DisplayCaptureService : IDisposable
{
    private IComObject<ID3D11Device>? _device;
    private IComObject<ID3D11DeviceContext>? _context;
    private IComObject<ID3D11Texture2D>? _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private IDirect3DDevice? _winrtDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private SizeInt32 _poolSize;

    private readonly object _lock = new();
    private byte[] _buffer = [];
    private int _width;
    private int _height;
    private long _lastFrameMs;

    public bool IsRunning => _session is not null;
    public int Width => _width;
    public int Height => _height;
    public long LastFrameAgeMs => _lastFrameMs == 0 ? long.MaxValue : Environment.TickCount64 - _lastFrameMs;
    public string? TargetName { get; private set; }

    /// <summary>Human-readable reason frames stopped (window closed, pump error), or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Ask Windows not to draw the capture highlight border. Set before Start.</summary>
    public bool HideBorder { get; set; } = true;

    // ---------- enumeration ----------

    public static IReadOnlyList<CaptureTarget> ListTargets()
    {
        var targets = new List<CaptureTarget>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            int len = GetWindowTextLengthW(hwnd);
            if (len == 0)
                return true;

            // Skip tool windows and cloaked (hidden UWP) windows.
            long exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0)
                return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId)
                return true;

            var sb = new StringBuilder(len + 1);
            GetWindowTextW(hwnd, sb, sb.Capacity);

            int ww = 0, wh = 0;
            var size = "";
            if (GetWindowRect(hwnd, out var wr))
            {
                ww = wr.right - wr.left;
                wh = wr.bottom - wr.top;
                size = $" ({ww}x{wh})";
            }
            targets.Add(new CaptureTarget($"{sb}{size}", hwnd, false, ww, wh));
            return true;
        }, IntPtr.Zero);

        var monitorIndex = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT rect, IntPtr _2) =>
        {
            monitorIndex++;
            targets.Add(new CaptureTarget(
                $"Monitor {monitorIndex} ({rect.right - rect.left}x{rect.bottom - rect.top})", hMonitor, true,
                rect.right - rect.left, rect.bottom - rect.top));
            return true;
        }, IntPtr.Zero);

        return targets;
    }

    /// <summary>
    /// Best-guess JW Library media target. When JW Library shows two windows
    /// (main + media), the media window is the one sized like a whole monitor
    /// when fullscreen; otherwise prefer the last-enumerated (most recently
    /// created) JW window.
    /// </summary>
    public static CaptureTarget? FindJwLibraryMediaTarget(IReadOnlyList<CaptureTarget> targets)
    {
        var jw = targets.Where(t => !t.IsMonitor && t.Name.Contains("JW Library", StringComparison.OrdinalIgnoreCase)).ToList();
        if (jw.Count == 0)
            return null;
        if (jw.Count == 1)
            return jw[0];

        var monitors = targets.Where(t => t.IsMonitor).ToList();
        var fullscreen = jw.FirstOrDefault(w =>
            monitors.Any(m => Math.Abs(m.Width - w.Width) <= 4 && Math.Abs(m.Height - w.Height) <= 4));
        return fullscreen ?? jw[^1];
    }

    // ---------- capture ----------

    public void Start(CaptureTarget target)
    {
        // The capture objects must live in the MTA: created on an STA (UI)
        // thread their wrappers can't be used from the frame-pump thread
        // ("COM object separated from its underlying RCW").
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            Task.Run(() => Start(target)).GetAwaiter().GetResult();
            return;
        }

        Stop();
        EnsureDevice();

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IID_IGraphicsCaptureItem
        IntPtr abi = target.IsMonitor
            ? interop.CreateForMonitor(target.Handle, ref iid)
            : interop.CreateForWindow(target.Handle, ref iid);
        try
        {
            _item = MarshalInterface<GraphicsCaptureItem>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }

        _item.Closed += (_, _) => LastError = "The captured window was closed (JW Library recreates its media window when switching between windowed and fullscreen — reselect and capture again).";

        _poolSize = _item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _session = _framePool.CreateCaptureSession(_item);
        try
        {
            _session.IsCursorCaptureEnabled = false;
        }
        catch
        {
            // Older builds don't allow toggling cursor capture; harmless.
        }
        if (HideBorder)
        {
            try
            {
                // Requires borderless capture access; auto-granted for desktop apps on Win11.
                GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                    .GetAwaiter().GetResult();
                _session.IsBorderRequired = false;
            }
            catch
            {
                // Not available / denied: the yellow border stays. Cosmetic only.
            }
        }
        _session.StartCapture();
        TargetName = target.Name;
        LastError = null;
    }

    public void Stop()
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA &&
            (_session is not null || _framePool is not null))
        {
            Task.Run(Stop).GetAwaiter().GetResult();
            return;
        }

        _session?.Dispose();
        _session = null;
        _framePool?.Dispose();
        _framePool = null;
        _item = null;
        TargetName = null;
        _lastFrameMs = 0;
    }

    /// <summary>Drains pending frames, keeping the newest. Call once per compose tick. Never throws.</summary>
    public void PumpFrames()
    {
        try
        {
            var pool = _framePool;
            if (pool is null)
                return;

            Direct3D11CaptureFrame? newest = null;
            while (pool.TryGetNextFrame() is { } frame)
            {
                newest?.Dispose();
                newest = frame;
            }
            if (newest is null)
                return;

            using (newest)
            {
                // Window resized: recreate the pool at the new size; this frame is
                // still valid at its stated content size.
                var content = newest.ContentSize;
                if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                {
                    _poolSize = content;
                    pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
                }

                CopyFrameToBuffer(newest);
            }
        }
        catch (Exception ex)
        {
            LastError = $"Capture error: {ex.Message}";
        }
    }

    private unsafe void CopyFrameToBuffer(Direct3D11CaptureFrame frame)
    {
        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var texGuid = typeof(ID3D11Texture2D).GUID;
        IntPtr texPtr = access.GetInterface(ref texGuid);
        using var texture = ComObject.From<ID3D11Texture2D>(texPtr);
        Marshal.Release(texPtr);
        if (texture is null)
            return;

        texture.Object.GetDesc(out var desc);
        int w = (int)desc.Width, h = (int)desc.Height;
        int copyW = Math.Min(w, frame.ContentSize.Width);
        int copyH = Math.Min(h, frame.ContentSize.Height);
        if (copyW <= 0 || copyH <= 0)
            return;

        if (_staging is null || _stagingWidth != w || _stagingHeight != h)
        {
            _staging?.Dispose();
            _staging = _device!.CreateTexture2D(new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            });
            _stagingWidth = w;
            _stagingHeight = h;
        }

        _context!.Object.CopyResource(_staging.Object, texture.Object);
        D3D11_MAPPED_SUBRESOURCE mapped = default;
        var hr = _context.Object.Map(_staging.Object, 0, D3D11_MAP.D3D11_MAP_READ, 0, (nint)(&mapped));
        if (hr.IsError)
            return;
        try
        {
            int size = copyW * copyH * 4;
            lock (_lock)
            {
                if (_buffer.Length != size)
                    _buffer = new byte[size];
                fixed (byte* pDest = _buffer)
                {
                    var srcBase = (byte*)mapped.pData;
                    for (var y = 0; y < copyH; y++)
                        Buffer.MemoryCopy(srcBase + (long)y * mapped.RowPitch, pDest + (long)y * copyW * 4, copyW * 4, copyW * 4);
                }
                _width = copyW;
                _height = copyH;
                _lastFrameMs = Environment.TickCount64;
            }
        }
        finally
        {
            _context.Object.Unmap(_staging.Object, 0);
        }
    }

    public bool TryCopyLatestFrame(ref byte[] dest, out int width, out int height)
    {
        lock (_lock)
        {
            width = _width;
            height = _height;
            if (_buffer.Length == 0)
                return false;
            if (dest.Length != _buffer.Length)
                dest = new byte[_buffer.Length];
            Buffer.BlockCopy(_buffer, 0, dest, 0, _buffer.Length);
            return true;
        }
    }

    private void EnsureDevice()
    {
        if (_winrtDevice is not null)
            return;

        _device = D3D11Functions.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, out var context);
        _context = context;

        ComObject.WithComPointer(_device!.Object, unk =>
        {
            var dxgiGuid = typeof(IDXGIDevice).GUID;
            int qi = Marshal.QueryInterface(unk, in dxgiGuid, out var dxgiPtr);
            if (qi != 0)
                throw new InvalidOperationException($"IDXGIDevice QI failed: 0x{qi:X8}");
            try
            {
                CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out var inspectable).ThrowOnError();
                try
                {
                    _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiPtr);
            }
        });
    }

    public void Dispose()
    {
        Stop();
        _staging?.Dispose();
        _winrtDevice = null;
        _context?.Dispose();
        _device?.Dispose();
    }

    // ---------- interop ----------

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11")]
    private static extern HRESULT CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("user32")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32")]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("user32")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
}
