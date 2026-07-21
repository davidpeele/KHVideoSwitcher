using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace KHVideoSwitcher.Capture;

public sealed record CameraInfo(string Name, MediaFrameSourceGroup SourceGroup)
{
    public override string ToString() => Name;
}

public sealed record CaptureFormat(int Width, int Height, double FrameRate, string Subtype)
{
    public override string ToString() => $"{Width}x{Height} @ {FrameRate:0.#} fps ({Subtype})";
}

/// <summary>
/// Captures BGRA8 frames from a webcam via MediaCapture/MediaFrameReader.
/// Frames land in an internal reusable buffer; the UI copies the latest frame
/// out under a lock, so a slow consumer only drops frames, never blocks capture.
/// </summary>
public sealed class CameraCaptureService
{
    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _reader;
    private byte[] _buffer = Array.Empty<byte>();
    private readonly object _bufferLock = new();
    private long _framesReceived;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public CaptureFormat? ActiveFormat { get; private set; }
    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public bool IsRunning => _reader is not null;

    /// <summary>Raised on a background thread whenever a new frame has been stored.</summary>
    public event Action? FrameArrived;

    public static async Task<IReadOnlyList<CameraInfo>> ListCamerasAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        return groups
            .Where(g => g.SourceInfos.Any(i =>
                i.SourceKind == MediaFrameSourceKind.Color &&
                (i.MediaStreamType == MediaStreamType.VideoRecord ||
                 i.MediaStreamType == MediaStreamType.VideoPreview)))
            .Select(g => new CameraInfo(g.DisplayName, g))
            .ToList();
    }

    public async Task<CaptureFormat> StartAsync(CameraInfo camera)
    {
        await StopAsync();

        _mediaCapture = new MediaCapture();
        await _mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = camera.SourceGroup,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            StreamingCaptureMode = StreamingCaptureMode.Video,
        });

        var source = _mediaCapture.FrameSources.Values.First(s =>
            s.Info.SourceKind == MediaFrameSourceKind.Color);

        // Highest resolution that still delivers at least 15 fps; ties broken by frame rate.
        var best = source.SupportedFormats
            .Where(f => f.VideoFormat is not null && FrameRateOf(f) >= 15)
            .OrderByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
            .ThenByDescending(FrameRateOf)
            .FirstOrDefault()
            ?? source.SupportedFormats.OrderByDescending(FrameRateOf).First();
        await source.SetFormatAsync(best);

        Width = (int)best.VideoFormat.Width;
        Height = (int)best.VideoFormat.Height;
        ActiveFormat = new CaptureFormat(Width, Height, FrameRateOf(best), best.Subtype);

        _reader = await _mediaCapture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;

        var status = await _reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            await StopAsync();
            throw new InvalidOperationException($"Camera failed to start: {status}");
        }
        return ActiveFormat;
    }

    public async Task StopAsync()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            await _reader.StopAsync();
            _reader.Dispose();
            _reader = null;
        }
        _mediaCapture?.Dispose();
        _mediaCapture = null;
        Interlocked.Exchange(ref _framesReceived, 0);
    }

    /// <summary>Copies the most recent frame into a managed buffer (BGRA8, Width*Height*4 bytes).</summary>
    public bool TryCopyLatestFrame(byte[] dest)
    {
        lock (_bufferLock)
        {
            if (_buffer.Length == 0 || dest.Length < _buffer.Length)
                return false;
            Buffer.BlockCopy(_buffer, 0, dest, 0, _buffer.Length);
            return true;
        }
    }

    /// <summary>Copies the most recent frame into <paramref name="dest"/> (BGRA8, Width*Height*4 bytes).</summary>
    public bool TryCopyLatestFrame(IntPtr dest, int destSize)
    {
        lock (_bufferLock)
        {
            if (_buffer.Length == 0 || _buffer.Length > destSize)
                return false;
            Marshal.Copy(_buffer, 0, dest, _buffer.Length);
            return true;
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null)
            return;

        int size = bitmap.PixelWidth * bitmap.PixelHeight * 4;
        lock (_bufferLock)
        {
            if (_buffer.Length != size)
                _buffer = new byte[size];
            bitmap.CopyToBuffer(_buffer.AsBuffer());
        }
        Interlocked.Increment(ref _framesReceived);
        FrameArrived?.Invoke();
    }

    private static double FrameRateOf(MediaFrameFormat f) =>
        f.FrameRate.Denominator == 0 ? 0 : (double)f.FrameRate.Numerator / f.FrameRate.Denominator;
}
