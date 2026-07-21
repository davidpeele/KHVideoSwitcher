# KH Video Switcher — Project Plan

A purpose-built Windows 11 application for Kingdom Hall meeting streaming that replaces the
current OBS setup with three core capabilities:

1. **Virtual camera output** — appears as a webcam in Zoom or any other streaming app.
2. **Virtual PTZ** — digital pan/tilt/zoom on a fixed wide-shot webcam, with presets,
   a preview-before-take workflow, and smooth crossfade transitions.
3. **JW Library awareness** — detects whether JW Library is showing nothing, a still
   image, or a playing video on the Kingdom Hall displays, and can automatically switch
   to a matching scene or PTZ preset.

---

## 1. How it works (concept)

```
┌─────────────┐     ┌──────────────────────────────────────────┐
│ 4K Webcam    │────▶│                                          │
│ (wide shot)  │     │   COMPOSITOR (GPU)                       │     ┌──────────────┐
└─────────────┘     │   • PTZ crop/scale of webcam             │────▶│ Virtual      │
┌─────────────┐     │   • Scenes: full cam / over-shoulder /   │     │ Camera       │──▶ Zoom, etc.
│ Secondary    │────▶│     fullscreen media                     │     └──────────────┘
│ display      │     │   • Crossfade transitions               │
│ (JW Library  │     └──────────────────────────────────────────┘
│  media)      │                      ▲
└─────────────┘     ┌──────────────────────────────────────────┐
                    │ JW LIBRARY DETECTOR                       │
                    │ analyzes the captured display:            │
                    │ none / still image / video → auto-switch  │
                    └──────────────────────────────────────────┘
```

The app has a **Preview / Program** workflow like a broadcast switcher:

- **Program** pane: what viewers currently see (what's going out the virtual camera).
- **Preview** pane: where you line up the next shot — drag a crop box on the wide shot
  to frame the speaker or a demonstration, or select a scene.
- **Take / Fade** button: switches Preview to Program with a quick crossfade.
  Viewers never see you hunting for the shot.
- **Preset buttons** (e.g., "Speaker", "Reader", "Demo Left", "Demo Right", "Wide"):
  one click fades directly to a saved PTZ framing.

### Scenes (initial set)

| Scene | Contents |
|---|---|
| **Camera** | The PTZ view of the webcam (current preset/framing) |
| **Fullscreen Media** | The captured JW Library display, full frame |
| **Over-the-Shoulder** | Media large + camera inset, speaker offset to one side |
| **Standby / Yeartext** | Optional static card or the stock display |

### Auto-switching rules (configurable, with manual override)

| JW Library state | Default action |
|---|---|
| Video playing | Fade to **Fullscreen Media** |
| Still image shown | Fade to **Over-the-Shoulder** |
| No media (stock/yeartext) | Fade to **Camera** (last PTZ preset) |

A manual override toggle ("Auto" on/off) and a short hold-off delay (so a 1-second
flicker doesn't cause scene thrashing) are essential.

---

## 2. Technology choices

| Concern | Choice | Why |
|---|---|---|
| Language / runtime | **C# / .NET 8** | Best balance of Windows API access and development speed |
| UI framework | **WPF** | Mature, great for custom video/preview controls; runs everywhere Win11 does |
| Webcam capture | **Media Foundation** (Source Reader) | Native, low-latency, supports 4K cameras, exposes resolution/FPS control |
| Media capture | **Windows.Graphics.Capture** | The modern, efficient API (same one OBS uses on Win11); captures JW Library's media **window** directly as a GPU texture, with whole-monitor capture as a fallback |
| Compositing / PTZ / fades | **Direct3D 11** | All cropping, scaling, picture-in-picture, and crossfades happen on the GPU — low CPU, smooth 30/60 fps |
| Virtual camera | **Windows 11 Media Foundation Virtual Camera API** (`MFCreateVirtualCamera`) | Official Win11 mechanism — no third-party drivers; shows up in Zoom/Teams/browsers as a real camera |
| JW Library detection | **Frame analysis** of the captured display (see §4) | JW Library has no public API; analyzing what's actually on the media display is robust and version-independent |
| Settings/presets | JSON file in `%APPDATA%\KHVideoSwitcher` | Simple, portable, easy to back up |

**Virtual camera note:** the Win11 virtual camera API requires a small COM component
registered once (one admin prompt at install). If we hit a wall there, fallback options
are the open-source `softcam` DirectShow filter or (worst case) continuing to route
through OBS's virtual camera while our app does everything else. We'll prove this out
early (Phase 2) precisely because it's the biggest technical risk.

---

## 3. Virtual PTZ design

- **Source:** a 4K (3840×2160) webcam wide shot; output at 1080p. That gives 2× zoom
  with zero quality loss, and usable zoom to ~3× before softness shows. (A 1080p camera
  works but zoom quality will be limited — a 4K camera is the main hardware recommendation.)
- **Model:** PTZ = a crop rectangle over the source frame, locked to 16:9.
  - Pan/tilt = move the rectangle. Zoom = shrink it.
  - The GPU scales the crop to the 1080p output every frame.
- **Presets:** named crop rectangles, saved to disk, arranged as a button row with
  keyboard shortcuts (1–9). Editable by framing in Preview and clicking "Save preset".
- **Transitions:** on Take, render both old and new framing and crossfade over a
  configurable duration (default ~300 ms). Later option: a smooth *motion* transition
  (animated pan/zoom between framings) for a real-PTZ feel — the crossfade ships first.
- **Preview interaction:** drag to pan, scroll/pinch or handles to zoom, on a paused-live
  preview pane showing the full wide shot with the crop box overlaid.

---

## 4. JW Library detection design

JW Library (a Microsoft Store app) has no public API, so we detect state from what is
actually displayed — the same capture we already use for the Fullscreen Media scene.

**Capture strategy:** we target JW Library's media *window* directly (Windows.Graphics.
Capture supports window capture, and the media output is its own window even when
fullscreen on the second display — the fullscreen output *is* a window sized to cover
the monitor). This isolates us from stray cursors/notifications on that monitor and
survives display renumbering. Whole-monitor capture remains as a fallback if the
window can't be located.

**Important constraint:** JW Library's *windowed* media mode removes the media from the
presentation displays entirely — in-person attendees would see nothing. So the app is
designed around JW Library staying in its normal fullscreen-to-second-display mode at
all times; both capture methods work with that mode. JW Library keeps driving the Hall
displays directly — we only *read* its output, so an app hiccup can never blank the
in-room screens.

Classification runs a few times per second on a downscaled copy:

1. **Does JW Library's media window exist at all?**
   Window presence/absence is a free signal that a media session is active.
2. **Motion check:** compare consecutive downscaled frames.
   - Significant pixel change over ~0.5 s → **video playing**.
   - Static content → still image *or* the stock/yeartext screen.
3. **Stock-screen check:** on setup, the user clicks "Capture stock screen" while the
   yeartext/background is showing; we store a fingerprint. A static frame matching the
   fingerprint → **no media**; otherwise → **still image**.

State changes are debounced (~1–2 s) before triggering an auto-switch, and every
auto-switch respects the manual override toggle. This approach keeps working across
JW Library updates because it never depends on JW Library internals.

---

## 5. Build phases

Each phase ends with something you can actually run and test at the Hall.

### Phase 0 — Environment setup
- Install .NET 8 SDK + Visual Studio 2022 (or Build Tools), create the solution, init git.
- Verify the webcam and displays enumerate correctly with a tiny test program.

### Phase 1 — Camera capture + preview window
- Enumerate cameras, pick one, capture at its best resolution/FPS.
- Show live video in the app window via D3D11.
- **Done when:** the app shows your webcam live with low latency.

### Phase 2 — Virtual camera output *(the big risk — do it early)*
- Implement the Win11 virtual camera and pump the (uncropped) webcam frames to it.
- **Done when:** Zoom lists "KH Video Switcher" as a camera and shows the webcam feed.

### Phase 3 — Virtual PTZ, presets, preview/program, crossfade
- Crop-rectangle PTZ on the GPU; Preview/Program panes; Take/Fade button.
- Preset save/recall with keyboard shortcuts; crossfade transition.
- **Done when:** you can frame a shot in Preview and fade to it without viewers seeing the move.

### Phase 4 — Media capture + scenes
- Capture JW Library's media window with Windows.Graphics.Capture (monitor capture as fallback).
- Build the Fullscreen Media and Over-the-Shoulder scenes (inset position/size configurable).
- Scene buttons participate in the same Preview/Take workflow.
- **Done when:** you can manually reproduce everything the current OBS setup does.
  **This is the milestone where the app can replace OBS for a real meeting.**

### Phase 5 — JW Library auto-detection
- Motion analysis + stock-screen fingerprint + window detection (§4).
- Rules mapping state → scene/preset; Auto on/off toggle; debounce; status indicator
  showing what the detector currently thinks ("Video playing", "Still image", "No media").
- **Done when:** playing a video in JW Library switches the stream to Fullscreen Media
  by itself, and back when it ends.

### Phase 6 — Polish & reliability
- Settings persistence (camera choice, display choice, presets, scene layouts, rules).
- Recovery: camera unplugged/replugged, display config changes, JW Library restarted.
- Global hotkeys, a compact "meeting mode" UI, logging for troubleshooting.
- Simple installer (registers the virtual camera component, creates shortcuts).

### Later ideas (not in scope yet)
- Animated pan/zoom transitions (simulated real PTZ move).
- Multiple cameras with cuts between them.
- NDI output alongside the virtual camera.
- **Media router mode:** JW Library outputs to a window and *this app* projects it onto
  the Hall displays (would let us add fades/branding on the in-room screens). Deliberately
  not in v1: it would make the app a single point of failure for the Hall displays, and
  it adds a frame or two of video delay relative to JW Library's audio.
- Audio level meter / reminder (the virtual camera carries video only — Zoom takes the
  microphone separately, same as with OBS today).

---

## 6. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Win11 virtual camera API friction | Tackled first (Phase 2); fallbacks: `softcam` driver, or OBS vcam as a stopgap |
| Detection misfires (e.g., mostly-still video) | Debounce, tunable thresholds, always-available manual override |
| GPU/CPU load on the Hall computer | GPU-only pipeline, 1080p30 default output, downscaled analysis frames |
| 1080p camera limits zoom quality | Works, but recommend a 4K webcam (e.g., Logitech Brio–class) for real PTZ range |
| Project lives in OneDrive | Fine for docs, but build outputs churn OneDrive sync — we'll .gitignore `bin`/`obj`, and can relocate the repo later if sync causes trouble |

---

## 7. Open questions (answer whenever — defaults are sensible)

1. **Which webcam** will be used (model/resolution)? Determines realistic zoom range.
2. **Output format:** 1080p30 is the default — is that what you stream at today?
3. **Display layout at the Hall:** how many monitors does the JW Library PC drive, and
   which one shows media? (The app will let you pick, but it helps to know.)
4. **Same PC or second PC?** Plan assumes everything (JW Library + this app + Zoom)
   runs on one machine, like your current OBS setup.
