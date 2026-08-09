# Security

## Reporting a vulnerability

Please open a [security advisory](https://github.com/davidpeele/KHVideoSwitcher/security/advisories/new)
(preferred, private) or a regular issue for non-sensitive reports.

## What this software does that's security-relevant

KH Video Switcher installs a **machine-wide COM component** (the virtual camera
media source) and therefore requires administrator rights to install. Windows
loads that component into other processes — the Frame Server service and any app
that opens the camera, such as Zoom or a browser — so its integrity matters.

### Trust boundaries

**Shared memory frame channel.** The app publishes composited frames to the
media source through a named section, `Global\KHVideoSwitcher.VCam.Frames.v2`.
It must be in the `Global\` namespace because the Frame Server runs in session 0
while the app runs in the user's session.

- Write access is granted to **Authenticated Users**; **Everyone** and
  **ALL APPLICATION PACKAGES** get read-only. This denies write access to
  sandboxed AppContainer processes (browser content processes), and the default
  mandatory integrity policy denies it to low-integrity processes.
- Write access cannot be narrowed further: only a process holding
  `SeCreateGlobalPrivilege` can create a `Global\` section, so the service
  creates it and the user-mode app must still be able to write to it.
  Same-user processes can therefore write to the channel — on Windows, code
  running as the same user at the same integrity level is not a security
  boundary in any case.
- Because of that, **the channel header is treated as untrusted input**. Every
  geometry field is validated against `MaxWidth`/`MaxHeight` in 64-bit
  arithmetic before any pointer math, and every copy is given the true remaining
  destination capacity. Readers map the view read-only and use plain aligned
  loads. `DeviceCheck channelfuzz` exercises this with hostile headers,
  including the integer-overflow combinations that defeat naive 32-bit checks.

**Virtual camera install directory.** `%ProgramData%\KHVideoSwitcher` holds the
registered COM server. Both installers seize ownership (Administrators) and
reset its ACL — SYSTEM/Administrators full control, Users and app packages
read+execute — *before* copying files in, so a standard user cannot pre-create
the folder and later replace the DLL.

**Runtime prerequisite download.** If the .NET 10 Desktop Runtime is missing,
the installer downloads it from `https://aka.ms/dotnet/...` over HTTPS, checks
curl's exit code, verifies the file carries a valid Authenticode signature whose
subject is `O=Microsoft Corporation`, and only then executes it, checking its
exit code too. A download that fails any of those checks is deleted, not run.

### Known limitations

- **Binaries are not Authenticode-signed.** A code-signing certificate is a
  recurring paid expense that this volunteer project doesn't carry, so Windows
  SmartScreen warns on first run. `SHA256SUMS.txt` is published with each
  release so downloads can be verified. This is the main open item; anyone able
  to donate a certificate is welcome to get in touch.
- Frames in the shared channel are readable by any local process. Any local
  process can also see your camera through the normal Windows camera APIs, so
  this does not widen the exposure meaningfully, but it is worth knowing.
- Releases are built and uploaded from a maintainer workstation rather than a
  hardened CI pipeline.

## No network activity

The application itself makes no network connections — it captures locally,
composites locally, and publishes to a local virtual camera. The only network
access anywhere in the project is the optional .NET runtime download during
installation, described above.
