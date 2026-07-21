using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KHVideoSwitcher.Capture;
using KHVideoSwitcher.VCam;
using KHVideoSwitcher.Video;

namespace KHVideoSwitcher;

public partial class MainWindow : Window
{
    private readonly CameraCaptureService _camera = new();
    private readonly VirtualCameraController _vcam = new();
    private readonly SharedFrameChannel _vcamChannel = new();
    private readonly PtzCompositor _compositor = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _statusTimer;

    private WriteableBitmap? _previewBitmap;   // raw wide shot
    private WriteableBitmap? _programBitmap;   // 1920x1080 program output
    private byte[] _sourceFrame = Array.Empty<byte>();
    private int _renderPending;
    private long _lastFrameCount;
    private volatile bool _vcamOn;

    // PTZ state. _preview is edited by the operator; _program is what viewers see.
    private PtzState _previewState = PtzState.FullFrame;
    private PtzState _programState = PtzState.FullFrame;
    private Transition? _transition;
    private readonly object _ptzLock = new();

    private bool _dragging;
    private Point _dragStart;
    private PtzState _dragStartState;

    public MainWindow()
    {
        InitializeComponent();
        _camera.FrameArrived += OnFrameArrived;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        Loaded += MainWindow_Loaded;
        SizeChanged += (_, _) => UpdateCropOverlay();
        RefreshPresetButtons();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var cameras = await CameraCaptureService.ListCamerasAsync();
            CameraCombo.ItemsSource = cameras;
            if (cameras.Count > 0)
            {
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

    // ---------- capture / compose / distribute ----------

    private void OnFrameArrived()
    {
        int w = _camera.Width, h = _camera.Height;
        int size = w * h * 4;
        if (size == 0)
            return;

        if (_sourceFrame.Length != size)
            _sourceFrame = new byte[size];
        if (!_camera.TryCopyLatestFrame(_sourceFrame))
            return;

        PtzState program;
        Transition? transition;
        lock (_ptzLock)
        {
            // Finish a completed transition: the incoming shot becomes program.
            if (_transition is { IsDone: true } done)
            {
                _programState = done.To;
                _transition = null;
            }
            program = _programState;
            transition = _transition;
        }

        _compositor.Compose(_sourceFrame, w, h, program, transition);

        if (_vcamOn && _vcamChannel.TryOpen())
        {
            _vcamChannel.WriteFrame(PtzCompositor.OutWidth, PtzCompositor.OutHeight, PtzCompositor.OutWidth * 4,
                dest => _compositor.CopyOutputTo(dest, PtzCompositor.OutBytes));
        }

        // Coalesce UI renders: drop the notification if one is already queued.
        if (Interlocked.CompareExchange(ref _renderPending, 1, 0) != 0)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                RenderPane(_previewBitmap, bmp => _camera.TryCopyLatestFrame(bmp.BackBuffer, bmp.BackBufferStride * bmp.PixelHeight));
                RenderPane(_programBitmap, bmp => _compositor.CopyOutputTo(bmp.BackBuffer, bmp.BackBufferStride * bmp.PixelHeight));
            }
            finally
            {
                Interlocked.Exchange(ref _renderPending, 0);
            }
        });
    }

    private static void RenderPane(WriteableBitmap? bmp, Func<WriteableBitmap, bool> fill)
    {
        if (bmp is null)
            return;
        bmp.Lock();
        try
        {
            if (fill(bmp))
                bmp.AddDirtyRect(new Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
        }
        finally
        {
            bmp.Unlock();
        }
    }

    // ---------- toolbar ----------

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
                _previewBitmap = new WriteableBitmap(_camera.Width, _camera.Height, 96, 96, PixelFormats.Bgra32, null);
                _programBitmap = new WriteableBitmap(PtzCompositor.OutWidth, PtzCompositor.OutHeight, 96, 96, PixelFormats.Bgra32, null);
                PreviewImage.Source = _previewBitmap;
                ProgramImage.Source = _programBitmap;
                _lastFrameCount = 0;
                _statusTimer.Start();
                StartStopButton.Content = "Stop";
                CameraCombo.IsEnabled = false;
                StatusText.Text = $"{cam.Name} — {format}";
                UpdateCropOverlay();
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

    // ---------- transport ----------

    private void TakeButton_Click(object sender, RoutedEventArgs e) => Take(_settings.FadeMs);
    private void CutButton_Click(object sender, RoutedEventArgs e) => Take(0);

    private void Take(int fadeMs)
    {
        lock (_ptzLock)
        {
            var target = _previewState.Clamped();
            if (_transition is not null)
            {
                // Land the in-flight transition first, then fade from there.
                _programState = _transition.To;
                _transition = null;
            }
            if (fadeMs <= 0 || target.Equals(_programState))
            {
                _programState = target;
            }
            else
            {
                _transition = new Transition(_programState, target, fadeMs);
            }
        }
    }

    private void WideButton_Click(object sender, RoutedEventArgs e)
    {
        _previewState = PtzState.FullFrame;
        UpdateCropOverlay();
    }

    // ---------- presets ----------

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index))
            RecallPreset(index);
    }

    private void Preset_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index))
        {
            _settings.Presets[index] = _previewState.Clamped();
            _settings.Save();
            RefreshPresetButtons();
            StatusText.Text = $"Preset {index + 1} saved.";
            e.Handled = true;
        }
    }

    private void RecallPreset(int index)
    {
        if (index < 0 || index >= _settings.Presets.Length || _settings.Presets[index] is not { } preset)
        {
            StatusText.Text = $"Preset {index + 1} is empty — frame a shot in PREVIEW and right-click the button to save it.";
            return;
        }
        _previewState = preset;
        UpdateCropOverlay();
        Take(_settings.FadeMs);
    }

    private void RefreshPresetButtons()
    {
        Button[] buttons = [Preset1, Preset2, Preset3, Preset4, Preset5, Preset6];
        for (var i = 0; i < buttons.Length; i++)
        {
            var saved = i < _settings.Presets.Length && _settings.Presets[i] is not null;
            buttons[i].Content = saved ? $"{i + 1} ●" : $"{i + 1}";
            buttons[i].Foreground = saved ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
        }
    }

    // ---------- keyboard ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || CameraCombo.IsDropDownOpen)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                Take(_settings.FadeMs);
                e.Handled = true;
                break;
            case Key.Space:
                Take(0);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                _previewState = PtzState.FullFrame;
                UpdateCropOverlay();
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D6:
                HandlePresetKey(e.Key - Key.D1, e);
                break;
            case >= Key.NumPad1 and <= Key.NumPad6:
                HandlePresetKey(e.Key - Key.NumPad1, e);
                break;
        }
    }

    private void HandlePresetKey(int index, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _settings.Presets[index] = _previewState.Clamped();
            _settings.Save();
            RefreshPresetButtons();
            StatusText.Text = $"Preset {index + 1} saved.";
        }
        else
        {
            RecallPreset(index);
        }
        e.Handled = true;
    }

    // ---------- preview pane interaction ----------

    /// <summary>Rectangle (in PreviewCell coordinates) actually covered by the video image.</summary>
    private Rect GetDisplayedImageRect()
    {
        double cw = PreviewCell.ActualWidth, ch = PreviewCell.ActualHeight;
        int sw = _camera.Width, sh = _camera.Height;
        if (cw <= 0 || ch <= 0 || sw <= 0 || sh <= 0)
            return Rect.Empty;

        double scale = Math.Min(cw / sw, ch / sh);
        double dw = sw * scale, dh = sh * scale;
        return new Rect((cw - dw) / 2, (ch - dh) / 2, dw, dh);
    }

    private void UpdateCropOverlay()
    {
        var view = GetDisplayedImageRect();
        if (view.IsEmpty)
        {
            CropRectShape.Visibility = Visibility.Collapsed;
            return;
        }

        var (x, y, w, h) = _previewState.CropRect(_camera.Width, _camera.Height);
        double scale = view.Width / _camera.Width;
        CropRectShape.Visibility = Visibility.Visible;
        Canvas.SetLeft(CropRectShape, view.X + x * scale);
        Canvas.SetTop(CropRectShape, view.Y + y * scale);
        CropRectShape.Width = Math.Max(0, w * scale);
        CropRectShape.Height = Math.Max(0, h * scale);
    }

    private void PreviewCell_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_camera.IsRunning)
            return;
        double factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        _previewState = _previewState with { Zoom = Math.Clamp(_previewState.Zoom * factor, PtzState.MinZoom, PtzState.MaxZoom) };
        _previewState = _previewState.Clamped();
        UpdateCropOverlay();
    }

    private void PreviewCell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_camera.IsRunning)
            return;
        _dragging = true;
        _dragStart = e.GetPosition(PreviewCell);
        _dragStartState = _previewState;
        PreviewCell.CaptureMouse();
    }

    private void PreviewCell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        PreviewCell.ReleaseMouseCapture();
    }

    private void PreviewCell_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        var view = GetDisplayedImageRect();
        if (view.IsEmpty)
            return;

        var pos = e.GetPosition(PreviewCell);
        double scale = view.Width / _camera.Width;
        double dx = (pos.X - _dragStart.X) / scale / _camera.Width;
        double dy = (pos.Y - _dragStart.Y) / scale / _camera.Height;
        _previewState = (_dragStartState with
        {
            CenterX = _dragStartState.CenterX + dx,
            CenterY = _dragStartState.CenterY + dy,
        }).Clamped();
        UpdateCropOverlay();
    }

    // ---------- status / shutdown ----------

    private void UpdateStatus()
    {
        long total = _camera.FramesReceived;
        long fps = total - _lastFrameCount;
        _lastFrameCount = total;
        if (_camera.IsRunning && _camera.ActiveFormat is { } fmt)
        {
            var pvZoom = _previewState.Zoom;
            var pgZoom = _programState.Zoom;
            StatusText.Text = $"{fmt}   |   live: {fps} fps   |   preview zoom: {pvZoom:0.0}x   |   program zoom: {pgZoom:0.0}x" +
                              (_vcamOn ? "   |   virtual camera: ON" : "");
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        _camera.FrameArrived -= OnFrameArrived;
        _vcamOn = false;
        _vcam.Dispose();
        _vcamChannel.Dispose();
        _compositor.Dispose();
        await _camera.StopAsync();
        base.OnClosed(e);
    }
}
