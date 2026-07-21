# Third-party code

## VCamNetSample (MIT License)

The virtual camera media source in `src/KHVideoSwitcher.VCam` is derived from
**VCamNetSample** by Simon Mourier — https://github.com/smourier/VCamNetSample
(MIT license). The Media Foundation media source/stream/activator plumbing and
the GPU/CPU frame pipeline come from that project; the shared-memory frame
channel and switcher integration are original to KH Video Switcher.

It depends on the **DirectNCore** NuGet package (MIT) by the same author for
DirectX / Media Foundation interop.
