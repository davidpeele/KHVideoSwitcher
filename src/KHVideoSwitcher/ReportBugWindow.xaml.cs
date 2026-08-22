using System.Diagnostics;
using System.Windows;

namespace KHVideoSwitcher;

/// <summary>
/// Lets a tester assemble a bug report and send it themselves - nothing here
/// transmits anything automatically. The diagnostics text is pre-scrubbed of
/// the Windows user path (see Diagnostics.AppLog.Scrub) and shown fully
/// editable, so what the tester reviews on screen is exactly what would be
/// shared, and they can delete anything before it goes anywhere.
/// </summary>
public partial class ReportBugWindow : Window
{
    private const string IssuesUrl = "https://github.com/davidpeele/KHVideoSwitcher/issues/new";

    // Keeps the prefilled-draft URL well under browser/URL length limits; the
    // clipboard copy (done first, same click) always has the untruncated text.
    private const int MaxUrlDiagnosticsChars = 3000;

    public ReportBugWindow(string diagnostics)
    {
        InitializeComponent();
        DiagnosticsBox.Text = diagnostics;
        DescriptionBox.Focus();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(FullText());
            StatusText.Text = "Copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't copy to clipboard: {ex.Message}";
        }
    }

    private void OpenIssueButton_Click(object sender, RoutedEventArgs e)
    {
        var clipboardOk = true;
        try
        {
            Clipboard.SetText(FullText());
        }
        catch
        {
            clipboardOk = false;
        }

        var diagnostics = DiagnosticsBox.Text;
        var truncated = diagnostics.Length > MaxUrlDiagnosticsChars;
        var bodyDiagnostics = truncated ? diagnostics[..MaxUrlDiagnosticsChars] + "\n...(truncated for the link)" : diagnostics;
        var clipboardNote = clipboardOk
            ? "(The full report was also copied to your clipboard — paste with Ctrl+V to replace this if anything above looks cut off.)"
            : "";
        var body = $"{DescriptionBox.Text.Trim()}\n\n{bodyDiagnostics}\n\n{clipboardNote}".TrimEnd();
        var url = $"{IssuesUrl}?labels=bug&title={Uri.EscapeDataString("Bug report")}&body={Uri.EscapeDataString(body)}";

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            StatusText.Text = "Opened GitHub in your browser with a draft — review it, then click Submit new issue.";
        }
        catch (Exception ex)
        {
            StatusText.Text = clipboardOk
                ? $"Couldn't open the browser ({ex.Message}). The full report is on your clipboard — paste it into a new issue at github.com/davidpeele/KHVideoSwitcher/issues."
                : $"Couldn't open the browser ({ex.Message}). Copy the text above and paste it into a new issue at github.com/davidpeele/KHVideoSwitcher/issues.";
        }
    }

    private string FullText() => $"{DescriptionBox.Text.Trim()}\n\n{DiagnosticsBox.Text}";
}
