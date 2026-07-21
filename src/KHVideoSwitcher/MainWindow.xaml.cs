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
    private const long MediaStaleMs = 1500;

    private readonly CameraCaptureService _camera = new();
    private readonly DisplayCaptureService _display = new();
    private readonly VirtualCameraController _vcam = new();
    private readonly SharedFrameChannel _vcamChannel = new();
    private readonly PtzCompositor _compositor = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _statusTimer;

    private WriteableBitmap? _previewBitmap;       // raw wide shot (Camera preview)
    private WriteableBitmap? _scenePreviewBitmap;  // rendered scene preview (Media/OTS)
    private WriteableBitmap? _programBitmap;       // 1920x1080 program output
    private byte[] _sourceFrame = Array.Empty<byte>();
    private byte[] _mediaFrame = Array.Empty<byte>();
    private int _renderPending;
    private long _lastFrameCount;
    private volatile bool _vcamOn;

    // Switcher state. Preview (scene kind + PTZ framing) is edited by the
    // operator; the program scene is what viewers see.
    private PtzState _previewState = PtzState.FullFrame;
    private volatile SceneKind _previewKind = SceneKind.Camera;
    private Scene _programScene = Scene.CameraWide;
    private SceneTransition? _transition;
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
            RefreshMediaTargets();
            UpdateSceneButtons();
            UpdateAutoTakeButton();
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

        _compositor.SetCameraFrame(_sourceFrame, w, h);

        // Pull the newest media frame if capture is running and fresh.
        if (_display.IsRunning)
        {
            _display.PumpFrames();
            if (_display.LastFrameAgeMs <= MediaStaleMs &&
                _display.TryCopyLatestFrame(ref _mediaFrame, out var mw, out var mh))
            {
                _compositor.SetMediaFrame(_mediaFrame, mw, mh);
            }
            else if (_display.LastFrameAgeMs > MediaStaleMs)
            {
                _compositor.ClearMedia();
            }
        }
        else
        {
            _compositor.ClearMedia();
        }

        Scene program;
        SceneTransition? transition;
        lock (_ptzLock)
        {
            // Finish a completed transition: the incoming scene becomes program.
            if (_transition is { IsDone: true } done)
            {
                _programScene = done.To;
                _transition = null;
            }
            program = _programScene;
            transition = _transition;
        }

        _compositor.RenderProgram(program, transition);

        if (_vcamOn && _vcamChannel.TryOpen())
        {
            _vcamChannel.WriteFrame(PtzCompositor.OutWidth, PtzCompositor.OutHeight, PtzCompositor.OutWidth * 4,
                dest => _compositor.CopyOutputTo(dest, PtzCompositor.OutBytes));
        }

        // Media/OTS preview needs a rendered frame; Camera preview shows the raw wide shot.
        var previewKind = _previewKind;
        if (previewKind != SceneKind.Camera)
            _compositor.RenderPreview(new Scene(previewKind, _previewState));

        // Coalesce UI renders: drop the notification if one is already queued.
        if (Interlocked.CompareExchange(ref _renderPending, 1, 0) != 0)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                if (_previewKind == SceneKind.Camera)
                    RenderPane(_previewBitmap, bmp => _camera.TryCopyLatestFrame(bmp.BackBuffer, bmp.BackBufferStride * bmp.PixelHeight));
                else
                    RenderPane(_scenePreviewBitmap, bmp => _compositor.CopyPreviewTo(bmp.BackBuffer, bmp.BackBufferStride * bmp.PixelHeight));
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
                _scenePreviewBitmap = new WriteableBitmap(PtzCompositor.OutWidth, PtzCompositor.OutHeight, 96, 96, PixelFormats.Bgra32, null);
                _programBitmap = new WriteableBitmap(PtzCompositor.OutWidth, PtzCompositor.OutHeight, 96, 96, PixelFormats.Bgra32, null);
                ApplyPreviewPaneSource();
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

    // ---------- media capture ----------

    private void MediaCombo_DropDownOpened(object? sender, EventArgs e) => RefreshMediaTargets();

    private void RefreshMediaTargets()
    {
        var selectedName = (MediaCombo.SelectedItem as CaptureTarget)?.Name;
        var targets = DisplayCaptureService.ListTargets();
        MediaCombo.ItemsSource = targets;
        MediaCombo.SelectedItem =
            targets.FirstOrDefault(t => t.Name == selectedName)
            ?? DisplayCaptureService.FindJwLibraryMediaTarget(targets)
            ?? targets.FirstOrDefault(t => t.IsMonitor && !Equals(t, targets.FirstOrDefault(m => m.IsMonitor))); // second monitor
    }

    private void MediaCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_display.IsRunning)
            {
                _display.Stop();
                MediaCaptureButton.Content = "Capture";
                MediaCombo.IsEnabled = true;
            }
            else if (MediaCombo.SelectedItem is CaptureTarget target)
            {
                _display.Start(target);
                MediaCaptureButton.Content = "Capturing…";
                MediaCombo.IsEnabled = false;
                StatusText.Text = $"Capturing: {target.Name}";
            }
            else
            {
                StatusText.Text = "Pick a media window or display first (open the Media dropdown).";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Media capture failed: {ex.Message}";
        }
    }

    // ---------- transport ----------

    private void TakeButton_Click(object sender, RoutedEventArgs e) => Take(_settings.FadeMs);
    private void CutButton_Click(object sender, RoutedEventArgs e) => Take(0);

    private void Take(int fadeMs)
    {
        lock (_ptzLock)
        {
            var target = new Scene(_previewKind, _previewState.Clamped());
            if (_transition is not null)
            {
                // Already fading to this exact scene: let that fade finish.
                if (_transition.To.Equals(target))
                    return;
                // Otherwise land the in-flight transition and fade from there.
                _programScene = _transition.To;
                _transition = null;
            }
            if (fadeMs <= 0 || target.Equals(_programScene))
            {
                _programScene = target;
            }
            else
            {
                _transition = new SceneTransition(_programScene, target, fadeMs);
            }
        }
    }

    /// <summary>In Auto-Take mode, scene selection goes straight to Program.</summary>
    private void TakeIfAuto()
    {
        if (_settings.AutoTakeScenes && _camera.IsRunning)
            Take(_settings.FadeMs);
    }

    private void AutoTakeButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoTakeScenes = !_settings.AutoTakeScenes;
        _settings.Save();
        UpdateAutoTakeButton();
    }

    private void UpdateAutoTakeButton()
    {
        AutoTakeButton.Content = _settings.AutoTakeScenes ? "Auto-Take: ON" : "Auto-Take: Off";
        AutoTakeButton.Background = new SolidColorBrush(_settings.AutoTakeScenes
            ? Color.FromRgb(0x7A, 0x5A, 0x1F)
            : Color.FromRgb(0x33, 0x33, 0x33));
    }

    // ---------- scenes ----------

    private void SceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<SceneKind>(tag, out var kind))
        {
            SetPreviewScene(kind);
            TakeIfAuto();
        }
    }

    private void SetPreviewScene(SceneKind kind)
    {
        _previewKind = kind;
        ApplyPreviewPaneSource();
        UpdateSceneButtons();
        if (kind != SceneKind.Camera && !_display.IsRunning)
            StatusText.Text = "Note: media capture is not running — media scenes show a placeholder.";
    }

    private void ApplyPreviewPaneSource()
    {
        if (_previewKind == SceneKind.Camera)
        {
            PreviewImage.Source = _previewBitmap;
            UpdateCropOverlay();
        }
        else
        {
            PreviewImage.Source = _scenePreviewBitmap;
            CropRectShape.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSceneButtons()
    {
        var active = new SolidColorBrush(Color.FromRgb(0x2A, 0x5E, 0x2A));
        var idle = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        SceneCamButton.Background = _previewKind == SceneKind.Camera ? active : idle;
        SceneMediaButton.Background = _previewKind == SceneKind.Media ? active : idle;
        SceneOtsButton.Background = _previewKind == SceneKind.OverShoulder ? active : idle;
    }

    private void WideButton_Click(object sender, RoutedEventArgs e)
    {
        _previewState = PtzState.FullFrame;
        SetPreviewScene(SceneKind.Camera);
        TakeIfAuto();
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
        SetPreviewScene(SceneKind.Camera);
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
                SetPreviewScene(SceneKind.Camera);
                UpdateCropOverlay();
                TakeIfAuto();
                e.Handled = true;
                break;
            case Key.F1:
                SetPreviewScene(SceneKind.Camera);
                TakeIfAuto();
                e.Handled = true;
                break;
            case Key.F2:
                SetPreviewScene(SceneKind.Media);
                TakeIfAuto();
                e.Handled = true;
                break;
            case Key.F3:
                SetPreviewScene(SceneKind.OverShoulder);
                TakeIfAuto();
                e.Handled = true;
                break;
            case Key.A:
                AutoTakeButton_Click(this, new RoutedEventArgs());
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
        if (view.IsEmpty || _previewKind != SceneKind.Camera)
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
            Scene program;
            lock (_ptzLock)
            {
                program = _programScene;
            }
            var media = !_display.IsRunning ? "off"
                : _display.LastError is not null ? $"ERROR — {_display.LastError}"
                : _display.LastFrameAgeMs > MediaStaleMs ? "no frames (is the window minimized?)"
                : $"{_display.Width}x{_display.Height}";
            StatusText.Text = $"{fmt}   |   live: {fps} fps   |   program: {program.Kind} {program.Ptz.Zoom:0.0}x" +
                              $"   |   media: {media}" +
                              (_vcamOn ? "   |   virtual camera: ON" : "");
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        _camera.FrameArrived -= OnFrameArrived;
        _vcamOn = false;
        _vcam.Dispose();
        _vcamChannel.Dispose();
        await _camera.StopAsync();
        _display.Dispose();
        _compositor.Dispose();
        base.OnClosed(e);
    }
}
