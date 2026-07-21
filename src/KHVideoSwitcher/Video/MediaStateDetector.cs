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

    // A tick is "moving" when enough grid CELLS changed. Cell-count (not
    // frame-average) keeps calm scenes detectable: a talking head changes a
    // small cluster of cells every frame, which a frame-wide mean dilutes.
    // Screen captures are pixel-exact, so a static screen changes 0 cells.
    private const int CellDiffThreshold = 4;  // luma delta for a cell to count as changed (catches dark fade-up intros)
    private const int MovedCellsNeeded = 4;   // of 2304 cells
    private const int VideoStickyMs = 1500;   // sustained motion this recent still counts as video
    private const double StockTolerance = 3.0;
    private const int EnterVideoMs = 150;     // react to video fast
    private const int EnterOtherMs = 1200;    // leave video / settle slowly

    // Video means SUSTAINED motion: at least MotionTicksNeeded moving frames
    // within the last MotionRingSize ticks (~1.5s at 30fps). A still image
    // swap (1 changed frame) or JW Library's image fade-in (~0.5s) stays far
    // below this; real playback is continuous and clears it easily.
    // Sing-along lyric videos change too rarely to qualify — the operator
    // taps MEDIA at song start and the video latch holds from there.
    // 18 ticks ≈ 0.6s of motion. A still image's fade-in (~0.5s ≈ 15 ticks)
    // stays under the bar; real playback clears it within about a second.
    private const int MotionRingSize = 45;
    private const int MotionTicksNeeded = 18;

    // Videos often hold a static frame for a while (scripture references,
    // title cards). Once Video is active it is LATCHED: stills don't end it —
    // only the stock screen or feed loss does. Without a stock fingerprint a
    // long stillness is the fallback exit.
    private const int NoStockStillExitMs = 6000;

    private readonly object _lock = new();
    private readonly byte[] _gridA = new byte[GridLen];
    private readonly byte[] _gridB = new byte[GridLen];
    private bool _latestIsA;
    private bool _havePrev;
    private byte[]? _stock;
    private long _lastMotionMs = long.MinValue;
    private long _lastSustainedMs = long.MinValue;
    private readonly bool[] _motionRing = new bool[MotionRingSize];
    private int _motionRingIndex;
    private int _motionRingCount;
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

            int movedCells = 0;
            if (_havePrev)
                movedCells = CountMovedCells(current, previous);
            LastMotion = movedCells;

            _latestIsA = !_latestIsA;
            var hadPrev = _havePrev;
            _havePrev = true;

            bool moving = hadPrev && movedCells >= MovedCellsNeeded;
            if (moving)
                _lastMotionMs = now;

            // Sliding window of per-tick motion flags.
            if (_motionRing[_motionRingIndex])
                _motionRingCount--;
            _motionRing[_motionRingIndex] = moving;
            if (moving)
                _motionRingCount++;
            _motionRingIndex = (_motionRingIndex + 1) % MotionRingSize;

            if (_motionRingCount >= MotionTicksNeeded)
                _lastSustainedMs = now;

            bool stockMatch = _stock is not null && MeanAbsDiff(current, _stock) <= StockTolerance;

            MediaState candidate;
            if (hadPrev && _lastSustainedMs != long.MinValue && now - _lastSustainedMs <= VideoStickyMs)
            {
                candidate = MediaState.Video;
            }
            else if (stockMatch)
            {
                candidate = MediaState.NoMedia;
            }
            else
            {
                candidate = MediaState.Still;

                // Video latch: during playback a static segment stays Video.
                if (_active == MediaState.Video)
                {
                    long stillnessMs = _lastMotionMs == long.MinValue ? 0 : now - _lastMotionMs;
                    if (_stock is not null || stillnessMs <= NoStockStillExitMs)
                        candidate = MediaState.Video;
                }
            }

            return Debounce(candidate, now);
        }
    }

    /// <summary>Call when the capture is stopped or stale.</summary>
    public MediaState AnalyzeNoFeed()
    {
        lock (_lock)
        {
            _havePrev = false;
            Array.Clear(_motionRing);
            _motionRingCount = 0;
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

    private static int CountMovedCells(byte[] a, byte[] b)
    {
        var count = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i] - b[i]) > CellDiffThreshold)
                count++;
        }
        return count;
    }

    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
}
