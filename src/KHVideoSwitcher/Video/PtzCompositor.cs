using System.Runtime.InteropServices;
using DirectN;

namespace KHVideoSwitcher.Video;

/// <summary>
/// Renders program and preview frames at 1920x1080 BGRA via Direct2D.
/// Inputs: the camera wide shot and (optionally) the captured media frame.
/// Scenes: Camera (PTZ crop), Media (letterboxed fullscreen), OverShoulder
/// (media + camera inset). Crossfades layer the incoming scene with opacity.
///
/// Call order per tick (capture thread): SetCameraFrame / SetMediaFrame,
/// RenderProgram, optionally RenderPreview. CopyOutputTo / CopyPreviewTo may
/// be called from any thread.
/// </summary>
public sealed class PtzCompositor : IDisposable
{
    public const int OutWidth = 1920;
    public const int OutHeight = 1080;
    public const int OutBytes = OutWidth * OutHeight * 4;

    // Over-the-shoulder layout: camera fullscreen, media inset top-right.
    // Settable at runtime (Settings window); read on the compose thread.
    public double InsetWidthFraction { get; set; } = 0.42;
    public double InsetMarginRight { get; set; } = 56;
    public double InsetMarginTop { get; set; } = 72;

    /// <summary>
    /// When true, OVER-THE-SHOULDER shifts the camera view left by roughly
    /// <see cref="InsetShiftFraction"/> of the output width so the media inset
    /// (top-right) doesn't cover the subject.
    /// </summary>
    public bool ShiftCameraForInset { get; set; }
    private const double InsetShiftFraction = 0.05;

    private readonly object _outputLock = new();
    private readonly byte[] _output = new byte[OutBytes];
    private readonly byte[] _previewOutput = new byte[OutBytes];
    private IComObject<IWICBitmap>? _targetWic;
    private IComObject<ID2D1RenderTarget>? _rt;
    private IComObject<ID2D1SolidColorBrush>? _placeholderBrush;
    private IComObject<ID2D1Bitmap>? _srcBitmap;
    private IComObject<ID2D1Bitmap>? _mediaBitmap;
    private int _srcWidth;
    private int _srcHeight;
    private int _mediaWidth;
    private int _mediaHeight;
    private bool _hasMedia;
    private bool _hasOutput;
    private bool _hasPreview;

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
        _placeholderBrush = _rt.CreateSolidColorBrush(new _D3DCOLORVALUE(1, 0.16f, 0.17f, 0.19f));
    }

    private IComObject<ID2D1Bitmap> EnsureBitmap(ref IComObject<ID2D1Bitmap>? bitmap, ref int bmpWidth, ref int bmpHeight, int width, int height)
    {
        if (bitmap is null || bmpWidth != width || bmpHeight != height)
        {
            bitmap?.Dispose();
            var props = new D2D1_BITMAP_PROPERTIES
            {
                pixelFormat = new D2D1_PIXEL_FORMAT
                {
                    format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                    alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_IGNORE
                }
            };
            bitmap = _rt!.CreateBitmap<ID2D1Bitmap>(new D2D_SIZE_U((uint)width, (uint)height), 0, 0, props);
            bmpWidth = width;
            bmpHeight = height;
        }
        return bitmap;
    }

    public unsafe void SetCameraFrame(byte[] data, int width, int height)
    {
        EnsureTarget();
        var bmp = EnsureBitmap(ref _srcBitmap, ref _srcWidth, ref _srcHeight, width, height);
        fixed (byte* p = data)
        {
            bmp.Object.CopyFromMemory(0, (nint)p, (uint)(width * 4)).ThrowOnError();
        }
    }

    public unsafe void SetMediaFrame(byte[] data, int width, int height)
    {
        EnsureTarget();
        var bmp = EnsureBitmap(ref _mediaBitmap, ref _mediaWidth, ref _mediaHeight, width, height);
        fixed (byte* p = data)
        {
            bmp.Object.CopyFromMemory(0, (nint)p, (uint)(width * 4)).ThrowOnError();
        }
        _hasMedia = true;
    }

    /// <summary>Marks media as unavailable (capture stopped/stale); scenes fall back to a placeholder.</summary>
    public void ClearMedia() => _hasMedia = false;

    public void RenderProgram(Scene program, SceneTransition? transition)
    {
        EnsureTarget();
        if (_srcBitmap is null)
            return;

        _rt!.BeginDraw();
        _rt.Clear(new _D3DCOLORVALUE(1, 0, 0, 0));
        DrawScene(program, 1f);
        if (transition is not null)
            DrawScene(transition.To, (float)transition.EasedProgress);
        _rt!.Object.EndDraw(0, 0).ThrowOnError();

        CopyTargetTo(_output, ref _hasOutput);
    }

    public void RenderPreview(Scene scene)
    {
        EnsureTarget();
        if (_srcBitmap is null)
            return;

        _rt!.BeginDraw();
        _rt.Clear(new _D3DCOLORVALUE(1, 0, 0, 0));
        DrawScene(scene, 1f);
        _rt!.Object.EndDraw(0, 0).ThrowOnError();

        CopyTargetTo(_previewOutput, ref _hasPreview);
    }

    private void CopyTargetTo(byte[] dest, ref bool flag)
    {
        using var locked = _targetWic!.Lock(WICBitmapLockFlags.WICBitmapLockRead);
        locked.Object.GetStride(out var stride).ThrowOnError();
        locked.Object.GetDataPointer(out var size, out var ptr).ThrowOnError();
        lock (_outputLock)
        {
            if (stride == OutWidth * 4)
            {
                Marshal.Copy(ptr, dest, 0, (int)Math.Min(size, OutBytes));
            }
            else
            {
                for (var y = 0; y < OutHeight; y++)
                    Marshal.Copy(ptr + y * (int)stride, dest, y * OutWidth * 4, OutWidth * 4);
            }
            flag = true;
        }
    }

    private void DrawScene(Scene scene, float opacity)
    {
        if (opacity <= 0)
            return;

        switch (scene.Kind)
        {
            case SceneKind.Camera:
                DrawCameraCrop(scene.Ptz, opacity, 0, 0, OutWidth, OutHeight);
                break;

            case SceneKind.Media:
                DrawMedia(new D2D_RECT_F(0f, 0f, OutWidth, OutHeight), opacity);
                break;

            case SceneKind.OverShoulder:
            {
                // Camera behind, captured media boxed in the top-right corner.
                var (cameraPtz, destDx) = ShiftCameraForInset ? ShiftForInset(scene.Ptz) : (scene.Ptz, 0f);
                DrawCameraCrop(cameraPtz, opacity, destDx, 0, OutWidth, OutHeight);
                float w = (float)(OutWidth * InsetWidthFraction);
                float h = w * 9f / 16f;
                float x = (float)(OutWidth - w - InsetMarginRight);
                float y = (float)InsetMarginTop;
                DrawMedia(new D2D_RECT_F(x, y, x + w, y + h), opacity);
                break;
            }
        }
    }

    /// <summary>
    /// Computes a left-shifted framing for OVER-THE-SHOULDER: pans the crop
    /// within the source frame by as much of the desired shift as the current
    /// zoom leaves room for (no-op at zoom 1, where the crop already spans the
    /// full source width), and returns a destination x-offset to cover the rest
    /// by sliding the whole rendered frame left, revealing the black background
    /// on the right edge.
    /// </summary>
    private (PtzState Ptz, float DestDx) ShiftForInset(PtzState ptz)
    {
        if (_srcWidth <= 0)
            return (ptz, 0f);

        var (cropX0, _, cropW, _) = ptz.CropRect(_srcWidth, _srcHeight);
        if (cropW <= 0)
            return (ptz, 0f);

        double desiredScreenShift = OutWidth * InsetShiftFraction;
        double desiredSourceShift = desiredScreenShift * (cropW / OutWidth);
        var shifted = ptz with { CenterX = ptz.CenterX + desiredSourceShift / _srcWidth };

        var (shiftedCropX, _, _, _) = shifted.CropRect(_srcWidth, _srcHeight);
        double achievedScreenShift = (shiftedCropX - cropX0) * (OutWidth / cropW);
        double leftoverScreenShift = Math.Max(0, desiredScreenShift - achievedScreenShift);

        return (shifted, (float)-leftoverScreenShift);
    }

    private unsafe void DrawCameraCrop(PtzState state, float opacity, float dx, float dy, float dw, float dh)
    {
        if (_srcBitmap is null)
            return;
        var (x, y, w, h) = state.CropRect(_srcWidth, _srcHeight);
        var src = new D2D_RECT_F((float)x, (float)y, (float)(x + w), (float)(y + h));
        var dest = new D2D_RECT_F(dx, dy, dx + dw, dy + dh);
        _rt!.Object.DrawBitmap(_srcBitmap.Object, (nint)(&dest), opacity,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, (nint)(&src));
    }

    /// <summary>
    /// Draws the captured media into <paramref name="dest"/> with aspect-fill:
    /// the 16:9 center of the source covers the rectangle edge to edge,
    /// cropping overflow (window chrome, ultrawide side bars). JW Library
    /// media is 16:9, so the actual content survives intact.
    /// </summary>
    private unsafe void DrawMedia(D2D_RECT_F dest, float opacity)
    {
        if (!_hasMedia || _mediaBitmap is null || _mediaWidth <= 0 || _mediaHeight <= 0)
        {
            // No media feed: neutral dark panel so fades still look intentional.
            if (_placeholderBrush is not null)
            {
                _placeholderBrush.Object.SetOpacity(opacity);
                _rt!.Object.FillRectangle(ref dest, _placeholderBrush.Object);
                _placeholderBrush.Object.SetOpacity(1f);
            }
            return;
        }

        double cropW = Math.Min(_mediaWidth, _mediaHeight * 16.0 / 9.0);
        double cropH = cropW * 9.0 / 16.0;
        float sx = (float)((_mediaWidth - cropW) / 2);
        float sy = (float)((_mediaHeight - cropH) / 2);
        var src = new D2D_RECT_F(sx, sy, sx + (float)cropW, sy + (float)cropH);
        _rt!.Object.DrawBitmap(_mediaBitmap.Object, (nint)(&dest), opacity,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, (nint)(&src));
    }

    /// <summary>Copies the latest program frame (BGRA 1920x1080) to <paramref name="dest"/>.</summary>
    public bool CopyOutputTo(IntPtr dest, int destSize) => CopyBuffer(_output, _hasOutput, dest, destSize);

    /// <summary>Copies the latest rendered preview frame to <paramref name="dest"/>.</summary>
    public bool CopyPreviewTo(IntPtr dest, int destSize) => CopyBuffer(_previewOutput, _hasPreview, dest, destSize);

    private bool CopyBuffer(byte[] source, bool available, IntPtr dest, int destSize)
    {
        lock (_outputLock)
        {
            if (!available || destSize < OutBytes)
                return false;
            Marshal.Copy(source, 0, dest, OutBytes);
            return true;
        }
    }

    public void Dispose()
    {
        _srcBitmap?.Dispose();
        _mediaBitmap?.Dispose();
        _placeholderBrush?.Dispose();
        _rt?.Dispose();
        _targetWic?.Dispose();
    }
}
