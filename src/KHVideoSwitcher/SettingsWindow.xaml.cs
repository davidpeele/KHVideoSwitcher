using System.Windows;
using System.Windows.Controls;
using KHVideoSwitcher.Video;

namespace KHVideoSwitcher;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private bool _loading = true;
    private UpdateChecker.UpdateInfo? _pendingUpdate;
    private bool _updateDownloading;

    public SettingsWindow(AppSettings settings, Action onChanged)
    {
        InitializeComponent();
        _settings = settings;
        _onChanged = onChanged;

        FadeSlider.Value = _settings.FadeMs;
        InsetSizeSlider.Value = _settings.OtsInsetWidthFraction * 100;
        InsetTopSlider.Value = _settings.OtsInsetTopMargin;
        InsetRightSlider.Value = _settings.OtsInsetRightMargin;
        ShiftAmountSlider.Value = _settings.OtsShiftFraction * 100;
        _loading = false;
        UpdateLabels();
        UpdateHideBorderTag();
        UpdateOtsShiftTag();
        UpdateFullScreenFirstTag();
        VersionText.Text = $"v{UpdateChecker.CurrentVersion}";
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
        UpdateLabels();
        _onChanged();
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

    private void UpdateLabels()
    {
        FadeLabel.Text = $"{(int)FadeSlider.Value} ms";
        InsetSizeLabel.Text = $"{(int)InsetSizeSlider.Value}%";
        InsetTopLabel.Text = $"{(int)InsetTopSlider.Value} px";
        InsetRightLabel.Text = $"{(int)InsetRightSlider.Value} px";
        ShiftAmountLabel.Text = $"{(int)ShiftAmountSlider.Value}%";
        UpdateInsetPreview();
    }

    /// <summary>
    /// Illustrative preview of the OTS inset box, matching the design mockup:
    /// the top/right margin sliders (0-80px) are drawn at face value against
    /// this 220x130 box rather than scaled from the real 1920x1080 output, so
    /// the margin's effect stays visible across its whole range.
    /// </summary>
    private void UpdateInsetPreview()
    {
        const double previewW = 220;
        double boxW = previewW * (InsetSizeSlider.Value / 100.0);
        double boxH = boxW * 9.0 / 16.0;

        InsetPreviewBox.Width = boxW;
        InsetPreviewBox.Height = boxH;
        Canvas.SetTop(InsetPreviewBox, InsetTopSlider.Value);
        Canvas.SetLeft(InsetPreviewBox, previewW - InsetRightSlider.Value - boxW);
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
