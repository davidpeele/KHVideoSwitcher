// DeviceCheck — Phase 0 sanity test.
// Enumerates video capture devices and display monitors so we know the
// APIs the switcher depends on work on this machine.

using Windows.Devices.Enumeration;
using Windows.Devices.Display;
using Windows.Media.Capture.Frames;

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
