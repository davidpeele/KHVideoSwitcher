using System.Runtime.InteropServices;
using System.Text;
using KHVideoSwitcher.Video;

namespace KHVideoSwitcher.Diagnostics;

/// <summary>
/// Assembles the text a tester reviews before sending a bug report: app/OS
/// version and the handful of settings that affect behavior, plus a tail of
/// the local log. Deliberately excludes anything about the meeting itself, the
/// Hall, or the operator - only what helps reproduce a technical issue. Run
/// through AppLog.Scrub the same way the log file is, so what's shown on
/// screen is exactly what would be shared.
/// </summary>
public static class DiagnosticsReport
{
    public static string Build(AppSettings settings, string? cameraName, string? mediaTargetName, bool vcamRunning)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Diagnostics (review before sending - edit or remove anything you don't want to share) ---");
        sb.AppendLine($"App version: {UpdateChecker.CurrentVersion}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"Camera: {cameraName ?? "(none selected)"}");
        sb.AppendLine($"Media capture target: {mediaTargetName ?? "(none selected)"}");
        sb.AppendLine($"Virtual camera running: {vcamRunning}");
        sb.AppendLine($"Settings: FadeMs={settings.FadeMs}, AutoTakeScenes={settings.AutoTakeScenes}, " +
                       $"AutoScenes={settings.AutoScenes}, DragMovesPicture={settings.DragMovesPicture}, " +
                       $"HideCaptureBorder={settings.HideCaptureBorder}, AlwaysFullScreenFirst={settings.AlwaysFullScreenFirst}, " +
                       $"OtsShiftCameraForInset={settings.OtsShiftCameraForInset}, OtsShiftFraction={settings.OtsShiftFraction:0.00}");
        sb.AppendLine();
        sb.AppendLine("--- Recent log (last 200 lines) ---");
        sb.AppendLine(AppLog.TailRecentLines());
        return AppLog.Scrub(sb.ToString());
    }
}
