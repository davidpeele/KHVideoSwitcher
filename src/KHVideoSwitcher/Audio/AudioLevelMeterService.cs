using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Render;
using WinRT;

namespace KHVideoSwitcher.Audio;

public sealed record AudioDeviceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Watches the peak input level of one chosen audio capture device (e.g. a
/// line-in fed from the sound booth amplifier) purely so the operator has a
/// visual clue to the level it's dialed in at - the same role the two little
/// meters play in OBS's audio mixer. This never records, forwards, or mixes
/// the audio anywhere; it opens the device in shared mode so it doesn't
/// interfere with Zoom (or anything else) capturing the same input.
///
/// Threading: StartAsync/StopAsync run on the UI thread. TakePeaks() is safe
/// to call from anywhere and returns/clears the peak seen since the last call,
/// so a UI timer can poll it like CameraCaptureService's latest-frame buffer.
/// </summary>
public sealed class AudioLevelMeterService
{
    public const int MaxChannels = 2;

    private AudioGraph? _graph;
    private AudioDeviceInputNode? _inputNode;
    private AudioFrameOutputNode? _outputNode;
    private readonly object _levelLock = new();
    private readonly float[] _peak = new float[MaxChannels];
    private int _channelCount;

    public bool IsRunning => _graph is not null;
    public int ChannelCount => _channelCount;

    /// <summary>Raised (on the audio thread) if the device drops out - unplugged, reformatted, etc.</summary>
    public event Action<string>? Failed;

    public static async Task<IReadOnlyList<AudioDeviceInfo>> ListDevicesAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
        return devices.Select(d => new AudioDeviceInfo(d.Id, d.Name)).ToList();
    }

    public async Task StartAsync(AudioDeviceInfo device)
    {
        await StopAsync();

        var deviceInfo = await DeviceInformation.CreateFromIdAsync(device.Id);

        var settings = new AudioGraphSettings(AudioRenderCategory.Other)
        {
            QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency,
        };
        var graphResult = await AudioGraph.CreateAsync(settings);
        if (graphResult.Status != AudioGraphCreationStatus.Success)
            throw new InvalidOperationException($"Could not create audio graph: {graphResult.Status}");
        var graph = graphResult.Graph;

        // Passing a null format lets the graph use the device's own natural format
        // rather than forcing a resample.
        var inputResult = await graph.CreateDeviceInputNodeAsync(MediaCategory.Other, null, deviceInfo);
        if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
        {
            graph.Dispose();
            throw new InvalidOperationException($"Could not open audio input: {inputResult.Status}");
        }

        var outputNode = graph.CreateFrameOutputNode();
        inputResult.DeviceInputNode.AddOutgoingConnection(outputNode);

        lock (_levelLock)
        {
            _channelCount = (int)outputNode.EncodingProperties.ChannelCount;
            Array.Clear(_peak);
        }

        graph.QuantumStarted += OnQuantumStarted;
        graph.UnrecoverableErrorOccurred += OnUnrecoverableError;

        _graph = graph;
        _inputNode = inputResult.DeviceInputNode;
        _outputNode = outputNode;

        graph.Start();
    }

    public Task StopAsync()
    {
        if (_graph is not null)
        {
            _graph.QuantumStarted -= OnQuantumStarted;
            _graph.UnrecoverableErrorOccurred -= OnUnrecoverableError;
            _graph.Stop();
            _inputNode?.Dispose();
            _outputNode?.Dispose();
            _graph.Dispose();
            _graph = null;
            _inputNode = null;
            _outputNode = null;
        }
        lock (_levelLock)
        {
            _channelCount = 0;
            Array.Clear(_peak);
        }
        return Task.CompletedTask;
    }

    /// <summary>Linear (0..1) peak amplitude per channel seen since the last call; clears the accumulator.</summary>
    public float[] TakePeaks()
    {
        lock (_levelLock)
        {
            var copy = (float[])_peak.Clone();
            Array.Clear(_peak);
            return copy;
        }
    }

    private void OnUnrecoverableError(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args) =>
        Failed?.Invoke("Audio input stopped unexpectedly (device removed or reformatted).");

    // AudioFrameOutputNode always delivers interleaved 32-bit float PCM,
    // regardless of the device's native format.
    private unsafe void OnQuantumStarted(AudioGraph sender, object args)
    {
        var outputNode = _outputNode;
        if (outputNode is null)
            return;

        using var frame = outputNode.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        var access = reference.As<IMemoryBufferByteAccess>();
        access.GetBuffer(out var data, out var capacity);
        if (data == null || capacity == 0)
            return;

        var floats = (float*)data;
        var sampleCount = (int)(capacity / sizeof(float));
        var channels = Math.Max(1, _channelCount);
        var localPeaks = new float[Math.Min(channels, MaxChannels)];

        for (var i = 0; i < sampleCount; i++)
        {
            var ch = i % channels;
            if (ch >= localPeaks.Length)
                continue;
            var abs = Math.Abs(floats[i]);
            if (abs > localPeaks[ch])
                localPeaks[ch] = abs;
        }

        lock (_levelLock)
        {
            for (var c = 0; c < localPeaks.Length; c++)
            {
                if (localPeaks[c] > _peak[c])
                    _peak[c] = localPeaks[c];
            }
        }
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
