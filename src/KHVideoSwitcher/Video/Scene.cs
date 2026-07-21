namespace KHVideoSwitcher.Video;

public enum SceneKind
{
    /// <summary>The PTZ camera view.</summary>
    Camera,

    /// <summary>Captured media (JW Library), fullscreen and letterboxed.</summary>
    Media,

    /// <summary>Media fullscreen with the PTZ camera view inset ("over the shoulder").</summary>
    OverShoulder,
}

/// <summary>A complete program look: which scene, and the camera framing it uses.</summary>
public record struct Scene(SceneKind Kind, PtzState Ptz)
{
    public static Scene CameraWide => new(SceneKind.Camera, PtzState.FullFrame);
}

/// <summary>A crossfade in progress from one scene to another.</summary>
public sealed class SceneTransition(Scene from, Scene to, int durationMs)
{
    private readonly long _startMs = Environment.TickCount64;

    public Scene From { get; } = from;
    public Scene To { get; } = to;

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

    /// <summary>Smoothstep-eased opacity for the incoming scene.</summary>
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
