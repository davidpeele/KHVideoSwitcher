# KH Video Switcher

A free, single-purpose video switcher for streaming Kingdom Hall meetings over
Zoom (or any app that accepts a webcam) — built to replace an OBS setup with
something meeting operators can run with a few clicks.

- **Virtual camera** — appears in Zoom as a camera named "KH Video Switcher";
  no drivers, uses the Windows 11 built-in virtual camera framework.
- **Virtual PTZ** — digitally pan/tilt/zoom a fixed wide-shot webcam. Frame the
  next shot in a PREVIEW pane (drag to pan, slider or wheel to zoom), then
  crossfade to it with TAKE — viewers never see the framing happen. Six
  one-click presets store complete shots.
- **Scenes** — camera (CAM), fullscreen media (MEDIA), and over-the-shoulder
  (OTS: camera behind, media boxed top-right; box size and borders adjustable
  in Settings).
- **JW Library aware** — captures the media window/display and detects whether
  a video is playing, a still image is up, or the yeartext screen is showing.
  With AUTO on, it switches scenes for you: video → MEDIA, still → OTS,
  nothing → CAM. The operator can always override; sing-along lyric videos are
  taken manually with one tap of MEDIA (F2) and held automatically after that.

## Requirements

- Windows 11 (the virtual camera uses a Windows 11 API)
- A webcam (1080p works well; 4K gives more zoom headroom)
- .NET 10 Desktop Runtime (the installer fetches it automatically if missing)

## Installing

1. Download the latest `KHVideoSwitcher-Setup-X.Y.Z.exe` from
   [Releases](../../releases).
2. Double-click it, click **Yes** on the one administrator prompt (this is
   Windows asking permission to register the virtual camera and install to
   Program Files — the same prompt every desktop installer shows), and click
   through the wizard.
3. That's it — Start Menu and Desktop shortcuts are created, the virtual
   camera is registered, and the .NET 10 Desktop Runtime is installed
   automatically if it isn't already present.

> **Expect a SmartScreen warning.** The downloads are not Authenticode-signed
> (a code-signing certificate is a recurring paid expense this volunteer
> project doesn't carry), so Windows shows *"Windows protected your PC"* on
> first run — click **More info → Run anyway**. To confirm you have the genuine
> file, compare its hash against `SHA256SUMS.txt` on the release page:
>
> ```powershell
> Get-FileHash .\KHVideoSwitcher-Setup-1.1.0.exe -Algorithm SHA256
> ```

To uninstall, use **Settings → Apps** (or **Add/Remove Programs**) like any
other Windows application, or run the **Uninstall KH Video Switcher** shortcut
in the Start Menu.

For unattended deployment to several machines, the installer supports Inno
Setup's standard `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` switches.

<details>
<summary>Advanced alternative: script-based install (no installer exe)</summary>

**Prefer the installer above.** This path exists for scripted or managed
deployments where a plain folder is easier to work with. Download
`KHVideoSwitcher-vX.Y.Z.zip` instead, extract it, then right-click
`install.ps1` → **Run with PowerShell** and accept the administrator prompt.
Uninstall with `install.ps1 -Uninstall`.

Note that if PowerShell's execution policy blocks the script, running it with
`-ExecutionPolicy Bypass` removes a safety check while executing code as
administrator. Only do that with a copy whose SHA-256 you have verified against
the release page.

</details>

## First-time setup (once per machine)

1. Launch **KH Video Switcher**, pick your camera, click **Start**.
2. Open the **Media** dropdown, pick the JW Library media window (or the
   monitor that shows media), click **Capture**.
3. With JW Library showing its normal yeartext/no-media screen, click
   **Set Stock** — this teaches the app what "nothing is playing" looks like.
   (The button glows amber whenever this step is needed.)
4. Frame your shots in PREVIEW and save presets: line up a shot, right-click a
   preset button (or Ctrl+1…6). Presets store the scene too — e.g. an
   over-the-shoulder with the speaker offset left.
5. Click **Virtual Camera: Off** to turn it ON, then select
   **"KH Video Switcher"** as the camera in Zoom.
6. Optional: press **F4** to enable AUTO scene switching.

Everything (devices, presets, stock screen, settings) is remembered, so
meeting-day startup is: open app → Start → Capture → Virtual Camera ON.

## Operator quick reference

| Action | Control |
|---|---|
| Fade / cut Program to the PREVIEW shot | **Enter** / **Space** |
| Scenes: camera, media, over-the-shoulder | **F1 / F2 / F3** |
| Full wide shot | **0** |
| Recall preset (fades) / save preset | **1–6** / **Ctrl+1–6** or right-click |
| Auto-Take (scene keys switch instantly) | **A** |
| AUTO scene switching from JW Library | **F4** |
| Compact layout (hide PREVIEW) | **F11** |
| Zoom the preview framing | mouse wheel or the slider beside PREVIEW |

During sing-along songs, tap **F2** (MEDIA) when the song starts — the app
holds it there and returns to camera automatically when the yeartext comes
back.

## Building from source

Requires the .NET 10 SDK on Windows 11, plus [Inno Setup 6](https://jrsoftware.org/isdl.php)
if you want to build the installer exe.

```powershell
dotnet build KHVideoSwitcher.sln -c Release
scripts\build-installer.ps1 -Version 1.2.3   # -> dist-installer\KHVideoSwitcher-Setup-1.2.3.exe
# or, for the plain-folder install method:
scripts\publish.ps1                          # -> dist\ (app + vcam + install.ps1)
```

`scripts\release.ps1 -Version 1.2.3` builds, runs the self-tests, tags, and
publishes a GitHub release in one step (see the script header for options).

The virtual camera component must be registered from a location readable by
Windows services — both install paths handle this (`C:\ProgramData`).
`tools\DeviceCheck` contains self-test modes used during development
(`vcamtest`, `ptztest`, `wgctest`, `detecttest`).

## Credits & license

MIT — see [LICENSE](LICENSE). The virtual camera media source is adapted from
[VCamNetSample](https://github.com/smourier/VCamNetSample) by Simon Mourier
(MIT), using his [DirectN](https://github.com/smourier/DirectN) interop
library. See [NOTICE.md](NOTICE.md).

This is an independent volunteer project, not affiliated with or endorsed by
the Watch Tower Bible and Tract Society. "JW Library" is a registered
trademark of the Watch Tower Bible and Tract Society of Pennsylvania.
