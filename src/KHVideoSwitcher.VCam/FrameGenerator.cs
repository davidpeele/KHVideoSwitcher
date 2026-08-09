using System;
using System.Runtime.InteropServices;
using DirectN;
using KHVideoSwitcher.VCam.Utilities;

namespace KHVideoSwitcher.VCam
{
    /// <summary>
    /// Produces virtual camera frames. Each frame is the latest image published
    /// by the switcher app through <see cref="SharedFrameChannel"/>, drawn onto a
    /// D2D render target (GPU texture or WIC bitmap depending on the consumer),
    /// then converted to NV12 when requested. When the app isn't publishing,
    /// a standby card is shown instead.
    /// (Derived from VCamNetSample, MIT license, Simon Mourier.)
    /// </summary>
    public class FrameGenerator : IDisposable
    {
        private const long StaleFrameMs = 2000;

        private bool _disposedValue;
        private uint _width;
        private uint _height;
        private ulong _frameCount;
        private IntPtr _deviceHandle;
        private IComObject<ID3D11Texture2D>? _texture;
        private IComObject<ID2D1RenderTarget>? _renderTarget;
        private IComObject<ID2D1SolidColorBrush>? _whiteBrush;
        private IComObject<IDWriteTextFormat>? _textFormat;
        private IComObject<IDWriteFactory>? _dwrite;
        private ComObject<IMFTransform>? _converter;
        private IComObject<IWICBitmap>? _bitmap;
        private ComObject<IMFDXGIDeviceManager>? _dxgiManager;

        private readonly SharedFrameChannel _channel = new();
        private readonly byte[] _sharedPixels = new byte[SharedFrameChannel.PixelCapacity];
        private IComObject<ID2D1Bitmap>? _sharedBitmap;
        private int _sharedBitmapWidth;
        private int _sharedBitmapHeight;
        private long _lastGoodFrameMs;

        public bool HasD3DManager => _texture != null;
        public ulong FrameCount => _frameCount;

        private HRESULT CreateRenderTargetResources(uint width, uint height)
        {
            if (_renderTarget == null)
                return HRESULTS.E_FAIL;

            _whiteBrush = _renderTarget.CreateSolidColorBrush(new _D3DCOLORVALUE(1, 1, 1, 1));
            _dwrite = DWriteFunctions.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED);
            _textFormat = _dwrite.CreateTextFormat("Segoe UI", 40);
            _textFormat.Object.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            _textFormat.Object.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
            _width = width;
            _height = height;
            return HRESULTS.S_OK;
        }

        private void SetConverterTypes(uint width, uint height)
        {
            Functions.MFCreateMediaType(out var inputType).ThrowOnError();
            inputType.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
            inputType.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_RGB32).ThrowOnError();
            inputType.SetSize(MFConstants.MF_MT_FRAME_SIZE, width, height);
            _converter!.Object.SetInputType(0, inputType, 0).ThrowOnError();

            Functions.MFCreateMediaType(out var outputType).ThrowOnError();
            outputType.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
            outputType.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_NV12).ThrowOnError();
            outputType.SetSize(MFConstants.MF_MT_FRAME_SIZE, width, height);
            _converter!.Object.SetOutputType(0, outputType, 0).ThrowOnError();
        }

        public HRESULT SetD3DManager(object manager, uint width, uint height)
        {
            if (manager == null)
                return HRESULTS.E_POINTER;

            if (width == 0 || height == 0)
                return HRESULTS.E_INVALIDARG;

            if (manager is not IMFDXGIDeviceManager dxgiManager)
                return HRESULTS.E_NOTIMPL;

            _dxgiManager = new ComObject<IMFDXGIDeviceManager>(dxgiManager);
            _dxgiManager.Object.OpenDeviceHandle(out _deviceHandle).ThrowOnError();
            _dxgiManager.Object.GetVideoService(_deviceHandle, typeof(ID3D11Device).GUID, out var obj).ThrowOnError();

            using var device = new ComObject<ID3D11Device>((ID3D11Device)obj);
            _texture = device.CreateTexture2D(new D3D11_TEXTURE2D_DESC
            {
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                Width = width,
                Height = height,
                ArraySize = 1,
                MipLevels = 1,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET)
            });

            var surface = new ComObject<IDXGISurface>((IDXGISurface)_texture.Object, false);
            using var factory = D2D1Functions.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED);
            _renderTarget = factory.CreateDxgiSurfaceRenderTarget(surface, new D2D1_RENDER_TARGET_PROPERTIES { pixelFormat = new D2D1_PIXEL_FORMAT { alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED } });

            CreateRenderTargetResources(width, height).ThrowOnError();

            _sharedBitmap = null; // render target changed; recreate lazily

            _converter = new ComObject<IMFTransform>((IMFTransform)System.Activator.CreateInstance(Type.GetTypeFromCLSID(MFConstants.CLSID_VideoProcessorMFT)!)!);
            SetConverterTypes(width, height);

            ComObject.WithComPointer(manager, unk => _converter!.Object.ProcessMessage(_MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, unk));
            EventProvider.LogInfo("OK");
            return HRESULTS.S_OK;
        }

        public HRESULT EnsureRenderTarget(uint width, uint height)
        {
            try
            {
                if (!HasD3DManager)
                {
                    using var factory = D2D1Functions.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED);
                    _bitmap = WICImagingFactory.CreateBitmap((int)width, (int)height, WICConstants.GUID_WICPixelFormat32bppPBGRA, WICBitmapCreateCacheOption.WICBitmapCacheOnDemand);
                    _renderTarget = factory.CreateWicBitmapRenderTarget(_bitmap, new D2D1_RENDER_TARGET_PROPERTIES
                    {
                        pixelFormat = new D2D1_PIXEL_FORMAT
                        {
                            alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
                            format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM
                        }
                    });

                    CreateRenderTargetResources(width, height).ThrowOnError();

                    _sharedBitmap = null; // render target changed; recreate lazily

                    _converter = new ComObject<IMFTransform>((IMFTransform)System.Activator.CreateInstance(Type.GetTypeFromCLSID(MFConstants.CLSID_CColorConvertDMO)!)!);
                    SetConverterTypes(width, height);
                }

                _frameCount = 0;
                return HRESULTS.S_OK;
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        private unsafe void DrawFrame()
        {
            if (_renderTarget == null)
                return;

            _renderTarget.BeginDraw();

            // Pull the newest frame if one is available; on a missed read keep
            // showing the previous frame rather than blinking to standby.
            if (_channel.TryOpenForRead() &&
                _channel.TryReadFrame(_sharedPixels, out var fw, out var fh, out var ageMs) &&
                ageMs <= StaleFrameMs)
            {
                if (_sharedBitmap == null || _sharedBitmapWidth != fw || _sharedBitmapHeight != fh)
                {
                    _sharedBitmap?.Dispose();
                    var props = new D2D1_BITMAP_PROPERTIES
                    {
                        pixelFormat = new D2D1_PIXEL_FORMAT
                        {
                            format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                            alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_IGNORE
                        }
                    };
                    _sharedBitmap = _renderTarget.CreateBitmap<ID2D1Bitmap>(new D2D_SIZE_U((uint)fw, (uint)fh), 0, 0, props);
                    _sharedBitmapWidth = fw;
                    _sharedBitmapHeight = fh;
                }

                fixed (byte* p = _sharedPixels)
                {
                    _sharedBitmap.Object.CopyFromMemory(0, (nint)p, (uint)(fw * 4)).ThrowOnError();
                }
                _lastGoodFrameMs = Environment.TickCount64;
            }

            var haveFrame = _sharedBitmap is not null &&
                            Environment.TickCount64 - _lastGoodFrameMs <= StaleFrameMs;
            if (haveFrame)
            {
                var dest = new D2D_RECT_F(0f, 0f, _width, _height);
                _renderTarget.Object.DrawBitmap(_sharedBitmap!.Object, (nint)(&dest), 1f,
                    D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, 0);
            }

            if (!haveFrame)
            {
                // Standby card: dark background with a caption.
                _renderTarget.Clear(new _D3DCOLORVALUE(1, 0.10f, 0.11f, 0.13f));
                if (_textFormat != null && _dwrite != null && _whiteBrush != null)
                {
                    const string text = "KH Video Switcher\nwaiting for video…";
                    using var layout = _dwrite.CreateTextLayout(_textFormat, text, text.Length, _width, _height);
                    _renderTarget.DrawTextLayout(new D2D_POINT_2F(0, 0), layout, _whiteBrush);
                }
            }

            _renderTarget.EndDraw();
        }

        public IComObject<IMFSample> Generate(IComObject<IMFSample> sample, Guid format)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(sample);
                IComObject<IMFSample>? outSample;

                DrawFrame();

                if (HasD3DManager)
                {
                    sample.RemoveAllBuffers();

                    using var mediaBuffer = MFFunctions.MFCreateDXGISurfaceBuffer(_texture);
                    sample.Object.AddBuffer(mediaBuffer.Object).ThrowOnError();

                    if (format == MFConstants.MFVideoFormat_NV12)
                    {
                        _converter!.Object.ProcessInput(0, sample.Object, 0).ThrowOnError();
                        var buffers = new _MFT_OUTPUT_DATA_BUFFER[1];
                        _converter.Object.ProcessOutput(0, (uint)buffers.Length, buffers, out var status).ThrowOnError();
                        outSample = ComObject.From<IMFSample>(buffers[0].pSample);
                        Marshal.Release(buffers[0].pSample);
                    }
                    else
                    {
                        outSample = sample;
                    }

                    _frameCount++;
                    return outSample;
                }

                using var locked = _bitmap.Lock(WICBitmapLockFlags.WICBitmapLockRead);
                locked.Object.GetSize(out var w, out var h).ThrowOnError();
                locked.Object.GetStride(out var wicStride).ThrowOnError();
                locked.Object.GetDataPointer(out var wicSize, out var wicPointer).ThrowOnError();

                if (format == MFConstants.MFVideoFormat_NV12)
                {
                    using var wicSample = MFFunctions.MFCreateSample();
                    using var wicBuffer = MFFunctions.MFCreateMemoryBuffer(wicSize);
                    wicSample.AddBuffer(wicBuffer);
                    wicBuffer.WithLock((scanline, length, _) => wicPointer.CopyTo(scanline, length));
                    wicBuffer.SetCurrentLength(wicSize);

                    _converter!.Object.ProcessInput(0, wicSample.Object, 0).ThrowOnError();

                    sample.WithComPointer(outSamplePtr =>
                    {
                        var buffers = new _MFT_OUTPUT_DATA_BUFFER[1];
                        buffers[0].pSample = outSamplePtr;
                        _converter.Object.ProcessOutput(0, 1, buffers, out var status).ThrowOnError();
                    });
                }
                else
                {
                    using var buffer = sample.GetBufferByIndex(0);
                    buffer.WithLock((scanline, length, _) => wicPointer.CopyTo(scanline, length));
                    buffer.SetCurrentLength(wicSize);
                }

                _frameCount++;
                return sample;
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            EventProvider.LogInfo();
            try
            {
                if (!_disposedValue)
                {
                    if (disposing)
                    {
                        var dxgiManager = _dxgiManager;
                        if (_deviceHandle != IntPtr.Zero && dxgiManager != null)
                        {
                            dxgiManager.Object.CloseDeviceHandle(_deviceHandle);
                        }

                        _sharedBitmap.SafeDispose();
                        _channel.Dispose();
                        _whiteBrush.SafeDispose();
                        _bitmap.SafeDispose();
                        _texture.SafeDispose();
                        _textFormat.SafeDispose();
                        _dwrite.SafeDispose();
                        _renderTarget.SafeDispose();
                        _converter.SafeDispose();
                    }

                    _disposedValue = true;
                    EventProvider.LogInfo("Disposed");
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
