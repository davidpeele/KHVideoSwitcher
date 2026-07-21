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
    await RunCaptureTestAsync(
        args.Length > 1 ? args[1] : "capture-test.png",
        args.Length > 2 ? args[2] : "Logitech");
    return;
}

if (args.Length > 0 && args[0].Equals("vcamtest", StringComparison.OrdinalIgnoreCase))
{
    await RunVCamEndToEndTestAsync(args.Length > 1 ? args[1] : "vcam-e2e.png");
    return;
}

if (args.Length > 0 && args[0].Equals("ptztest", StringComparison.OrdinalIgnoreCase))
{
    await RunPtzTestAsync(args.Length > 1 ? args[1] : ".");
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

// PTZ compositor test: captures the webcam, renders three framings
// (wide, 2x zoom left, mid-crossfade), and saves each as a PNG.
static async Task RunPtzTestAsync(string outputDir)
{
    Console.WriteLine("=== KH Video Switcher — PTZ Compositor Test ===\n");

    var cameras = await CameraCaptureService.ListCamerasAsync();
    var physical = cameras.FirstOrDefault(c => c.Name.Contains("Logitech", StringComparison.OrdinalIgnoreCase));
    if (physical is null)
    {
        Console.WriteLine("FAIL: physical camera not found.");
        return;
    }

    var service = new CameraCaptureService();
    var format = await service.StartAsync(physical);
    Console.WriteLine($"Camera: {physical.Name} {format}");
    await Task.Delay(1500);

    var frame = new byte[service.Width * service.Height * 4];
    if (!service.TryCopyLatestFrame(frame))
    {
        Console.WriteLine("FAIL: no frame.");
        await service.StopAsync();
        return;
    }

    using var compositor = new KHVideoSwitcher.Video.PtzCompositor();
    var outBytes = new byte[KHVideoSwitcher.Video.PtzCompositor.OutBytes];
    var wide = KHVideoSwitcher.Video.PtzState.FullFrame;
    var zoomLeft = new KHVideoSwitcher.Video.PtzState(0.3, 0.5, 2.0);

    async Task RenderAsync(string name, KHVideoSwitcher.Video.PtzState state, KHVideoSwitcher.Video.Transition? tr)
    {
        compositor.Compose(frame, service.Width, service.Height, state, tr);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(outBytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            compositor.CopyOutputTo(handle.AddrOfPinnedObject(), outBytes.Length);
        }
        finally
        {
            handle.Free();
        }
        var path = Path.Combine(outputDir, name);
        await SavePngAsync(outBytes, KHVideoSwitcher.Video.PtzCompositor.OutWidth,
            KHVideoSwitcher.Video.PtzCompositor.OutHeight, path);
        Console.WriteLine($"Saved {Path.GetFullPath(path)}");
    }

    await RenderAsync("ptz-wide.png", wide, null);
    await RenderAsync("ptz-zoom-left.png", zoomLeft, null);

    // A transition caught mid-fade: 60 ms duration sampled ~30 ms in (~50%).
    var tr = new KHVideoSwitcher.Video.Transition(wide, zoomLeft, 60);
    await Task.Delay(30);
    await RenderAsync("ptz-midfade.png", wide, tr);

    await service.StopAsync();
    Console.WriteLine("\nPTZ test complete.");
}

// End-to-end virtual camera test:
//   webcam -> CameraCaptureService -> SharedFrameChannel -> KH Video Switcher vcam
//   -> MediaCapture consumer (same path Zoom uses) -> PNG on disk.
static async Task RunVCamEndToEndTestAsync(string outputPath)
{
    Console.WriteLine("=== KH Video Switcher — Virtual Camera End-to-End Test ===\n");

    var cameras = await CameraCaptureService.ListCamerasAsync();
    var physical = cameras.FirstOrDefault(c => c.Name.Contains("Logitech", StringComparison.OrdinalIgnoreCase));
    if (physical is null)
    {
        Console.WriteLine("FAIL: physical camera (Logitech) not found.");
        return;
    }

    Console.WriteLine($"1. Starting physical camera: {physical.Name}");
    var service = new CameraCaptureService();
    var format = await service.StartAsync(physical);
    Console.WriteLine($"   {format}");

    Console.WriteLine("2. Starting virtual camera…");
    using var vcam = new KHVideoSwitcher.VCam.VirtualCameraController();
    vcam.Start();
    Console.WriteLine("   Virtual camera started.");

    Console.WriteLine("3. Publishing frames to shared memory…");
    using var channel = new KHVideoSwitcher.VCam.SharedFrameChannel();
    long published = 0;
    service.FrameArrived += () =>
    {
        if (channel.TryOpen())
        {
            int w = service.Width, h = service.Height;
            if (channel.WriteFrame(w, h, w * 4, dest => service.TryCopyLatestFrame(dest, w * h * 4)))
                Interlocked.Increment(ref published);
        }
    };
    await Task.Delay(2000);
    Console.WriteLine($"   Published {Interlocked.Read(ref published)} frames so far. " +
        $"(channel open: {channel.IsOpen}, openErr: {channel.LastOpenError}, createErr: {channel.LastCreateError})");

    Console.WriteLine("4. Opening the virtual camera as a consumer (like Zoom would)…");
    var refreshed = await CameraCaptureService.ListCamerasAsync();
    var virtualCam = refreshed.FirstOrDefault(c => c.Name.Contains("KH Video Switcher", StringComparison.OrdinalIgnoreCase));
    if (virtualCam is null)
    {
        Console.WriteLine($"FAIL: virtual camera not enumerated. Available: {string.Join(", ", refreshed.Select(c => c.Name))}");
        await service.StopAsync();
        return;
    }
    Console.WriteLine($"   Found: {virtualCam.Name}");

    var consumer = new CameraCaptureService();
    var vFormat = await consumer.StartAsync(virtualCam);
    Console.WriteLine($"   Consumer format: {vFormat}");

    // Inspect every consumed frame for dark/blank content (the "glitch" symptom).
    long inspected = 0, darkFrames = 0;
    var inspectBuffer = new byte[consumer.Width * consumer.Height * 4];
    consumer.FrameArrived += () =>
    {
        if (!consumer.TryCopyLatestFrame(inspectBuffer))
            return;
        long sum = 0;
        for (var i = 0; i < inspectBuffer.Length; i += 4001)
            sum += inspectBuffer[i];
        var avg = sum / (inspectBuffer.Length / 4001.0);
        Interlocked.Increment(ref inspected);
        if (avg < 8) // near-black
            Interlocked.Increment(ref darkFrames);
    };

    for (var i = 0; i < 10; i++)
    {
        await Task.Delay(1000);
        Console.WriteLine($"   t+{i + 1}s  published: {Interlocked.Read(ref published)}  " +
            $"open: {channel.IsOpen}  openErr: {channel.LastOpenError}  createErr: {channel.LastCreateError}");
    }
    long consumed = consumer.FramesReceived;
    Console.WriteLine($"   Frames from virtual camera: {consumed} (~{consumed / 10.0:0.#} fps); published: {Interlocked.Read(ref published)}");
    Console.WriteLine($"   Inspected: {Interlocked.Read(ref inspected)}  dark/blank: {Interlocked.Read(ref darkFrames)}" +
        (Interlocked.Read(ref darkFrames) == 0 ? "  — no glitch frames detected" : "  — GLITCH FRAMES PRESENT"));

    int size = consumer.Width * consumer.Height * 4;
    var pixels = new byte[size];
    var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
    bool copied;
    try
    {
        copied = consumer.TryCopyLatestFrame(handle.AddrOfPinnedObject(), size);
    }
    finally
    {
        handle.Free();
    }

    if (copied)
    {
        await SavePngAsync(pixels, consumer.Width, consumer.Height, outputPath);
        Console.WriteLine($"5. Saved virtual camera frame: {Path.GetFullPath(outputPath)}");
    }
    else
    {
        Console.WriteLine("FAIL: could not copy a frame from the virtual camera.");
    }

    await consumer.StopAsync();
    await service.StopAsync();
    vcam.Stop();
    Console.WriteLine("\nEnd-to-end test complete.");
}

static async Task SavePngAsync(byte[] bgraPixels, int width, int height, string outputPath)
{
    var sb = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
        System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bgraPixels),
        Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, width, height,
        Windows.Graphics.Imaging.BitmapAlphaMode.Ignore);

    string fullPath = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(fullPath));
    var file = await folder.CreateFileAsync(Path.GetFileName(fullPath),
        Windows.Storage.CreationCollisionOption.ReplaceExisting);
    using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
    var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
        Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
    encoder.SetSoftwareBitmap(sb);
    await encoder.FlushAsync();
}

static async Task RunCaptureTestAsync(string outputPath, string nameFilter)
{
    Console.WriteLine("=== KH Video Switcher — Capture Test ===\n");

    var cameras = await CameraCaptureService.ListCamerasAsync();
    if (cameras.Count == 0)
    {
        Console.WriteLine("No cameras found.");
        return;
    }
    var camera = cameras.FirstOrDefault(c => c.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
    if (camera is null)
    {
        Console.WriteLine($"FAIL: no camera matching '{nameFilter}'. Available: {string.Join(", ", cameras.Select(c => c.Name))}");
        return;
    }
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
