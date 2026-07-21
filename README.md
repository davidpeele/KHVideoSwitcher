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

1. Download the latest `KHVideoSwitcher-vX.Y.Z.zip` from
   [Releases](../../releases) and extract it anywhere.
2. Right-click `install.ps1` → **Run with PowerShell**, and accept the
   administrator prompt. (If script execution is blocked on your machine, open
   a PowerShell window in the folder and run
   `powershell -ExecutionPolicy Bypass -File .\install.ps1`.)
3. The installer puts the app in Program Files, registers the virtual camera,
   creates Start Menu and Desktop shortcuts, and installs the .NET runtime if
   needed.

To uninstall: run `install.ps1 -Uninstall` (or keep the extracted folder and
run it from there later).

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

Requires the .NET 10 SDK on Windows 11.

```powershell
dotnet build KHVideoSwitcher.sln -c Release
scripts\publish.ps1          # builds the dist\ package
dist\install.ps1             # installs it (elevated)
```

The virtual camera component must be registered from a location readable by
Windows services — the install script handles this (`C:\ProgramData`).
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
