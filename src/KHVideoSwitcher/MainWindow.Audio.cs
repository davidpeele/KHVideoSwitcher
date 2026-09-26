using System.Windows;
using System.Windows.Controls;
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
    // doesn't get in Zoom's way capturing the same device.

    private const double MeterFloorDb = -60.0;
    private const double MeterTrackWidth = 160.0;   // must match AudioMeterGrid's Width and MeterGradientBrush's EndPoint in Theme.xaml
    private const double MeterReleasePerSecond = 24.0;      // bar fall-off
    private const double MeterHoldReleasePerSecond = 14.0;  // peak marker fall-off, once MeterPeakHoldTime has elapsed
    private static readonly TimeSpan MeterPeakHoldTime = TimeSpan.FromSeconds(1.2);

    private readonly AudioLevelMeterService _audioMeter = new();
    private readonly DispatcherTimer _audioMeterTimer = new() { Interval = TimeSpan.FromSeconds(1.0 / 30) };
    private readonly double[] _meterDisplayDb = { MeterFloorDb, MeterFloorDb };
    private readonly double[] _meterPeakHoldDb = { MeterFloorDb, MeterFloorDb };
    private readonly DateTime[] _meterPeakHoldAt = new DateTime[2];
    private DateTime _lastMeterTick = DateTime.UtcNow;
    private bool _audioMeterReady;

    private async Task InitializeAudioMeterAsync()
    {
        _audioMeter.Failed += OnAudioMeterFailed;
        await RefreshAudioDevicesAsync();
        _audioMeterTimer.Tick += AudioMeterTimer_Tick;
        _audioMeterTimer.Start();
        _audioMeterReady = true;
    }

    /// <summary>Raised on the audio thread (e.g. the device was unplugged) - marshal to the UI thread before touching controls.</summary>
    private void OnAudioMeterFailed(string message) => Dispatcher.BeginInvoke(() =>
    {
        StatusText.Text = message;
        AppLog.Warn(message);
    });

    private void AudioDeviceCombo_DropDownOpened(object? sender, EventArgs e)
    {
        if (_audioMeterReady)
            _ = RefreshAudioDevicesAsync();
    }

    private async Task RefreshAudioDevicesAsync()
    {
        try
        {
            var selectedName = (AudioDeviceCombo.SelectedItem as AudioDeviceInfo)?.Name;
            var devices = await AudioLevelMeterService.ListDevicesAsync();
            AudioDeviceCombo.ItemsSource = devices;
            AudioDeviceCombo.SelectedItem =
                devices.FirstOrDefault(d => d.Name == selectedName)
                ?? devices.FirstOrDefault(d => d.Name == _settings.LastAudioDeviceName)
                ?? devices.FirstOrDefault();
            AudioDeviceCombo.ToolTip = devices.Count == 0
                ? "No audio input devices found."
                : "Audio input to watch (e.g. the amplifier line-in) - shown for reference only, never recorded or sent anywhere by this app.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Audio device enumeration failed", ex);
        }
    }

    private async void AudioDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AudioDeviceCombo.SelectedItem is not AudioDeviceInfo device)
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
                ? Math.Clamp(20.0 * Math.Log10(sample), MeterFloorDb, 0.0)
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

    private void UpdateMeterBar(int channel, double displayDb, double peakDb)
    {
        var fill = channel == 0 ? MeterFillL : MeterFillR;
        var peakMark = channel == 0 ? MeterPeakL : MeterPeakR;

        var fillFraction = Math.Clamp((displayDb - MeterFloorDb) / -MeterFloorDb, 0.0, 1.0);
        fill.Width = fillFraction * MeterTrackWidth;

        var peakFraction = Math.Clamp((peakDb - MeterFloorDb) / -MeterFloorDb, 0.0, 1.0);
        peakMark.Margin = new Thickness(Math.Max(0, peakFraction * MeterTrackWidth - 1), 0, 0, 0);
    }

    private async Task ShutdownAudioMeterAsync()
    {
        _audioMeterTimer.Stop();
        await _audioMeter.StopAsync();
    }
}
