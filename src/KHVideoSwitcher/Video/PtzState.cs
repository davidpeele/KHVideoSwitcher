namespace KHVideoSwitcher.Video;

/// <summary>
/// A virtual PTZ framing: crop center (normalized 0..1 across the source frame)
/// and zoom factor (1 = widest 16:9 window that fits the source).
/// </summary>
public record struct PtzState(double CenterX, double CenterY, double Zoom)
{
    public const double MinZoom = 1.0;
    public const double MaxZoom = 8.0;

    public static PtzState FullFrame => new(0.5, 0.5, 1.0);

    /// <summary>Computes the crop rectangle in source pixels, clamped inside the frame.</summary>
    public readonly (double X, double Y, double W, double H) CropRect(int srcWidth, int srcHeight)
    {
        double zoom = Math.Clamp(Zoom, MinZoom, MaxZoom);

        // Widest 16:9 window that fits inside the source.
        double fitW = Math.Min(srcWidth, srcHeight * 16.0 / 9.0);
        double fitH = fitW * 9.0 / 16.0;

        double w = fitW / zoom;
        double h = fitH / zoom;

        double cx = Math.Clamp(CenterX * srcWidth, w / 2, srcWidth - w / 2);
        double cy = Math.Clamp(CenterY * srcHeight, h / 2, srcHeight - h / 2);

        return (cx - w / 2, cy - h / 2, w, h);
    }

    public readonly PtzState Clamped()
    {
        double zoom = Math.Clamp(Zoom, MinZoom, MaxZoom);
        // Keep the center in a range where the crop stays inside the frame
        // for a 16:9 source (the per-frame CropRect clamps exactly anyway).
        double half = 0.5 / zoom;
        return new PtzState(
            Math.Clamp(CenterX, half, 1 - half),
            Math.Clamp(CenterY, half, 1 - half),
            zoom);
    }
}

/// <summary>A crossfade in progress from one framing to another.</summary>
public sealed class Transition(PtzState from, PtzState to, int durationMs)
{
    private readonly long _startMs = Environment.TickCount64;

    public PtzState From { get; } = from;
    public PtzState To { get; } = to;

    public double Progress
    {
        get
        {
            if (durationMs <= 0)
                return 1;
            double t = (Environment.TickCount64 - _startMs) / (double)durationMs;
            return Math.Clamp(t, 0, 1);
        }
    }

    /// <summary>Smoothstep-eased opacity for the incoming shot.</summary>
    public double EasedProgress
    {
        get
        {
            double t = Progress;
            return t * t * (3 - 2 * t);
        }
    }

    public bool IsDone => Progress >= 1;
}
