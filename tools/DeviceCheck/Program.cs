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

if (args.Length > 0 && args[0].Equals("detecttest", StringComparison.OrdinalIgnoreCase))
{
    await RunDetectTestAsync();
    return;
}

if (args.Length > 0 && args[0].Equals("wgctest", StringComparison.OrdinalIgnoreCase))
{
    await RunWgcTestAsync(args.Length > 1 ? args[1] : "Notepad", args.Length > 2 ? args[2] : "wgc-test.png");
    return;
}

// Same as wgctest but Start() runs on an STA thread (like the WPF UI thread)
// while frames are pumped from an MTA thread (like the camera callback thread).
if (args.Length > 0 && args[0].Equals("wgcsta", StringComparison.OrdinalIgnoreCase))
{
    var filter = args.Length > 1 ? args[1] : "Notepad";
    var output = args.Length > 2 ? args[2] : "wgc-sta.png";

    var targets = KHVideoSwitcher.Capture.DisplayCaptureService.ListTargets();
    var target = targets.FirstOrDefault(t => t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
    if (target is null)
    {
        Console.WriteLine($"FAIL: no target matching '{filter}'.");
        return;
    }
    Console.WriteLine($"Capturing (STA start): {target}");

    using var svc = new KHVideoSwitcher.Capture.DisplayCaptureService();
    Exception? startError = null;
    var started = new ManualResetEventSlim();
    var staThread = new Thread(() =>
    {
        try
        {
            svc.Start(target);
        }
        catch (Exception ex)
        {
            startError = ex;
        }
        started.Set();
        Thread.Sleep(5000); // keep the STA thread alive like a UI thread
    });
    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    started.Wait();
    if (startError is not null)
    {
        Console.WriteLine($"START FAILED: {startError}");
        return;
    }

    byte[] staFrame = [];
    int sw = 0, sh = 0;
    for (var i = 0; i < 90; i++)
    {
        await Task.Delay(33).ConfigureAwait(false); // stay off the STA thread
        svc.PumpFrames();
        svc.TryCopyLatestFrame(ref staFrame, out sw, out sh);
    }
    Console.WriteLine($"Result: {sw}x{sh}, age {svc.LastFrameAgeMs} ms, error: {svc.LastError ?? "none"}");
    if (sw > 0)
    {
        await SavePngAsync(staFrame, sw, sh, output);
        Console.WriteLine($"Saved {Path.GetFullPath(output)}");
    }
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

// Media state detector test: drives synthetic frames through the detector and
// checks the classified states (video / still / no-media) with real timing.
static async Task RunDetectTestAsync()
{
    Console.WriteLine("=== KH Video Switcher — Media State Detector Test ===\n");

    const int w = 320, h = 180;
    var frame = new byte[w * h * 4];
    var detector = new KHVideoSwitcher.Video.MediaStateDetector();
    var rng = new Random(42);
    var failures = 0;

    void FillNoise()
    {
        rng.NextBytes(frame);
    }
    // Calm video scene: only a small region (like a speaker's face) changes.
    void MutateSmallRegion()
    {
        for (var y = 60; y < 100; y++)
        {
            for (var x = 100; x < 140; x++)
            {
                int i = (y * w + x) * 4;
                frame[i] = (byte)rng.Next(256);
                frame[i + 1] = (byte)rng.Next(256);
                frame[i + 2] = (byte)rng.Next(256);
            }
        }
    }
    void FillSolid(byte b, byte g, byte r)
    {
        for (var i = 0; i < frame.Length; i += 4)
        {
            frame[i] = b;
            frame[i + 1] = g;
            frame[i + 2] = r;
            frame[i + 3] = 255;
        }
    }

    async Task<KHVideoSwitcher.Video.MediaState> RunPhaseAsync(string name, int ms, Action? mutatePerTick)
    {
        var end = Environment.TickCount64 + ms;
        KHVideoSwitcher.Video.MediaState state = default;
        while (Environment.TickCount64 < end)
        {
            mutatePerTick?.Invoke();
            state = detector.Analyze(frame, w, h);
            await Task.Delay(33);
        }
        Console.WriteLine($"{name}: state={state} (moved cells {detector.LastMotion:0})");
        return state;
    }

    void Check(string what, KHVideoSwitcher.Video.MediaState actual, KHVideoSwitcher.Video.MediaState expected)
    {
        if (actual != expected)
        {
            Console.WriteLine($"  FAIL: {what}: expected {expected}, got {actual}");
            failures++;
        }
    }

    // 1. CALM video (only a small region moves, like a talking head) => Video.
    FillSolid(20, 40, 60);
    var s = await RunPhaseAsync("1. calm video (small region) 2.5s", 2500, MutateSmallRegion);
    Check("calm video detection", s, KHVideoSwitcher.Video.MediaState.Video);

    // 2. Static mid-video (scripture card), no stock set => stays LATCHED.
    FillSolid(30, 60, 90);
    s = await RunPhaseAsync("2. static 4s during video (latch)", 4000, null);
    Check("video latch through still segment", s, KHVideoSwitcher.Video.MediaState.Video);

    // 2b. Without a stock fingerprint, a very long stillness eventually exits.
    s = await RunPhaseAsync("2b. static 5 more sec (no-stock fallback)", 5000, null);
    Check("no-stock long-still fallback", s, KHVideoSwitcher.Video.MediaState.Still);

    // 3. Fingerprint this static frame as stock => NoMedia.
    if (!detector.TryCaptureStock(out _))
    {
        Console.WriteLine("  FAIL: could not capture stock fingerprint");
        failures++;
    }
    s = await RunPhaseAsync("3. same static, fingerprinted, 2s", 2000, null);
    Check("no-media detection", s, KHVideoSwitcher.Video.MediaState.NoMedia);

    // 4. Full-motion video => Video quickly.
    s = await RunPhaseAsync("4. busy video 1.5s", 1500, FillNoise);
    Check("video re-detection", s, KHVideoSwitcher.Video.MediaState.Video);

    // 5. With stock set, a static NON-stock frame keeps the latch…
    FillSolid(200, 180, 120);
    s = await RunPhaseAsync("5. static non-stock 8s (latched)", 8000, null);
    Check("video latch holds with stock set", s, KHVideoSwitcher.Video.MediaState.Video);

    // 6. …until the stock screen appears => NoMedia.
    FillSolid(30, 60, 90);
    s = await RunPhaseAsync("6. stock screen 4s", 4000, null);
    Check("stock screen releases the latch", s, KHVideoSwitcher.Video.MediaState.NoMedia);

    // 7. From NoMedia, a different static image => Still (normal path; the
    //    one-frame swap must NOT read as video).
    FillSolid(120, 90, 200);
    s = await RunPhaseAsync("7. different static 3s", 3000, null);
    Check("still (non-stock) detection", s, KHVideoSwitcher.Video.MediaState.Still);

    // 8. Slow sing-along lyric pattern (a line change every ~2s) stays Still —
    //    by design: the operator takes MEDIA manually for songs, and because
    //    the state never flaps, that manual choice sticks.
    var lyricStates = new HashSet<KHVideoSwitcher.Video.MediaState>();
    for (var line = 0; line < 4; line++)
    {
        FillSolid((byte)(50 + line * 25), (byte)(70 + line * 10), 110);
        lyricStates.Add(detector.Analyze(frame, w, h));
        await Task.Delay(33);
        s = await RunPhaseAsync($"8.{line + 1} lyric line, 2s hold", 2000, null);
        lyricStates.Add(s);
    }
    Check("slow lyrics stay Still (manual MEDIA sticks)", s, KHVideoSwitcher.Video.MediaState.Still);
    if (lyricStates.Count != 1)
    {
        Console.WriteLine($"  FAIL: lyric phase flapped between states: {string.Join(", ", lyricStates)}");
        failures++;
    }

    Console.WriteLine(failures == 0 ? "\nAll detector checks PASSED." : $"\n{failures} detector check(s) FAILED.");
}

// Window/monitor capture test: captures the first target whose name contains
// the filter, pumps frames for 3 seconds, reports errors verbatim, saves a PNG.
static async Task RunWgcTestAsync(string nameFilter, string outputPath)
{
    Console.WriteLine("=== KH Video Switcher — Display Capture Test ===\n");

    var targets = KHVideoSwitcher.Capture.DisplayCaptureService.ListTargets();
    Console.WriteLine($"Targets ({targets.Count}):");
    foreach (var t in targets)
        Console.WriteLine($"  {t}");

    var target = targets.FirstOrDefault(t => t.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
    if (target is null)
    {
        Console.WriteLine($"\nFAIL: no target matching '{nameFilter}'.");
        return;
    }
    Console.WriteLine($"\nCapturing: {target}");

    using var svc = new KHVideoSwitcher.Capture.DisplayCaptureService();
    try
    {
        svc.Start(target);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"START FAILED: {ex}");
        return;
    }

    byte[] frame = [];
    int gotW = 0, gotH = 0;
    for (var i = 0; i < 90; i++)
    {
        await Task.Delay(33);
        try
        {
            svc.PumpFrames();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PUMP FAILED (iteration {i}): {ex}");
            return;
        }
        if (svc.TryCopyLatestFrame(ref frame, out gotW, out gotH) && i == 45)
            Console.WriteLine($"  mid-run: receiving {gotW}x{gotH}, age {svc.LastFrameAgeMs} ms");
    }

    if (gotW == 0)
    {
        Console.WriteLine($"FAIL: no frames received in 3s (age: {svc.LastFrameAgeMs} ms).");
        return;
    }

    long sum = 0;
    for (var i = 0; i < frame.Length; i += 2003) sum += frame[i];
    Console.WriteLine($"Received {gotW}x{gotH}; sample sum {sum} ({(sum == 0 ? "ALL BLACK" : "has content")})");
    await SavePngAsync(frame, gotW, gotH, outputPath);
    Console.WriteLine($"Saved {Path.GetFullPath(outputPath)}");
}

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
    compositor.SetCameraFrame(frame, service.Width, service.Height);

    // Synthetic "media" frame: a blue-to-white gradient with a grid, so scene
    // tests don't depend on JW Library running.
    const int mediaW = 1280, mediaH = 720;
    var media = new byte[mediaW * mediaH * 4];
    for (var y = 0; y < mediaH; y++)
    {
        for (var x = 0; x < mediaW; x++)
        {
            int i = (y * mediaW + x) * 4;
            bool grid = x % 80 < 2 || y % 80 < 2;
            media[i] = (byte)(grid ? 255 : 200 * x / mediaW);      // B
            media[i + 1] = (byte)(grid ? 255 : 80);                 // G
            media[i + 2] = (byte)(grid ? 255 : 40 + 100 * y / mediaH); // R
            media[i + 3] = 255;
        }
    }
    compositor.SetMediaFrame(media, mediaW, mediaH);

    var outBytes = new byte[KHVideoSwitcher.Video.PtzCompositor.OutBytes];
    var wide = KHVideoSwitcher.Video.Scene.CameraWide;
    var zoomLeft = new KHVideoSwitcher.Video.Scene(KHVideoSwitcher.Video.SceneKind.Camera,
        new KHVideoSwitcher.Video.PtzState(0.3, 0.5, 2.0));
    var mediaScene = new KHVideoSwitcher.Video.Scene(KHVideoSwitcher.Video.SceneKind.Media, KHVideoSwitcher.Video.PtzState.FullFrame);
    var ots = new KHVideoSwitcher.Video.Scene(KHVideoSwitcher.Video.SceneKind.OverShoulder,
        new KHVideoSwitcher.Video.PtzState(0.3, 0.5, 2.0));

    async Task RenderAsync(string name, KHVideoSwitcher.Video.Scene scene, KHVideoSwitcher.Video.SceneTransition? tr)
    {
        compositor.RenderProgram(scene, tr);
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
    await RenderAsync("scene-media.png", mediaScene, null);
    await RenderAsync("scene-ots.png", ots, null);

    // A transition caught mid-fade: 60 ms duration sampled ~30 ms in (~50%).
    var tr = new KHVideoSwitcher.Video.SceneTransition(wide, ots, 60);
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
