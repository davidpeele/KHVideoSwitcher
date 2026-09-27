<p align="center">
  <img src="assets/icon-256.png" alt="KH Video Switcher icon" width="128" height="128">
</p>

<h1 align="center">KH Video Switcher</h1>

<p align="center">
  <a href="../../releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/davidpeele/KHVideoSwitcher"></a>
  <img alt="Windows 11" src="https://img.shields.io/badge/Windows-11-0078D4">
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/github/license/davidpeele/KHVideoSwitcher"></a>
</p>

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
  (OTS: camera behind, media boxed top-right). In Settings you can size the
  box, add a thin border in any color, and optionally shift the camera left so
  the box doesn't cover the speaker — with a choice of background color for
  the space that reveals.
- **JW Library aware** — captures the media window/display and detects whether
  a video is playing, a still image is up, or the yeartext screen is showing.
  With AUTO on, it switches scenes for you: video → MEDIA, still → OTS,
  nothing → CAM. The operator can always override; sing-along lyric videos are
  taken manually with one tap of MEDIA (F2) and held automatically after that.
  An **Always Full Screen First** setting sends stills to full-screen MEDIA
  too, for halls that don't want the smaller over-the-shoulder box.
- **Audio meter** — a color-coded level meter under PROGRAM shows the sound
  booth's line-in at a glance (with a dB scale and per-channel readouts), so
  you can confirm levels without OBS open. It's a visual reference only — Zoom
  still takes its audio directly. Input device and gain calibration are in
  Settings.
- **Built-in Help** — a Help button opens a plain-English user guide in its
  own window, with jump-to-section navigation, that stays open alongside the
  app while you run a meeting.
- **Keeps itself up to date** — checks for a new release once a day and shows
  an **UPDATE** tag when one is available (or use **Check for Updates** in
  Settings). Nothing downloads until you click it, and the installer is
  verified against the release's published checksum before it runs.
- **Report Bug** — gathers the app version, device names, and a recent
  activity log (with your Windows username stripped out) into a report you can
  read and edit, then copy or open as a prefilled GitHub issue. Nothing is
  ever sent automatically.

## Requirements

- Windows 11 (the virtual camera uses a Windows 11 API)
- A webcam (1080p works well; 4K gives more zoom headroom)
- .NET 10 Desktop Runtime (the installer fetches it automatically if missing)

## Installing

1. Download the latest `KHVideoSwitcher-Setup-X.Y.Z.exe` from
   [Releases](../../releases).
2. Double-click it and choose an install mode on the first page:
   - **Install for all users (recommended)** — click **Yes** on the one
     administrator prompt. Installs the app *and* the virtual camera.
   - **Install for me only** — no administrator rights needed. See below.
3. Click through the wizard. Start Menu and Desktop shortcuts are created, the
   .NET 10 Desktop Runtime is installed if missing, and (in all-users mode) the
   virtual camera is registered.

### Which mode do I want?

**Install for all users** is what you want on the Kingdom Hall computer. It
needs an administrator once, and because the virtual camera registers
machine-wide, *every* Windows account on that PC can then run the switcher —
including standard, non-admin accounts, each with their own presets. Running the
app day to day never requires administrator rights; only this install does.

**Install for me only** needs no administrator at all and puts the app in your
own user folder. Use it when:

- an administrator already installed the virtual camera on this computer — then
  everything works normally, including output to Zoom; or
- you want to learn the app, rehearse framing, or set up presets on a machine
  where you can't elevate. Camera, pan/zoom, presets, scenes, media capture and
  automatic switching all work — you just can't send video to Zoom from that
  machine, because the virtual camera must be registered for the whole computer.

The installer tells you which of those two situations you're in when it
finishes.

**Switching from "just for me" to "all users" later** (i.e. adding the virtual
camera): uninstall the per-user copy first — Settings → Apps → *KH Video
Switcher (Current user)* — then run the installer again and choose **Install
for all users**. Removing it first avoids ending up with two copies installed
side by side. Your presets and settings live in `%APPDATA%` and are untouched
by either install or uninstall.

> **Expect Windows to complain, and here's the honest reason.** These downloads
> are not Authenticode-signed — a code-signing certificate is a recurring paid
> expense this volunteer project doesn't carry — so an unsigned installer with
> no download history can trip two different Windows protections:
>
> - **SmartScreen**: *"Windows protected your PC"* → click **More info →
>   Run anyway**.
> - **Defender may occasionally report a threat** (typically
>   `Trojan:Win32/Wacatac.*!ml`). The `!ml` suffix means a machine-learning
>   guess rather than a known-malware signature, and unsigned installers are a
>   well-known source of these false alarms. If it happens, please
>   [open an issue](../../issues) so the build can be submitted to Microsoft
>   for correction, and use an unaffected release in the meantime.
>
> **Always verify what you downloaded** against `SHA256SUMS.txt` on the release
> page — that check is meaningful whether or not Windows complains:
>
> ```powershell
> Get-FileHash .\KHVideoSwitcher-Setup-X.Y.Z.exe -Algorithm SHA256
> ```
>
> The full source is in this repository and the release scripts build the
> published artifacts from it, so anyone who prefers can build their own copy
> instead (see *Building from source*).

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

`install.ps1 -PerUser` is the equivalent of the installer's "just for me" mode:
it installs the app to `%LOCALAPPDATA%\Programs` with no elevation and leaves
the machine-wide virtual camera untouched (`-Uninstall -PerUser` to remove it).

Note that if PowerShell's execution policy blocks the script, running it with
`-ExecutionPolicy Bypass` removes a safety check while executing code as
administrator. Only do that with a copy whose SHA-256 you have verified against
the release page.

</details>

## First-time setup (once per machine)

1. Launch **KH Video Switcher**, pick your camera, click **Capture Camera**.
2. Pick the JW Library media window (or the monitor that shows media) from
   the second dropdown, click **Capture Screen**. Both buttons turn blue while
   capturing.
3. With JW Library showing its normal yeartext/no-media screen, click the
   **STOCK · NOT SET** tag under the toolbar — this teaches the app what
   "nothing is playing" looks like. (The tag is dashed whenever this step is
   needed.)
4. Frame your shots in PREVIEW and save presets: line up a shot, right-click a
   preset button (or Ctrl+1…6). Presets store the scene too — e.g. an
   over-the-shoulder with the speaker offset left.
5. Click **Virtual Camera** to turn it on, then select
   **"KH Video Switcher"** as the camera in Zoom.
6. Optional: open **Settings** to pick the audio input for the level meter.
7. Optional: press **F4** to enable AUTO scene switching.

Everything (devices, presets, stock screen, settings) is remembered, so
meeting-day startup is: open app → Capture Camera → Capture Screen → Virtual
Camera on.

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
| Open the in-app user guide | **Help** button (toolbar) |

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

`scripts\release.ps1 -Version 1.2.3` runs a local build and the self-tests,
then tags and pushes; GitHub Actions (`.github/workflows/release.yml`) builds
the published installer, zip, and `SHA256SUMS.txt` from that tag. Per-version
notes live in [`release-notes/`](release-notes).

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
