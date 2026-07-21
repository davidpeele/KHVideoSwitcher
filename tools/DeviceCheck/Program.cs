// DeviceCheck — Phase 0/1 sanity tests.
// Default mode enumerates video capture devices and display monitors.
// "capture" mode runs the app's CameraCaptureService for a few seconds,
// reports the frame rate, and saves the last frame as a PNG.

using System.Runtime.InteropServices;
using KHVideoSwitcher.Capture;
using Windows.Devices.Enumeration;
using Windows.Devices.Display;
using Windows.Media.Capture.Frames;

if (args.Length > 0 && args[0].Equals("capture", StringComparison.OrdinalIgnoreCase))
{
    await RunCaptureTestAsync(args.Length > 1 ? args[1] : "capture-test.png");
    return;
}

Console.WriteLine("=== KH Video Switcher — Device Check ===\n");

Console.WriteLine("Video capture devices (Windows.Devices.Enumeration):");
var cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
if (cameras.Count == 0)
{
    Console.WriteLine("  (none found)");
}
foreach (var cam in cameras)
{
    Console.WriteLine($"  - {cam.Name}");
    Console.WriteLine($"      id: {cam.Id}");
    Console.WriteLine($"      enabled: {cam.IsEnabled}");
}

Console.WriteLine("\nMedia frame source groups (Windows.Media.Capture.Frames):");
var groups = await MediaFrameSourceGroup.FindAllAsync();
if (groups.Count == 0)
{
    Console.WriteLine("  (none found)");
}
foreach (var group in groups)
{
    Console.WriteLine($"  - {group.DisplayName}");
    foreach (var info in group.SourceInfos)
    {
        Console.WriteLine($"      source: kind={info.SourceKind}, stream={info.MediaStreamType}");
    }
}

Console.WriteLine("\nDisplay monitors (Windows.Devices.Display):");
var monitorDevices = await DeviceInformation.FindAllAsync(DisplayMonitor.GetDeviceSelector());
if (monitorDevices.Count == 0)
{
    Console.WriteLine("  (none found)");
}
foreach (var dev in monitorDevices)
{
    var monitor = await DisplayMonitor.FromInterfaceIdAsync(dev.Id);
    var res = monitor.NativeResolutionInRawPixels;
    Console.WriteLine($"  - {(string.IsNullOrWhiteSpace(monitor.DisplayName) ? dev.Name : monitor.DisplayName)}");
    Console.WriteLine($"      native resolution: {res.Width}x{res.Height}");
    Console.WriteLine($"      connection: {monitor.ConnectionKind}");
}

Console.WriteLine("\nDevice check complete.");

static async Task RunCaptureTestAsync(string outputPath)
{
    Console.WriteLine("=== KH Video Switcher — Capture Test ===\n");

    var cameras = await CameraCaptureService.ListCamerasAsync();
    if (cameras.Count == 0)
    {
        Console.WriteLine("No cameras found.");
        return;
    }
    var camera = cameras.FirstOrDefault(c => c.Name.Contains("Logitech", StringComparison.OrdinalIgnoreCase))
                 ?? cameras[0];
    Console.WriteLine($"Using camera: {camera.Name}");

    var service = new CameraCaptureService();
    var format = await service.StartAsync(camera);
    Console.WriteLine($"Format: {format}");

    const int seconds = 5;
    Console.WriteLine($"Capturing for {seconds} seconds…");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    long frames = service.FramesReceived;
    Console.WriteLine($"Frames received: {frames} (~{frames / (double)seconds:0.#} fps)");

    // Pull the latest frame out through the same API the UI uses.
    int size = service.Width * service.Height * 4;
    var pixels = new byte[size];
    var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
    bool copied;
    try
    {
        copied = service.TryCopyLatestFrame(handle.AddrOfPinnedObject(), size);
    }
    finally
    {
        handle.Free();
    }
    await service.StopAsync();

    if (!copied)
    {
        Console.WriteLine("FAIL: no frame available to copy.");
        return;
    }

    // Sanity: make sure the image isn't all-black / all-identical.
    long sum = 0;
    for (int i = 0; i < pixels.Length; i += 997) sum += pixels[i];
    Console.WriteLine($"Pixel sample sum: {sum} ({(sum == 0 ? "SUSPICIOUS: all black" : "non-blank image")})");

    var sb = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
        System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(pixels),
        Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, service.Width, service.Height,
        Windows.Graphics.Imaging.BitmapAlphaMode.Ignore);

    string fullPath = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(fullPath));
    var file = await folder.CreateFileAsync(Path.GetFileName(fullPath),
        Windows.Storage.CreationCollisionOption.ReplaceExisting);
    using (var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
    {
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(sb);
        await encoder.FlushAsync();
    }
    Console.WriteLine($"Saved frame: {fullPath}");
    Console.WriteLine("\nCapture test complete.");
}
