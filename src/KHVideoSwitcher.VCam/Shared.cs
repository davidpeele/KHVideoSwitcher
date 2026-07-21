namespace KHVideoSwitcher.VCam
{
    public static class Shared
    {
        // CLSID of the KH Video Switcher virtual camera media source.
        public const string CLSID_KHVCam = "8ae54092-501b-4c01-afe0-b55cef94eb2d";

        public const string CameraFriendlyName = "KH Video Switcher";

        // Virtual camera output format. The switcher app publishes frames at
        // exactly this size; the media source scales if they ever differ.
        public const int Width = 1920;
        public const int Height = 1080;
        public const int FrameRate = 30;
    }
}
