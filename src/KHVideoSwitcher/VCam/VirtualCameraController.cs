using DirectN;

namespace KHVideoSwitcher.VCam;

/// <summary>
/// Owns the lifetime of the "KH Video Switcher" Windows virtual camera.
/// The camera exists (and is visible to Zoom etc.) while Start has been
/// called and this process is alive; Session lifetime means Windows cleans
/// it up automatically if the app dies.
/// </summary>
public sealed class VirtualCameraController : IDisposable
{
    private IMFVirtualCamera? _camera;
    private bool _mfStarted;

    public bool IsRunning => _camera is not null;

    public void Start()
    {
        if (_camera is not null)
            return;

        if (!_mfStarted)
        {
            MFFunctions.MFStartup();
            _mfStarted = true;
        }

        Functions.MFCreateVirtualCamera(
            __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0001.MFVirtualCameraType_SoftwareCameraSource,
            __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0002.MFVirtualCameraLifetime_Session,
            __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_CurrentUser,
            Shared.CameraFriendlyName,
            "{" + Shared.CLSID_KHVCam + "}",
            null,
            0,
            out var camera).ThrowOnError();

        var hr = camera.Start(null);
        if (hr.IsError)
        {
            camera.Remove();
            hr.ThrowOnError();
        }
        _camera = camera;
    }

    public void Stop()
    {
        var camera = _camera;
        _camera = null;
        if (camera is not null)
        {
            camera.Remove();
        }
    }

    public void Dispose() => Stop();
}
