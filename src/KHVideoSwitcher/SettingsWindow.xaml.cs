using System.Windows;
using KHVideoSwitcher.Video;

namespace KHVideoSwitcher;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private bool _loading = true;

    public SettingsWindow(AppSettings settings, Action onChanged)
    {
        InitializeComponent();
        _settings = settings;
        _onChanged = onChanged;

        FadeSlider.Value = _settings.FadeMs;
        InsetSizeSlider.Value = _settings.OtsInsetWidthFraction * 100;
        InsetTopSlider.Value = _settings.OtsInsetTopMargin;
        InsetRightSlider.Value = _settings.OtsInsetRightMargin;
        HideBorderCheck.IsChecked = _settings.HideCaptureBorder;
        _loading = false;
        UpdateLabels();
    }

    private void Any_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.FadeMs = (int)FadeSlider.Value;
        _settings.OtsInsetWidthFraction = InsetSizeSlider.Value / 100.0;
        _settings.OtsInsetTopMargin = InsetTopSlider.Value;
        _settings.OtsInsetRightMargin = InsetRightSlider.Value;
        _settings.HideCaptureBorder = HideBorderCheck.IsChecked ?? true;
        UpdateLabels();
        _onChanged();
    }

    private void UpdateLabels()
    {
        FadeLabel.Text = $"{(int)FadeSlider.Value} ms";
        InsetSizeLabel.Text = $"{(int)InsetSizeSlider.Value}%";
        InsetTopLabel.Text = $"{(int)InsetTopSlider.Value} px";
        InsetRightLabel.Text = $"{(int)InsetRightSlider.Value} px";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
