using System.Windows;
using System.Windows.Controls;
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
        _loading = false;
        UpdateLabels();
        UpdateHideBorderTag();
    }

    private void Any_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.FadeMs = (int)FadeSlider.Value;
        _settings.OtsInsetWidthFraction = InsetSizeSlider.Value / 100.0;
        _settings.OtsInsetTopMargin = InsetTopSlider.Value;
        _settings.OtsInsetRightMargin = InsetRightSlider.Value;
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

    private void UpdateLabels()
    {
        FadeLabel.Text = $"{(int)FadeSlider.Value} ms";
        InsetSizeLabel.Text = $"{(int)InsetSizeSlider.Value}%";
        InsetTopLabel.Text = $"{(int)InsetTopSlider.Value} px";
        InsetRightLabel.Text = $"{(int)InsetRightSlider.Value} px";
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
}
