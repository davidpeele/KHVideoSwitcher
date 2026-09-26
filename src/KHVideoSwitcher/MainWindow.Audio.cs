using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using KHVideoSwitcher.Audio;
using KHVideoSwitcher.Diagnostics;

namespace KHVideoSwitcher;

public partial class MainWindow
{
    // ---------- audio input level meter ----------
    // Purely a visual reference for the level dialed in at the sound booth
    // (the amplifier feeds a line-in on the PC) - the same job the two little
    // meters do in OBS's audio mixer. This app never records, sends, or mixes
    // that audio; it just watches the input's peak level in shared mode, so it
    // doesn't get in Zoom's way capturing the same device. The device picker
    // itself lives in Settings (picked once); this file only drives the bars.

    private const double MeterFloorDb = -60.0;
    private const double MeterReleasePerSecond = 24.0;      // bar fall-off
    private const double MeterHoldReleasePerSecond = 14.0;  // peak marker fall-off, once MeterPeakHoldTime has elapsed
    private static readonly TimeSpan MeterPeakHoldTime = TimeSpan.FromSeconds(1.2);
    private static readonly Color MeterGreenColor = Color.FromRgb(0x4C, 0x9F, 0x70);
    private static readonly Color MeterYellowColor = Color.FromRgb(0xD9, 0xA6, 0x3C);
    private static readonly Color MeterRedColor = Color.FromRgb(0xC1, 0x54, 0x4B);

    private readonly AudioLevelMeterService _audioMeter = new();
    private readonly DispatcherTimer _audioMeterTimer = new() { Interval = TimeSpan.FromSeconds(1.0 / 30) };
    private readonly double[] _meterDisplayDb = { MeterFloorDb, MeterFloorDb };
    private readonly double[] _meterPeakHoldDb = { MeterFloorDb, MeterFloorDb };
    private readonly DateTime[] _meterPeakHoldAt = new DateTime[2];
    private DateTime _lastMeterTick = DateTime.UtcNow;
    private double _meterTrackWidth = 300.0;   // corrected to the pane's real width as soon as it's laid out

    private async Task InitializeAudioMeterAsync()
    {
        _audioMeter.Failed += OnAudioMeterFailed;
        _audioMeterTimer.Tick += AudioMeterTimer_Tick;
        _audioMeterTimer.Start();
        ApplyAudioMeterVisibility();

        MeterTrackL.SizeChanged += (_, e) => UpdateMeterTrackWidth(e.NewSize.Width);
        UpdateMeterTrackWidth(MeterTrackL.ActualWidth);

        try
        {
            var devices = await AudioLevelMeterService.ListDevicesAsync();
            var device = devices.FirstOrDefault(d => d.Name == _settings.LastAudioDeviceName) ?? devices.FirstOrDefault();
            if (device is not null)
                await SetAudioDeviceAsync(device);
        }
        catch (Exception ex)
        {
            AppLog.Error("Audio device enumeration failed", ex);
        }
    }

    /// <summary>Shows/hides the audio meter (under PROGRAM) per the SHOW AUDIO METER setting.</summary>
    private void ApplyAudioMeterVisibility() =>
        AudioMeterPane.Visibility = _settings.ShowAudioMeter ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Raised on the audio thread (e.g. the device was unplugged) - marshal to the UI thread before touching controls.</summary>
    private void OnAudioMeterFailed(string message) => Dispatcher.BeginInvoke(() =>
    {
        StatusText.Text = message;
        AppLog.Warn(message);
    });

    /// <summary>Opens (or closes, for a null device) the audio input the meter watches. Called from Settings' device picker.</summary>
    public async Task SetAudioDeviceAsync(AudioDeviceInfo? device)
    {
        if (device is null)
        {
            await _audioMeter.StopAsync();
            return;
        }

        try
        {
            await _audioMeter.StartAsync(device);
            _settings.LastAudioDeviceName = device.Name;
            _settings.Save();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open audio input '{device.Name}': {ex.Message}";
            AppLog.Error($"Audio meter failed to start ({device.Name})", ex);
        }
    }

    private void AudioMeterTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min(0.2, (now - _lastMeterTick).TotalSeconds);
        _lastMeterTick = now;

        var linearPeaks = _audioMeter.IsRunning ? _audioMeter.TakePeaks() : Array.Empty<float>();

        for (var ch = 0; ch < 2; ch++)
        {
            // A mono input drives both bars so a single line-in still reads as a
            // meter instead of leaving one side permanently blank.
            var sample = linearPeaks.Length > 0 ? (double)linearPeaks[Math.Min(ch, linearPeaks.Length - 1)] : 0.0;
            var peakDb = sample > 0.0
                ? Math.Clamp(20.0 * Math.Log10(sample) + _settings.AudioMeterGainDb, MeterFloorDb, 0.0)
                : MeterFloorDb;

            _meterDisplayDb[ch] = peakDb > _meterDisplayDb[ch]
                ? peakDb
                : Math.Max(MeterFloorDb, _meterDisplayDb[ch] - MeterReleasePerSecond * dt);

            if (peakDb >= _meterPeakHoldDb[ch])
            {
                _meterPeakHoldDb[ch] = peakDb;
                _meterPeakHoldAt[ch] = now;
            }
            else if (now - _meterPeakHoldAt[ch] > MeterPeakHoldTime)
            {
                _meterPeakHoldDb[ch] = Math.Max(MeterFloorDb, _meterPeakHoldDb[ch] - MeterHoldReleasePerSecond * dt);
            }

            UpdateMeterBar(ch, _meterDisplayDb[ch], _meterPeakHoldDb[ch]);
        }
    }

    /// <summary>
    /// The meter now spans the PROGRAM pane's full (resizable) width, so unlike a fixed
    /// toolbar meter its pixel width changes with the window. The fill/peak fractions
    /// stay simple percentages of that width, but the color-zone gradient is anchored in
    /// absolute pixels (so green/yellow/red always mean the same dB) and must be rebuilt,
    /// and the -24/-12 scale labels repositioned, whenever the width changes.
    /// </summary>
    private void UpdateMeterTrackWidth(double width)
    {
        if (width <= 0)
            return;
        _meterTrackWidth = width;

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(_meterTrackWidth, 0),
            MappingMode = BrushMappingMode.Absolute,
        };
        brush.GradientStops.Add(new GradientStop(MeterGreenColor, 0));
        brush.GradientStops.Add(new GradientStop(MeterGreenColor, 0.80));
        brush.GradientStops.Add(new GradientStop(MeterYellowColor, 0.80));
        brush.GradientStops.Add(new GradientStop(MeterYellowColor, 0.95));
        brush.GradientStops.Add(new GradientStop(MeterRedColor, 0.95));
        brush.GradientStops.Add(new GradientStop(MeterRedColor, 1));
        brush.Freeze();
        MeterFillL.Background = brush;
        MeterFillR.Background = brush;

        // -24dB and -12dB sit at 60% and 80% of the way from the -60dB floor to 0dB -
        // the same fractions as the gradient's yellow/red breakpoints above.
        MeterScale24.Margin = new Thickness(Math.Max(0, _meterTrackWidth * 0.60 - 8), 0, 0, 0);
        MeterScale12.Margin = new Thickness(Math.Max(0, _meterTrackWidth * 0.80 - 8), 0, 0, 0);

        UpdateMeterBar(0, _meterDisplayDb[0], _meterPeakHoldDb[0]);
        UpdateMeterBar(1, _meterDisplayDb[1], _meterPeakHoldDb[1]);
    }

    private void UpdateMeterBar(int channel, double displayDb, double peakDb)
    {
        var fill = channel == 0 ? MeterFillL : MeterFillR;
        var peakMark = channel == 0 ? MeterPeakL : MeterPeakR;
        var readout = channel == 0 ? MeterReadoutL : MeterReadoutR;

        var fillFraction = Math.Clamp((displayDb - MeterFloorDb) / -MeterFloorDb, 0.0, 1.0);
        fill.Width = fillFraction * _meterTrackWidth;

        var peakFraction = Math.Clamp((peakDb - MeterFloorDb) / -MeterFloorDb, 0.0, 1.0);
        peakMark.Margin = new Thickness(Math.Max(0, peakFraction * _meterTrackWidth - 1), 0, 0, 0);

        readout.Text = peakDb <= MeterFloorDb ? "-∞" : peakDb.ToString("0.0");
    }

    private async Task ShutdownAudioMeterAsync()
    {
        _audioMeterTimer.Stop();
        await _audioMeter.StopAsync();
    }
}
