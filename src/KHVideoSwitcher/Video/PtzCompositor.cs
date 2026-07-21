using System.Runtime.InteropServices;
using DirectN;

namespace KHVideoSwitcher.Video;

/// <summary>
/// Renders the program output: crops the source frame to the active PTZ framing
/// (optionally crossfading to a second framing during a transition) and scales
/// it to 1920x1080 BGRA via Direct2D. Compose() is called on the capture thread;
/// CopyOutputTo() may be called from any thread.
/// </summary>
public sealed class PtzCompositor : IDisposable
{
    public const int OutWidth = 1920;
    public const int OutHeight = 1080;
    public const int OutBytes = OutWidth * OutHeight * 4;

    private readonly object _outputLock = new();
    private readonly byte[] _output = new byte[OutBytes];
    private IComObject<IWICBitmap>? _targetWic;
    private IComObject<ID2D1RenderTarget>? _rt;
    private IComObject<ID2D1Bitmap>? _srcBitmap;
    private int _srcWidth;
    private int _srcHeight;
    private bool _hasOutput;

    public bool HasOutput => _hasOutput;

    private void EnsureTarget()
    {
        if (_rt is not null)
            return;

        using var factory = D2D1Functions.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED);
        _targetWic = WICImagingFactory.CreateBitmap(OutWidth, OutHeight,
            WICConstants.GUID_WICPixelFormat32bppPBGRA, WICBitmapCreateCacheOption.WICBitmapCacheOnDemand);
        _rt = factory.CreateWicBitmapRenderTarget(_targetWic, new D2D1_RENDER_TARGET_PROPERTIES
        {
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
                format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM
            }
        });
    }

    private void EnsureSource(int width, int height)
    {
        if (_srcBitmap is not null && _srcWidth == width && _srcHeight == height)
            return;

        _srcBitmap?.Dispose();
        var props = new D2D1_BITMAP_PROPERTIES
        {
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_IGNORE
            }
        };
        _srcBitmap = _rt!.CreateBitmap<ID2D1Bitmap>(new D2D_SIZE_U((uint)width, (uint)height), 0, 0, props);
        _srcWidth = width;
        _srcHeight = height;
    }

    /// <summary>Renders one program frame from <paramref name="source"/> (tightly packed BGRA).</summary>
    public unsafe void Compose(byte[] source, int width, int height, PtzState program, Transition? transition)
    {
        EnsureTarget();
        EnsureSource(width, height);

        fixed (byte* p = source)
        {
            _srcBitmap!.Object.CopyFromMemory(0, (nint)p, (uint)(width * 4)).ThrowOnError();
        }

        _rt!.BeginDraw();
        _rt.Clear(new _D3DCOLORVALUE(1, 0, 0, 0));

        DrawCrop(program, 1f);
        if (transition is not null)
            DrawCrop(transition.To, (float)transition.EasedProgress);

        _rt.Object.EndDraw(0, 0).ThrowOnError();

        // Copy rendered pixels out for the vcam/preview consumers.
        using var locked = _targetWic!.Lock(WICBitmapLockFlags.WICBitmapLockRead);
        locked.Object.GetStride(out var stride).ThrowOnError();
        locked.Object.GetDataPointer(out var size, out var ptr).ThrowOnError();
        lock (_outputLock)
        {
            if (stride == OutWidth * 4)
            {
                Marshal.Copy(ptr, _output, 0, (int)Math.Min(size, OutBytes));
            }
            else
            {
                for (var y = 0; y < OutHeight; y++)
                    Marshal.Copy(ptr + y * (int)stride, _output, y * OutWidth * 4, OutWidth * 4);
            }
            _hasOutput = true;
        }
    }

    private unsafe void DrawCrop(PtzState state, float opacity)
    {
        if (opacity <= 0)
            return;

        var (x, y, w, h) = state.CropRect(_srcWidth, _srcHeight);
        var src = new D2D_RECT_F((float)x, (float)y, (float)(x + w), (float)(y + h));
        var dest = new D2D_RECT_F(0f, 0f, OutWidth, OutHeight);
        _rt!.Object.DrawBitmap(_srcBitmap!.Object, (nint)(&dest), opacity,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, (nint)(&src));
    }

    /// <summary>Copies the latest program frame (BGRA 1920x1080) to <paramref name="dest"/>.</summary>
    public bool CopyOutputTo(IntPtr dest, int destSize)
    {
        lock (_outputLock)
        {
            if (!_hasOutput || destSize < OutBytes)
                return false;
            Marshal.Copy(_output, 0, dest, OutBytes);
            return true;
        }
    }

    public void Dispose()
    {
        _srcBitmap?.Dispose();
        _rt?.Dispose();
        _targetWic?.Dispose();
    }
}
