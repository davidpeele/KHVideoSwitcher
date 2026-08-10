using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace KHVideoSwitcher;

/// <summary>
/// Checks GitHub Releases for a newer KH Video Switcher build, and can fetch +
/// verify the installer for the caller to launch. No auto-run: the caller always
/// decides when (or whether) to actually download/install.
/// </summary>
public static class UpdateChecker
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/davidpeele/KHVideoSwitcher/releases/latest";

    public sealed record UpdateInfo(Version Version, string TagName, string HtmlUrl, string? ReleaseNotes, Uri InstallerUrl, Uri ChecksumsUrl);

    /// <summary>
    /// The version this build was stamped with via `-p:Version` (see scripts/publish.ps1),
    /// normalized to Major.Minor.Build. An unstamped dev build reads as 0.0.0.
    /// </summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KHVideoSwitcher-UpdateCheck");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Returns update info if a newer release is published, or null if already up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        // A dev build can't meaningfully compare against a real version - skip
        // rather than perpetually reporting every release as "available".
        if (CurrentVersion == new Version(0, 0, 0))
            return null;

        using var http = CreateClient();
        using var resp = await http.GetAsync(ReleasesApiUrl, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString();
        if (tag is null || !Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
            return null;
        var latest = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        if (latest.CompareTo(CurrentVersion) <= 0)
            return null;

        Uri? installerUrl = null, checksumsUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name is null || url is null)
                continue;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                installerUrl = new Uri(url);
            else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                checksumsUrl = new Uri(url);
        }
        // Release exists but the workflow hasn't finished attaching assets yet - treat as no update.
        if (installerUrl is null || checksumsUrl is null)
            return null;

        var htmlUrl = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null;
        var notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;

        return new UpdateInfo(latest, tag, htmlUrl ?? $"https://github.com/davidpeele/KHVideoSwitcher/releases/tag/{tag}", notes, installerUrl, checksumsUrl);
    }

    /// <summary>
    /// Downloads the installer to a temp file and verifies it against the release's
    /// published SHA256SUMS.txt before returning - mirrors the verify-before-execute
    /// pattern the installer itself uses for its .NET runtime download (see
    /// installer/KHVideoSwitcher.iss, InstallDesktopRuntimeQuietly). Throws, and
    /// deletes whatever was downloaded, on any transport or checksum failure.
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = CreateClient();

        var sums = await http.GetStringAsync(info.ChecksumsUrl, ct);
        var installerName = Path.GetFileName(info.InstallerUrl.LocalPath);
        var expectedHash = ParseSha256Sums(sums, installerName)
            ?? throw new InvalidOperationException($"No checksum found for {installerName} in SHA256SUMS.txt.");

        var tempPath = Path.Combine(Path.GetTempPath(), installerName);
        try
        {
            using (var resp = await http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength;
                await using var httpStream = await resp.Content.ReadAsStreamAsync(ct);
                await using var fileStream = File.Create(tempPath);

                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await httpStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total is > 0)
                        progress?.Report((double)read / total.Value);
                }
            }

            string actualHash;
            await using (var verifyStream = File.OpenRead(tempPath))
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verifyStream, ct)).ToLowerInvariant();

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Downloaded installer failed checksum verification - discarded, not installed.");
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }

        return tempPath;
    }

    private static string? ParseSha256Sums(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            var parts = trimmed.Split([' ', '*'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[1], fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Launches the downloaded installer (Windows shows the UAC/wizard prompts) and exits this app so its own files aren't locked during the upgrade.</summary>
    public static void LaunchInstallerAndExit(string installerPath)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }
}
