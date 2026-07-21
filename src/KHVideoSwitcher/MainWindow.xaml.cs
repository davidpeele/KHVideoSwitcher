using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KHVideoSwitcher.Capture;
using KHVideoSwitcher.VCam;

namespace KHVideoSwitcher;

public partial class MainWindow : Window
{
    private readonly CameraCaptureService _camera = new();
    private readonly VirtualCameraController _vcam = new();
    private readonly SharedFrameChannel _vcamChannel = new();
    private readonly DispatcherTimer _statusTimer;
    private WriteableBitmap? _previewBitmap;
    private int _renderPending;
    private long _lastFrameCount;
    private volatile bool _vcamOn;

    public MainWindow()
    {
        InitializeComponent();
        _camera.FrameArrived += OnFrameArrived;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var cameras = await CameraCaptureService.ListCamerasAsync();
            CameraCombo.ItemsSource = cameras;
            if (cameras.Count > 0)
            {
                // Prefer an external camera over a built-in one when both exist.
                CameraCombo.SelectedItem =
                    cameras.FirstOrDefault(c => c.Name.Contains("Logitech", StringComparison.OrdinalIgnoreCase))
                    ?? cameras[0];
                StartStopButton.IsEnabled = true;
            }
            else
            {
                StatusText.Text = "No cameras found.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Camera enumeration failed: {ex.Message}";
        }
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        StartStopButton.IsEnabled = false;
        try
        {
            if (_camera.IsRunning)
            {
                await _camera.StopAsync();
                _statusTimer.Stop();
                StartStopButton.Content = "Start";
                CameraCombo.IsEnabled = true;
                StatusText.Text = "Stopped.";
            }
            else if (CameraCombo.SelectedItem is CameraInfo cam)
            {
                StatusText.Text = $"Starting {cam.Name}…";
                var format = await _camera.StartAsync(cam);
                _previewBitmap = new WriteableBitmap(
                    _camera.Width, _camera.Height, 96, 96, PixelFormats.Bgra32, null);
                PreviewImage.Source = _previewBitmap;
                _lastFrameCount = 0;
                _statusTimer.Start();
                StartStopButton.Content = "Stop";
                CameraCombo.IsEnabled = false;
                StatusText.Text = $"{cam.Name} — {format}";
            }
        }
        catch (UnauthorizedAccessException)
        {
            StatusText.Text = "Camera access denied. Enable it in Settings > Privacy & security > Camera " +
                              "(including 'Let desktop apps access your camera').";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to start camera: {ex.Message}";
        }
        finally
        {
            StartStopButton.IsEnabled = true;
        }
    }

    private async void VCamButton_Click(object sender, RoutedEventArgs e)
    {
        VCamButton.IsEnabled = false;
        try
        {
            if (_vcam.IsRunning)
            {
                _vcamOn = false;
                _vcam.Stop();
                VCamButton.Content = "Virtual Camera: Off";
            }
            else
            {
                // Start off the UI thread; creating the camera can take a moment.
                await Task.Run(_vcam.Start);
                _vcamChannel.TryOpen();
                _vcamOn = true;
                VCamButton.Content = "Virtual Camera: ON";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Virtual camera failed: {ex.Message} " +
                              "(Is the KH Video Switcher camera component installed? Run scripts\\install-vcam.ps1.)";
        }
        finally
        {
            VCamButton.IsEnabled = true;
        }
    }

    private void OnFrameArrived()
    {
        // Publish to the virtual camera first (still on the capture thread).
        if (_vcamOn && _vcamChannel.TryOpen())
        {
            int w = _camera.Width, h = _camera.Height;
            _vcamChannel.WriteFrame(w, h, w * 4, dest => _camera.TryCopyLatestFrame(dest, w * h * 4));
        }

        // Coalesce: if a render is already queued, drop this frame notification.
        if (Interlocked.CompareExchange(ref _renderPending, 1, 0) != 0)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                var bmp = _previewBitmap;
                if (bmp is null)
                    return;
                bmp.Lock();
                try
                {
                    if (_camera.TryCopyLatestFrame(bmp.BackBuffer, bmp.BackBufferStride * bmp.PixelHeight))
                        bmp.AddDirtyRect(new Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
                }
                finally
                {
                    bmp.Unlock();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _renderPending, 0);
            }
        });
    }

    private void UpdateStatus()
    {
        long total = _camera.FramesReceived;
        long fps = total - _lastFrameCount;
        _lastFrameCount = total;
        if (_camera.IsRunning && _camera.ActiveFormat is { } fmt)
            StatusText.Text = $"{fmt}   |   live: {fps} fps   |   frames: {total}" +
                              (_vcamOn ? "   |   virtual camera: ON" : "");
    }

    protected override async void OnClosed(EventArgs e)
    {
        _camera.FrameArrived -= OnFrameArrived;
        _vcamOn = false;
        _vcam.Dispose();
        _vcamChannel.Dispose();
        await _camera.StopAsync();
        base.OnClosed(e);
    }
}
