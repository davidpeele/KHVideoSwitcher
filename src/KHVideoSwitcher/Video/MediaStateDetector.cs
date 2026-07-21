namespace KHVideoSwitcher.Video;

public enum MediaState
{
    /// <summary>No capture running / no frames arriving.</summary>
    NoFeed,

    /// <summary>The stock (yeartext / no media) screen is showing.</summary>
    NoMedia,

    /// <summary>A still image is displayed.</summary>
    Still,

    /// <summary>Video is playing.</summary>
    Video,
}

/// <summary>
/// Classifies what JW Library is showing by analyzing the captured media
/// frames: motion between frames means video; a static frame either matches
/// the fingerprinted stock screen (no media) or is a still image.
/// State changes are debounced so brief flickers never flap scenes.
/// Analyze runs on the compose thread; TryCaptureStock may be called from
/// the UI thread.
/// </summary>
public sealed class MediaStateDetector
{
    public const int GridW = 64;
    public const int GridH = 36;
    public const int GridLen = GridW * GridH;

    // Screen captures are pixel-exact, so a static screen diffs at ~0;
    // any real playback motion lands far above this.
    private const double MotionThreshold = 0.8;
    private const int VideoStickyMs = 1500;   // motion this recent still counts as video
    private const double StockTolerance = 3.0;
    private const int EnterVideoMs = 250;     // react to video fast
    private const int EnterOtherMs = 1200;    // leave video / settle slowly

    private readonly object _lock = new();
    private readonly byte[] _gridA = new byte[GridLen];
    private readonly byte[] _gridB = new byte[GridLen];
    private bool _latestIsA;
    private bool _havePrev;
    private byte[]? _stock;
    private long _lastMotionMs = long.MinValue;
    private MediaState _active = MediaState.NoFeed;
    private MediaState _candidate = MediaState.NoFeed;
    private long _candidateSinceMs;

    public MediaState State => _active;
    public double LastMotion { get; private set; }
    public bool HasStock => _stock is not null;

    /// <summary>Classifies the given BGRA media frame and returns the debounced state.</summary>
    public MediaState Analyze(byte[] bgra, int width, int height)
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;

            var current = _latestIsA ? _gridB : _gridA; // write into the older buffer
            SampleLumaGrid(bgra, width, height, current);
            var previous = _latestIsA ? _gridA : _gridB;

            double motion = 0;
            if (_havePrev)
                motion = MeanAbsDiff(current, previous);
            LastMotion = motion;

            _latestIsA = !_latestIsA;
            var hadPrev = _havePrev;
            _havePrev = true;

            if (hadPrev && motion > MotionThreshold)
                _lastMotionMs = now;

            MediaState candidate;
            if (hadPrev && _lastMotionMs != long.MinValue && now - _lastMotionMs <= VideoStickyMs)
                candidate = MediaState.Video;
            else if (_stock is not null && MeanAbsDiff(current, _stock) <= StockTolerance)
                candidate = MediaState.NoMedia;
            else
                candidate = MediaState.Still;

            return Debounce(candidate, now);
        }
    }

    /// <summary>Call when the capture is stopped or stale.</summary>
    public MediaState AnalyzeNoFeed()
    {
        lock (_lock)
        {
            _havePrev = false;
            return Debounce(MediaState.NoFeed, Environment.TickCount64);
        }
    }

    /// <summary>Fingerprints the most recently analyzed frame as the stock (no media) screen.</summary>
    public bool TryCaptureStock(out string base64)
    {
        lock (_lock)
        {
            base64 = "";
            if (!_havePrev)
                return false;
            var latest = _latestIsA ? _gridA : _gridB;
            _stock = (byte[])latest.Clone();
            base64 = Convert.ToBase64String(_stock);
            return true;
        }
    }

    public void ImportStock(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return;
        try
        {
            var data = Convert.FromBase64String(base64);
            if (data.Length == GridLen)
            {
                lock (_lock)
                {
                    _stock = data;
                }
            }
        }
        catch (FormatException)
        {
            // Corrupt setting; ignore.
        }
    }

    private MediaState Debounce(MediaState candidate, long now)
    {
        if (candidate != _candidate)
        {
            _candidate = candidate;
            _candidateSinceMs = now;
        }
        if (_active != _candidate)
        {
            int needMs = _candidate == MediaState.Video ? EnterVideoMs : EnterOtherMs;
            if (now - _candidateSinceMs >= needMs)
                _active = _candidate;
        }
        return _active;
    }

    private static void SampleLumaGrid(byte[] bgra, int width, int height, byte[] grid)
    {
        for (var gy = 0; gy < GridH; gy++)
        {
            int y = (gy * height + height / 2) / GridH;
            for (var gx = 0; gx < GridW; gx++)
            {
                int x = (gx * width + width / 2) / GridW;
                int i = (y * width + x) * 4;
                if (i + 2 >= bgra.Length)
                {
                    grid[gy * GridW + gx] = 0;
                    continue;
                }
                // Cheap luma: (B + 2G + R) / 4.
                grid[gy * GridW + gx] = (byte)((bgra[i] + 2 * bgra[i + 1] + bgra[i + 2]) >> 2);
            }
        }
    }

    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
}
