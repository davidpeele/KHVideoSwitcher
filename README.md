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
> Get-FileHash .\KHVideoSwitcher-Setup-1.3.0.exe -Algorithm SHA256
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
