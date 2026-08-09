# Third-party code

## VCamNetSample (MIT License)

The virtual camera media source in `src/KHVideoSwitcher.VCam` is derived from
**VCamNetSample** by Simon Mourier — https://github.com/smourier/VCamNetSample
(MIT license). The Media Foundation media source/stream/activator plumbing and
the GPU/CPU frame pipeline come from that project; the shared-memory frame
channel and switcher integration are original to KH Video Switcher.

It depends on the **DirectNCore** NuGet package (MIT) by the same author for
DirectX / Media Foundation interop.

## Barlow & Barlow Condensed fonts (SIL Open Font License 1.1)

The UI in `src/KHVideoSwitcher/Fonts` bundles three static weights of the
**Barlow** typeface family by Jeremy Tribby — https://github.com/jpt/barlow —
sourced from Google Fonts (https://github.com/google/fonts). Licensed under
the SIL Open Font License; see `src/KHVideoSwitcher/Fonts/OFL.txt`.
