using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KHVideoSwitcher.Audio;
using KHVideoSwitcher.Diagnostics;
using KHVideoSwitcher.Video;

namespace KHVideoSwitcher;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private readonly Func<AudioDeviceInfo?, Task> _onAudioDeviceSelected;
    private bool _loading = true;
    private UpdateChecker.UpdateInfo? _pendingUpdate;
    private bool _updateDownloading;

    public SettingsWindow(AppSettings settings, Action onChanged, Func<AudioDeviceInfo?, Task> onAudioDeviceSelected)
    {
        InitializeComponent();
        _settings = settings;
        _onChanged = onChanged;
        _onAudioDeviceSelected = onAudioDeviceSelected;

        FadeSlider.Value = _settings.FadeMs;
        InsetSizeSlider.Value = _settings.OtsInsetWidthFraction * 100;
        InsetTopSlider.Value = _settings.OtsInsetTopMargin;
        InsetRightSlider.Value = _settings.OtsInsetRightMargin;
        ShiftAmountSlider.Value = _settings.OtsShiftFraction * 100;
        AudioGainSlider.Value = _settings.AudioMeterGainDb;
        SetRgbSliders(BgRSlider, BgGSlider, BgBSlider, _settings.OtsBackgroundColor);
        SetRgbSliders(BorderRSlider, BorderGSlider, BorderBSlider, _settings.OtsBorderColor);
        _loading = false;
        UpdateLabels();
        UpdateHideBorderTag();
        UpdateOtsShiftTag();
        UpdateFullScreenFirstTag();
        UpdateShowAudioMeterTag();
        UpdateShowOtsBorderTag();
        UpdateColorPreviews();
        VersionText.Text = $"v{UpdateChecker.CurrentVersion}";
        _ = RefreshAudioDevicesAsync();
    }

    private void Any_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.FadeMs = (int)FadeSlider.Value;
        _settings.OtsInsetWidthFraction = InsetSizeSlider.Value / 100.0;
        _settings.OtsInsetTopMargin = InsetTopSlider.Value;
        _settings.OtsInsetRightMargin = InsetRightSlider.Value;
        _settings.OtsShiftFraction = ShiftAmountSlider.Value / 100.0;
        _settings.AudioMeterGainDb = AudioGainSlider.Value;
        UpdateLabels();
        _onChanged();
    }

    private void ShowAudioMeterTag_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowAudioMeter = !_settings.ShowAudioMeter;
        UpdateShowAudioMeterTag();
        _onChanged();
    }

    private void UpdateShowAudioMeterTag()
    {
        var accent = (System.Windows.Media.Brush)Application.Current.Resources["AccentBrush"];
        var bg = (System.Windows.Media.Brush)Application.Current.Resources["BgBrush"];
        var text = (System.Windows.Media.Brush)Application.Current.Resources["TextBrush"];
        var divider = (System.Windows.Media.Brush)Application.Current.Resources["DividerBrush"];

        if (_settings.ShowAudioMeter)
        {
            ShowAudioMeterTag.Background = accent;
            ShowAudioMeterTag.Foreground = bg;
            ShowAudioMeterTag.BorderBrush = accent;
        }
        else
        {
            ShowAudioMeterTag.Background = System.Windows.Media.Brushes.Transparent;
            ShowAudioMeterTag.Foreground = text;
            ShowAudioMeterTag.BorderBrush = divider;
        }
        ShowAudioMeterStateRun.Text = _settings.ShowAudioMeter ? "ON" : "OFF";
    }

    private void AudioDeviceCombo_DropDownOpened(object? sender, EventArgs e) => _ = RefreshAudioDevicesAsync();

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
        if (_loading)
            return;

        // Refreshing the list re-selects whatever's already active, which would otherwise
        // restart the audio graph every time this window (or its dropdown) is opened.
        var device = AudioDeviceCombo.SelectedItem as AudioDeviceInfo;
        if (device?.Name == _settings.LastAudioDeviceName)
            return;

        await _onAudioDeviceSelected(device);
    }

    private void HideBorderTag_Click(object sender, RoutedEventArgs e)
    {
        _settings.HideCaptureBorder = !_settings.HideCaptureBorder;
        UpdateHideBorderTag();
        _onChanged();
    }

    private void UpdateHideBorderTag()
    {
        var accent = (System.Windows.Media.Brush)Application.Current.Resources["AccentBrush"];
        var bg = (System.Windows.Media.Brush)Application.Current.Resources["BgBrush"];
        var text = (System.Windows.Media.Brush)Application.Current.Resources["TextBrush"];
        var divider = (System.Windows.Media.Brush)Application.Current.Resources["DividerBrush"];

        if (_settings.HideCaptureBorder)
        {
            HideBorderTag.Background = accent;
            HideBorderTag.Foreground = bg;
            HideBorderTag.BorderBrush = accent;
        }
        else
        {
            HideBorderTag.Background = System.Windows.Media.Brushes.Transparent;
            HideBorderTag.Foreground = text;
            HideBorderTag.BorderBrush = divider;
        }
        HideBorderStateRun.Text = _settings.HideCaptureBorder ? "ON" : "OFF";
    }

    private void OtsShiftTag_Click(object sender, RoutedEventArgs e)
    {
        _settings.OtsShiftCameraForInset = !_settings.OtsShiftCameraForInset;
        UpdateOtsShiftTag();
        _onChanged();
    }

    private void UpdateOtsShiftTag()
    {
        var accent = (System.Windows.Media.Brush)Application.Current.Resources["AccentBrush"];
        var bg = (System.Windows.Media.Brush)Application.Current.Resources["BgBrush"];
        var text = (System.Windows.Media.Brush)Application.Current.Resources["TextBrush"];
        var divider = (System.Windows.Media.Brush)Application.Current.Resources["DividerBrush"];

        if (_settings.OtsShiftCameraForInset)
        {
            OtsShiftTag.Background = accent;
            OtsShiftTag.Foreground = bg;
            OtsShiftTag.BorderBrush = accent;
        }
        else
        {
            OtsShiftTag.Background = System.Windows.Media.Brushes.Transparent;
            OtsShiftTag.Foreground = text;
            OtsShiftTag.BorderBrush = divider;
        }
        OtsShiftStateRun.Text = _settings.OtsShiftCameraForInset ? "ON" : "OFF";
    }

    private void FullScreenFirstTag_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysFullScreenFirst = !_settings.AlwaysFullScreenFirst;
        UpdateFullScreenFirstTag();
        _onChanged();
    }

    private void UpdateFullScreenFirstTag()
    {
        var accent = (System.Windows.Media.Brush)Application.Current.Resources["AccentBrush"];
        var bg = (System.Windows.Media.Brush)Application.Current.Resources["BgBrush"];
        var text = (System.Windows.Media.Brush)Application.Current.Resources["TextBrush"];
        var divider = (System.Windows.Media.Brush)Application.Current.Resources["DividerBrush"];

        if (_settings.AlwaysFullScreenFirst)
        {
            FullScreenFirstTag.Background = accent;
            FullScreenFirstTag.Foreground = bg;
            FullScreenFirstTag.BorderBrush = accent;
        }
        else
        {
            FullScreenFirstTag.Background = System.Windows.Media.Brushes.Transparent;
            FullScreenFirstTag.Foreground = text;
            FullScreenFirstTag.BorderBrush = divider;
        }
        FullScreenFirstStateRun.Text = _settings.AlwaysFullScreenFirst ? "ON" : "OFF";
    }

    // ---------- OTS background / border color pickers ----------

    private void BgPreset_Click(object sender, RoutedEventArgs e) => ApplyBgColor((string)((Button)sender).Tag);

    private void BgRgb_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        _settings.OtsBackgroundColor = RgbToHex(BgRSlider.Value, BgGSlider.Value, BgBSlider.Value);
        UpdateColorPreviews();
        _onChanged();
    }

    private void ApplyBgColor(string hex)
    {
        // Suppress BgRgb_ValueChanged while the three sliders are set individually below,
        // so picking a preset doesn't fire three redundant onChanged/save round-trips.
        var wasLoading = _loading;
        _loading = true;
        SetRgbSliders(BgRSlider, BgGSlider, BgBSlider, hex);
        _loading = wasLoading;

        _settings.OtsBackgroundColor = hex;
        UpdateColorPreviews();
        _onChanged();
    }

    private void BorderPreset_Click(object sender, RoutedEventArgs e) => ApplyBorderColor((string)((Button)sender).Tag);

    private void BorderRgb_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        _settings.OtsBorderColor = RgbToHex(BorderRSlider.Value, BorderGSlider.Value, BorderBSlider.Value);
        UpdateColorPreviews();
        _onChanged();
    }

    private void ApplyBorderColor(string hex)
    {
        var wasLoading = _loading;
        _loading = true;
        SetRgbSliders(BorderRSlider, BorderGSlider, BorderBSlider, hex);
        _loading = wasLoading;

        _settings.OtsBorderColor = hex;
        UpdateColorPreviews();
        _onChanged();
    }

    private void ShowOtsBorderTag_Click(object sender, RoutedEventArgs e)
    {
        _settings.OtsBorderEnabled = !_settings.OtsBorderEnabled;
        UpdateShowOtsBorderTag();
        _onChanged();
    }

    private void UpdateShowOtsBorderTag()
    {
        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        var bg = (Brush)Application.Current.Resources["BgBrush"];
        var text = (Brush)Application.Current.Resources["TextBrush"];
        var divider = (Brush)Application.Current.Resources["DividerBrush"];

        if (_settings.OtsBorderEnabled)
        {
            ShowOtsBorderTag.Background = accent;
            ShowOtsBorderTag.Foreground = bg;
            ShowOtsBorderTag.BorderBrush = accent;
        }
        else
        {
            ShowOtsBorderTag.Background = Brushes.Transparent;
            ShowOtsBorderTag.Foreground = text;
            ShowOtsBorderTag.BorderBrush = divider;
        }
        ShowOtsBorderStateRun.Text = _settings.OtsBorderEnabled ? "ON" : "OFF";
    }

    private static void SetRgbSliders(Slider r, Slider g, Slider b, string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        r.Value = c.R;
        g.Value = c.G;
        b.Value = c.B;
    }

    private static string RgbToHex(double r, double g, double b) => $"#{(byte)r:X2}{(byte)g:X2}{(byte)b:X2}";

    private void UpdateColorPreviews()
    {
        BgRLabel.Text = ((int)BgRSlider.Value).ToString();
        BgGLabel.Text = ((int)BgGSlider.Value).ToString();
        BgBLabel.Text = ((int)BgBSlider.Value).ToString();
        BgColorPreview.Background = new SolidColorBrush(Color.FromRgb((byte)BgRSlider.Value, (byte)BgGSlider.Value, (byte)BgBSlider.Value));

        BorderRLabel.Text = ((int)BorderRSlider.Value).ToString();
        BorderGLabel.Text = ((int)BorderGSlider.Value).ToString();
        BorderBLabel.Text = ((int)BorderBSlider.Value).ToString();
        BorderColorPreview.Background = new SolidColorBrush(Color.FromRgb((byte)BorderRSlider.Value, (byte)BorderGSlider.Value, (byte)BorderBSlider.Value));
    }

    private void UpdateLabels()
    {
        FadeLabel.Text = $"{(int)FadeSlider.Value} ms";
        InsetSizeLabel.Text = $"{(int)InsetSizeSlider.Value}%";
        InsetTopLabel.Text = $"{(int)InsetTopSlider.Value} px";
        InsetRightLabel.Text = $"{(int)InsetRightSlider.Value} px";
        ShiftAmountLabel.Text = $"{(int)ShiftAmountSlider.Value}%";
        AudioGainLabel.Text = $"{(int)AudioGainSlider.Value:+0;-0;0} dB";
        UpdateInsetPreview();
    }

    /// <summary>
    /// Preview of the OTS inset box, scaled down from the real 1920x1080 output
    /// (PtzCompositor.OutWidth/OutHeight) so it actually matches where the box
    /// lands in PREVIEW - the top/right margin sliders are real output pixels,
    /// not preview pixels, so they must be scaled rather than applied at face value.
    /// </summary>
    private void UpdateInsetPreview()
    {
        const double previewW = 220;
        const double previewH = 130;
        var scaleX = previewW / PtzCompositor.OutWidth;
        var scaleY = previewH / PtzCompositor.OutHeight;

        double boxW = previewW * (InsetSizeSlider.Value / 100.0);
        double boxH = boxW * 9.0 / 16.0;

        InsetPreviewBox.Width = boxW;
        InsetPreviewBox.Height = boxH;
        Canvas.SetTop(InsetPreviewBox, InsetTopSlider.Value * scaleY);
        Canvas.SetLeft(InsetPreviewBox, previewW - InsetRightSlider.Value * scaleX - boxW);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- update check ----------

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking...";
        try
        {
            var update = await UpdateChecker.CheckAsync();
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _settings.Save();
            _pendingUpdate = update;
            UpdateStatusText.Text = update is null
                ? "You're up to date."
                : $"{update.TagName} is available — click UPDATE below to install.";
            UpdateSettingsUpdateTag();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Check failed: {ex.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void UpdateSettingsUpdateTag()
    {
        if (_pendingUpdate is null)
        {
            SettingsUpdateTag.Visibility = Visibility.Collapsed;
            return;
        }

        var accent700 = (System.Windows.Media.Brush)Application.Current.Resources["Accent700Brush"];
        SettingsUpdateTag.Visibility = Visibility.Visible;
        SettingsUpdateTag.Background = System.Windows.Media.Brushes.Transparent;
        SettingsUpdateTag.Foreground = accent700;
        SettingsUpdateTag.BorderBrush = accent700;
        SettingsUpdateTag.BorderThickness = new Thickness(1.5);
        SettingsUpdateTag.Template = (ControlTemplate)Application.Current.Resources["StatusTagDashedTemplate"];
        SettingsUpdateStateRun.Text = _pendingUpdate.TagName;
    }

    private async void SettingsUpdateTag_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null || _updateDownloading)
            return;

        var info = _pendingUpdate;
        var result = MessageBox.Show(this,
            $"KH Video Switcher {info.TagName} is available (you're on v{UpdateChecker.CurrentVersion}).\n\n" +
            "Download and install it now? The app will close and the installer will open — " +
            "Windows may ask for administrator approval.",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        _updateDownloading = true;
        SettingsUpdateTag.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateStatusText.Text = $"Downloading {info.TagName}... {p:P0}");
            UpdateStatusText.Text = $"Downloading {info.TagName}...";
            var installerPath = await UpdateChecker.DownloadInstallerAsync(info, progress, CancellationToken.None);
            UpdateStatusText.Text = "Verified. Launching installer...";
            UpdateChecker.LaunchInstallerAndExit(installerPath);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update download failed: {ex.Message}";
            _updateDownloading = false;
            SettingsUpdateTag.IsEnabled = true;
        }
    }
}
