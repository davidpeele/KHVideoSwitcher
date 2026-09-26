using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KHVideoSwitcher.Capture;
using KHVideoSwitcher.Diagnostics;
using KHVideoSwitcher.VCam;
using KHVideoSwitcher.Video;
using MediaState = KHVideoSwitcher.Video.MediaState;

namespace KHVideoSwitcher;

public partial class MainWindow : Window
{
    // ---------- shared status-rail visual rule ----------
    // One rule everywhere: engaged/ON = solid accent fill; idle/OFF = outline.
    // "Needs attention" (Set Stock) = dashed accent-700 outline until resolved.
    // The app is code-behind driven with no bound view-model, so this is
    // implemented as one shared helper (rather than a XAML DataTrigger) that
    // every toggle's Update*() method calls.

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Applies the engaged/idle fill-vs-outline rule and updates a tag's state Run.</summary>
    private static void ApplyEngagedStyle(Button tag, Run stateRun, bool on)
    {
        if (on)
        {
            var accent = Res("AccentBrush");
            tag.Background = accent;
            tag.Foreground = Res("BgBrush");
            tag.BorderBrush = accent;
        }
        else
        {
            tag.Background = Brushes.Transparent;
            tag.Foreground = Res("TextBrush");
            tag.BorderBrush = Res("DividerBrush");
        }
        stateRun.Text = on ? "ON" : "OFF";
    }

    /// <summary>Same engaged/idle fill-vs-outline rule as <see cref="ApplyEngagedStyle"/>, for a
    /// plain-content toggle button (Capture Camera / Capture Screen) whose text isn't a fixed ON/OFF Run.</summary>
    private static void ApplyEngagedButtonStyle(Button button, bool on)
    {
        if (on)
        {
            var accent = Res("AccentBrush");
            button.Background = accent;
            button.Foreground = Res("BgBrush");
            button.BorderBrush = accent;
        }
        else
        {
            button.Background = Brushes.Transparent;
            button.Foreground = Res("TextBrush");
            button.BorderBrush = Res("DividerBrush");
        }
    }

    /// <summary>Applies the "needs attention" dashed-outline rule (Set Stock).</summary>
    private static void ApplyAttentionStyle(Button tag, Run stateRun, bool resolved, string resolvedText, string attentionText)
    {
        tag.Background = Brushes.Transparent;
        if (resolved)
        {
            tag.Foreground = Res("MutedTextBrush");
            tag.BorderBrush = Res("DividerBrush");
            tag.BorderThickness = new Thickness(1);
            tag.Template = (ControlTemplate)Application.Current.Resources["StatusTagTemplate"];
        }
        else
        {
            tag.Foreground = Res("Accent700Brush");
            tag.BorderBrush = Res("Accent700Brush");
            tag.BorderThickness = new Thickness(1.5);
            tag.Template = (ControlTemplate)Application.Current.Resources["StatusTagDashedTemplate"];
        }
        stateRun.Text = resolved ? resolvedText : attentionText;
    }

    private readonly CameraCaptureService _camera = new();
    private readonly DisplayCaptureService _display = new();
    private readonly MediaStateDetector _detector = new();
    private MediaState _lastAutoState = MediaState.NoFeed;
    private readonly VirtualCameraController _vcam = new();
    private readonly SharedFrameChannel _vcamChannel = new();
    private readonly PtzCompositor _compositor = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _statusTimer;
    private UpdateChecker.UpdateInfo? _pendingUpdate;
    private bool _updateDownloading;

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
    private bool _syncingZoomSlider;

    private Thread? _composeThread;
    private volatile bool _composeActive;
    private bool _haveCameraFrame;
    private int _cameraStalledSeconds;
    private bool _cameraRecovering;

    public MainWindow()
    {
        InitializeComponent();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        Loaded += MainWindow_Loaded;
        SizeChanged += (_, _) => LayoutPanes();
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
                    cameras.FirstOrDefault(c => c.Name == _settings.LastCameraName)
                    ?? cameras.FirstOrDefault(c => c.Name.Contains("Logitech", StringComparison.OrdinalIgnoreCase))
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
            UpdateAutoScenesButton();
            UpdateDragModeTag();
            UpdateVCamRailTag();
            UpdateStockReminder();
            SyncZoomSlider();
            _detector.ImportStock(_settings.StockFingerprint);
            ApplySettings();
            LayoutPanes();
            _ = CheckForUpdatesAsync(silent: true);
            await InitializeAudioMeterAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Camera enumeration failed: {ex.Message}";
            AppLog.Error("Camera enumeration failed", ex);
        }
    }

    // ---------- update check ----------

    /// <summary>
    /// Silent (startup) checks are throttled to once/day and never bother the
    /// operator on failure (e.g. offline at the Hall) - only a found update
    /// surfaces, via the UPDATE status-rail tag. A manual check always reports.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (silent)
        {
            var last = _settings.LastUpdateCheckUtc;
            if (last is not null && DateTime.UtcNow - last.Value < TimeSpan.FromDays(1))
                return;
        }

        try
        {
            var update = await UpdateChecker.CheckAsync();
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _settings.Save();
            _pendingUpdate = update;
            UpdateUpdateTag();
            if (!silent)
            {
                StatusText.Text = update is null
                    ? $"You're up to date (v{UpdateChecker.CurrentVersion})."
                    : $"KH Video Switcher {update.TagName} is available — click UPDATE to install.";
            }
        }
        catch (Exception ex)
        {
            if (!silent)
                StatusText.Text = $"Update check failed: {ex.Message}";
        }
    }

    private void UpdateUpdateTag()
    {
        if (_pendingUpdate is null)
        {
            UpdateTag.Visibility = Visibility.Collapsed;
            return;
        }
        UpdateTag.Visibility = Visibility.Visible;
        ApplyAttentionStyle(UpdateTag, UpdateStateRun, resolved: false, "AVAILABLE", _pendingUpdate.TagName);
    }

    private async void UpdateTag_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null || _updateDownloading)
            return;

        var info = _pendingUpdate;
        var result = MessageBox.Show(this,
            $"KH Video Switcher {info.TagName} is available (you're on v{UpdateChecker.CurrentVersion}).\n\n" +
            "Download and install it now? The app will close and the installer will open — " +
            "Windows may ask for administrator approval.",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        _updateDownloading = true;
        UpdateTag.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => StatusText.Text = $"Downloading {info.TagName}... {p:P0}");
            StatusText.Text = $"Downloading {info.TagName}...";
            var installerPath = await UpdateChecker.DownloadInstallerAsync(info, progress, CancellationToken.None);
            StatusText.Text = "Verified. Launching installer...";
            UpdateChecker.LaunchInstallerAndExit(installerPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Update download failed: {ex.Message}";
            _updateDownloading = false;
            UpdateTag.IsEnabled = true;
        }
    }

    // ---------- compose clock ----------

    // The output runs on its own 30 fps clock, independent of the camera:
    // a stalled camera repeats its last frame instead of freezing the
    // program output, fades, and scene switches.
    private void StartComposeLoop()
    {
        if (_composeThread is not null)
            return;
        _composeActive = true;
        _composeThread = new Thread(ComposeLoop) { IsBackground = true, Name = "Compose" };
        _composeThread.Start();
    }

    private void StopComposeLoop()
    {
        _composeActive = false;
        _composeThread?.Join(500);
        _composeThread = null;
        _haveCameraFrame = false;
    }

    private void ComposeLoop()
    {
        const long frameMs = 33;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long next = 0;
        while (_composeActive)
        {
            try
            {
                ComposeTick();
            }
            catch
            {
                // A transient compose failure must never kill the output clock.
            }
            next += frameMs;
            long sleep = next - sw.ElapsedMilliseconds;
            if (sleep > 1)
                Thread.Sleep((int)sleep);
            else if (sleep < -250)
                next = sw.ElapsedMilliseconds; // fell badly behind; resync
        }
    }

    private void ComposeTick()
    {
        int w = _camera.Width, h = _camera.Height;
        int size = w * h * 4;
        if (size == 0)
            return;

        if (_sourceFrame.Length != size)
        {
            _sourceFrame = new byte[size];
            _haveCameraFrame = false;
        }
        // A failed copy (camera hiccup) keeps the previous frame content.
        if (_camera.TryCopyLatestFrame(_sourceFrame))
            _haveCameraFrame = true;
        if (!_haveCameraFrame)
            return;

        _compositor.SetCameraFrame(_sourceFrame, w, h);

        // Pull the newest media frame if capture is running and fresh.
        MediaState mediaState;
        if (_display.IsRunning)
        {
            _display.PumpFrames();
            // Windows Graphics Capture only delivers a frame when the desktop
            // actually recomposites the captured surface — a static picture
            // (e.g. JW Library's year text with no motion) can legitimately go
            // minutes without a new frame, for both window and monitor
            // targets. So "no recent frame" is not staleness; only "no frame
            // ever" (nothing captured yet) or a minimized window (which WGC
            // genuinely stops updating) means there's no feed to show.
            bool mediaFresh = _display.LastFrameAgeMs != long.MaxValue && !_display.IsTargetMinimized;
            if (mediaFresh && _display.TryCopyLatestFrame(ref _mediaFrame, out var mw, out var mh))
            {
                _compositor.SetMediaFrame(_mediaFrame, mw, mh);
                mediaState = _detector.Analyze(_mediaFrame, mw, mh);
            }
            else
            {
                if (!mediaFresh)
                    _compositor.ClearMedia();
                mediaState = _detector.AnalyzeNoFeed();
            }
        }
        else
        {
            _compositor.ClearMedia();
            mediaState = _detector.AnalyzeNoFeed();
        }

        // The auto-director reacts to state CHANGES only, so manual takes
        // stick until JW Library actually does something different.
        if (mediaState != _lastAutoState)
        {
            _lastAutoState = mediaState;
            if (_settings.AutoScenes)
                AutoSwitch(mediaState);
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

        if (_vcamOn && _vcamChannel.TryOpenForWrite())
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
                StopComposeLoop();
                await _camera.StopAsync();
                _statusTimer.Stop();
                StartStopButton.Content = "Capture Camera";
                ApplyEngagedButtonStyle(StartStopButton, on: false);
                CameraCombo.IsEnabled = true;
                StatusText.Text = "Stopped.";
                AppLog.Info("Camera stopped.");
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
                StartStopButton.Content = "Camera Captured";
                ApplyEngagedButtonStyle(StartStopButton, on: true);
                CameraCombo.IsEnabled = false;
                StatusText.Text = $"{cam.Name} — {format}";
                UpdateCropOverlay();
                StartComposeLoop();
                _settings.LastCameraName = cam.Name;
                _settings.Save();
                AppLog.Info($"Camera started: {cam.Name} — {format}");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusText.Text = "Camera access denied. Enable it in Settings > Privacy & security > Camera " +
                              "(including 'Let desktop apps access your camera').";
            AppLog.Error("Camera access denied", ex);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to start camera: {ex.Message}";
            AppLog.Error("Failed to start camera", ex);
        }
        finally
        {
            StartStopButton.IsEnabled = true;
        }
    }

    private async void VCamButton_Click(object sender, RoutedEventArgs e)
    {
        VCamButton.IsEnabled = false;
        VCamRailTag.IsEnabled = false;
        try
        {
            if (_vcam.IsRunning)
            {
                _vcamOn = false;
                _vcam.Stop();
                AppLog.Info("Virtual camera stopped.");
            }
            else
            {
                await Task.Run(_vcam.Start);
                _vcamChannel.TryOpenForWrite();
                _vcamOn = true;
                AppLog.Info("Virtual camera started.");
            }
            UpdateVCamRailTag();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Virtual camera failed: {ex.Message} " +
                              "(Is the KH Video Switcher camera component installed? Run scripts\\install-vcam.ps1.)";
            AppLog.Error("Virtual camera toggle failed", ex);
        }
        finally
        {
            VCamButton.IsEnabled = true;
            VCamRailTag.IsEnabled = true;
        }
    }

    private void UpdateVCamRailTag() => ApplyEngagedStyle(VCamRailTag, VCamStateRun, _vcam.IsRunning);

    // ---------- media capture ----------

    private void MediaCombo_DropDownOpened(object? sender, EventArgs e) => RefreshMediaTargets();

    private void RefreshMediaTargets()
    {
        var selectedName = (MediaCombo.SelectedItem as CaptureTarget)?.Name;
        var targets = DisplayCaptureService.ListTargets();
        MediaCombo.ItemsSource = targets;
        MediaCombo.SelectedItem =
            targets.FirstOrDefault(t => t.Name == selectedName)
            ?? targets.FirstOrDefault(t => t.Name == _settings.LastMediaTargetName)
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
                MediaCaptureButton.Content = "Capture Screen";
                ApplyEngagedButtonStyle(MediaCaptureButton, on: false);
                MediaCombo.IsEnabled = true;
                AppLog.Info("Media capture stopped.");
            }
            else if (!_camera.IsRunning)
            {
                // PumpFrames() only runs on the compose thread, which the camera
                // Capture Camera button spins up. Without it, a capture session opens but
                // never delivers a visible frame — indistinguishable from broken
                // to the user, so refuse up front instead of leaving them staring
                // at a black Media preview with no clue why.
                StatusText.Text = "Capture the camera first (top-left Capture Camera button) — Capture Screen needs it running to show anything.";
            }
            else if (MediaCombo.SelectedItem is CaptureTarget target)
            {
                _display.Start(target);
                MediaCaptureButton.Content = "Screen Captured";
                ApplyEngagedButtonStyle(MediaCaptureButton, on: true);
                MediaCombo.IsEnabled = false;
                StatusText.Text = $"Capturing: {target.Name}";
                _settings.LastMediaTargetName = target.Name;
                _settings.Save();
                AppLog.Info($"Media capture started: {target.Name}");
            }
            else
            {
                StatusText.Text = "Pick a media window or display first (open the Media dropdown).";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Media capture failed: {ex.Message}";
            AppLog.Error("Media capture failed to start", ex);
        }
    }

    // ---------- transport ----------

    private void TakeButton_Click(object sender, RoutedEventArgs e) => Take(_settings.FadeMs);
    private void CutButton_Click(object sender, RoutedEventArgs e) => Take(0);

    private void Take(int fadeMs) => TakeTo(new Scene(_previewKind, _previewState.Clamped()), fadeMs);

    private void TakeTo(Scene target, int fadeMs)
    {
        lock (_ptzLock)
        {
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

    /// <summary>Auto-director action for a media state change (compose thread).</summary>
    private void AutoSwitch(MediaState state)
    {
        var ptz = _previewState.Clamped();
        var target = state switch
        {
            MediaState.Video => new Scene(SceneKind.Media, ptz),
            MediaState.Still => new Scene(_settings.AlwaysFullScreenFirst ? SceneKind.Media : SceneKind.OverShoulder, ptz),
            _ => new Scene(SceneKind.Camera, ptz),
        };
        TakeTo(target, _settings.FadeMs);
    }

    private void AutoScenesButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoScenes = !_settings.AutoScenes;
        _settings.Save();
        UpdateAutoScenesButton();
        if (_settings.AutoScenes)
        {
            if (!_display.IsRunning)
                StatusText.Text = "AUTO is on, but media capture is not running — start Capture so JW Library can be watched.";
            else if (!_detector.HasStock)
                StatusText.Text = "AUTO is on. Tip: click Set Stock while the yeartext screen shows, so 'no media' is recognized.";
            AutoSwitch(_detector.State);
        }
    }

    private void UpdateAutoScenesButton() => ApplyEngagedStyle(AutoScenesButton, AutoScenesStateRun, _settings.AutoScenes);

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_settings, ApplySettings, SetAudioDeviceAsync) { Owner = this }.Show();
    }

    private void ReportBugButton_Click(object sender, RoutedEventArgs e)
    {
        var cameraName = (CameraCombo.SelectedItem as CameraInfo)?.Name;
        var mediaName = (MediaCombo.SelectedItem as CaptureTarget)?.Name;
        var diagnostics = DiagnosticsReport.Build(_settings, cameraName, mediaName, _vcam.IsRunning);
        new ReportBugWindow(diagnostics) { Owner = this }.Show();
    }

    private HelpWindow? _helpWindow;

    /// <summary>Reuses a single Help window instead of stacking new ones — it's meant to stay open alongside the app.</summary>
    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_helpWindow is null)
        {
            _helpWindow = new HelpWindow { Owner = this };
            _helpWindow.Closed += (_, _) => _helpWindow = null;
            _helpWindow.Show();
        }
        else
        {
            _helpWindow.Activate();
        }
    }

    /// <summary>Pushes current settings into the live pipeline and persists them.</summary>
    private void ApplySettings()
    {
        _compositor.InsetWidthFraction = _settings.OtsInsetWidthFraction;
        _compositor.InsetMarginTop = _settings.OtsInsetTopMargin;
        _compositor.InsetMarginRight = _settings.OtsInsetRightMargin;
        _compositor.ShiftCameraForInset = _settings.OtsShiftCameraForInset;
        _compositor.InsetShiftFraction = _settings.OtsShiftFraction;
        _compositor.BackgroundColor = ParseColor(_settings.OtsBackgroundColor);
        _compositor.BorderEnabled = _settings.OtsBorderEnabled;
        _compositor.BorderColor = ParseColor(_settings.OtsBorderColor);
        _display.HideBorder = _settings.HideCaptureBorder;
        ApplyAudioMeterVisibility();
        _settings.Save();
    }

    /// <summary>Parses a "#RRGGBB" (or named/short) color into 0..1 linear RGB components for Direct2D.</summary>
    private static (float R, float G, float B) ParseColor(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return (c.R / 255f, c.G / 255f, c.B / 255f);
    }

    private bool _compact;

    private void CompactButton_Click(object sender, RoutedEventArgs e)
    {
        _compact = !_compact;
        PreviewPane.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        PreviewCol.Width = _compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SpacerCol.Width = new GridLength(_compact ? 0 : 12);
        CompactButton.Content = _compact ? "Full View" : "Compact";
        LayoutPanes();
    }

    private void PanesGrid_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => LayoutPanes();

    /// <summary>
    /// Sizes the video panes to their true 16:9 height (bounded by the space
    /// the window actually has), so tall windows don't waste screen on
    /// letterboxing and the transport row stays right under the video.
    /// </summary>
    private void LayoutPanes()
    {
        double gridW = PanesGrid.ActualWidth;
        if (gridW <= 60 || !IsLoaded)
            return;

        double paneW = _compact ? gridW : Math.Max(120, (gridW - 14) / 2);
        double videoW = paneW - 2; // border
        // Preview and Program each carry a row below the video (ZoomBar / the audio meter)
        // of different heights - budget for whichever is taller so neither one runs the
        // window out of vertical space.
        double belowVideoH = Math.Max(ZoomBar.ActualHeight, AudioMeterPane.ActualHeight);
        double budget = RootGrid.ActualHeight - ToolbarPanel.ActualHeight - StatusRailPanel.ActualHeight
                        - TransportPanel.ActualHeight - PresetsPanel.ActualHeight - StatusText.ActualHeight
                        - belowVideoH - 60;
        double h = Math.Min(videoW * 9.0 / 16 + 4, Math.Max(150, budget));
        PreviewBorder.Height = h;
        ProgramBorder.Height = h;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateCropOverlay);
    }

    private void StockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detector.TryCaptureStock(out var base64))
        {
            _settings.StockFingerprint = base64;
            _settings.StockTargetName = StripSizeSuffix(_display.TargetName);
            _settings.Save();
            StatusText.Text = "Stock (no media) screen fingerprinted — automatic switching will treat this screen as NO MEDIA.";
            UpdateStockReminder();
        }
        else
        {
            StatusText.Text = "No media frame available yet — start Capture first, with the yeartext screen showing.";
        }
    }

    /// <summary>"JW Library (528x358)" -> "JW Library" (windows resize; identity doesn't).</summary>
    private static string? StripSizeSuffix(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        int i = name.LastIndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? name[..i] : name;
    }

    /// <summary>Why Set Stock deserves attention right now, or null if all is well.</summary>
    private string? GetStockReminder()
    {
        if (!_display.IsRunning || _display.LastError is not null)
            return null;
        if (!_detector.HasStock)
            return "Set Stock not done: show the yeartext screen and click Set Stock so AUTO can recognize 'no media'";
        if (!string.IsNullOrEmpty(_settings.StockTargetName) &&
            !string.Equals(StripSizeSuffix(_display.TargetName), _settings.StockTargetName, StringComparison.Ordinal))
            return "Media source changed since Set Stock: show the yeartext and click Set Stock again";
        return null;
    }

    private void UpdateStockReminder()
    {
        bool resolved = GetStockReminder() is null;
        ApplyAttentionStyle(StockButton, StockStateRun, resolved, "SET", "NOT SET");
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

    private void UpdateAutoTakeButton() => ApplyEngagedStyle(AutoTakeButton, AutoTakeStateRun, _settings.AutoTakeScenes);

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
        SetSceneTile(SceneCamButton, SceneCamDot, SceneCamHint, _previewKind == SceneKind.Camera);
        SetSceneTile(SceneMediaButton, SceneMediaDot, SceneMediaHint, _previewKind == SceneKind.Media);
        SetSceneTile(SceneOtsButton, SceneOtsDot, SceneOtsHint, _previewKind == SceneKind.OverShoulder);
    }

    private static void SetSceneTile(Button tile, Run dot, Run hint, bool active)
    {
        if (active)
        {
            var accent = Res("AccentBrush");
            tile.Background = accent;
            tile.Foreground = Res("BgBrush");
            hint.Foreground = Res("MutedOnAccentBrush");
        }
        else
        {
            tile.Background = Brushes.Transparent;
            tile.Foreground = Res("TextBrush");
            hint.Foreground = Res("MutedTextBrush");
        }
        dot.Text = active ? "●" : "○";
    }

    private void WideButton_Click(object sender, RoutedEventArgs e)
    {
        _previewState = PtzState.FullFrame;
        SetPreviewScene(SceneKind.Camera);
        SyncZoomSlider();
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
            SavePreset(index);
            e.Handled = true;
        }
    }

    private void SavePreset(int index)
    {
        var preset = new Scene(_previewKind, _previewState.Clamped());
        _settings.Presets[index] = preset;
        _settings.Save();
        RefreshPresetButtons();
        StatusText.Text = $"Preset {index + 1} saved: {KindTag(preset.Kind)} at {preset.Ptz.Zoom:0.0}x.";
    }

    private void RecallPreset(int index)
    {
        if (index < 0 || index >= _settings.Presets.Length || _settings.Presets[index] is not { } preset)
        {
            StatusText.Text = $"Preset {index + 1} is empty — set up a shot in PREVIEW and right-click the button to save it.";
            return;
        }
        _previewState = preset.Ptz;
        SetPreviewScene(preset.Kind);
        UpdateCropOverlay();
        SyncZoomSlider();
        Take(_settings.FadeMs);
    }

    private static string KindTag(SceneKind kind) => kind switch
    {
        SceneKind.Media => "MED",
        SceneKind.OverShoulder => "OTS",
        _ => "CAM",
    };

    private void RefreshPresetButtons()
    {
        Button[] buttons = [Preset1, Preset2, Preset3, Preset4, Preset5, Preset6];
        for (var i = 0; i < buttons.Length; i++)
        {
            var preset = i < _settings.Presets.Length ? _settings.Presets[i] : null;
            var button = buttons[i];
            bool saved = preset is not null;

            var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = (i + 1).ToString(),
                FontFamily = (FontFamily)Application.Current.Resources["HeadingFont"],
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = saved ? KindTag(preset!.Value.Kind) : "",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            });
            button.Content = stack;

            button.Foreground = saved ? Res("TextBrush") : Res("MutedTextBrush");
            button.BorderBrush = saved ? Res("Accent700Brush") : Res("DividerBrush");
            button.Template = saved
                ? (ControlTemplate)Application.Current.Resources["StatusTagTemplate"]
                : (ControlTemplate)Application.Current.Resources["StatusTagDashedTemplate"];
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
                SyncZoomSlider();
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
            case Key.F4:
                AutoScenesButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F11:
                CompactButton_Click(this, new RoutedEventArgs());
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
            SavePreset(index);
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
        SetPreviewZoom(_previewState.Zoom * factor);
    }

    private void SetPreviewZoom(double zoom)
    {
        _previewState = (_previewState with { Zoom = Math.Clamp(zoom, PtzState.MinZoom, PtzState.MaxZoom) }).Clamped();
        UpdateCropOverlay();
        SyncZoomSlider();
    }

    private void SyncZoomSlider()
    {
        _syncingZoomSlider = true;
        try
        {
            ZoomSlider.Value = _previewState.Zoom;
            ZoomReadout.Text = $"{_previewState.Zoom:0.0}x";
        }
        finally
        {
            _syncingZoomSlider = false;
        }
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingZoomSlider || ZoomReadout is null)
            return;
        _previewState = (_previewState with { Zoom = Math.Clamp(e.NewValue, PtzState.MinZoom, PtzState.MaxZoom) }).Clamped();
        ZoomReadout.Text = $"{_previewState.Zoom:0.0}x";
        UpdateCropOverlay();
    }

    private void DragModeTag_Click(object sender, RoutedEventArgs e)
    {
        _settings.DragMovesPicture = !_settings.DragMovesPicture;
        _settings.Save();
        UpdateDragModeTag();
    }

    private void UpdateDragModeTag() => ApplyEngagedStyle(DragModeTag, DragModeStateRun, _settings.DragMovesPicture);

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
        // "Drag moves picture": pull the image with the mouse (crop moves the
        // other way). Unchecked: drag moves the crop box / camera directly.
        double sign = _settings.DragMovesPicture ? -1 : 1;
        _previewState = (_dragStartState with
        {
            CenterX = _dragStartState.CenterX + sign * dx,
            CenterY = _dragStartState.CenterY + sign * dy,
        }).Clamped();
        UpdateCropOverlay();
    }

    // ---------- status / shutdown ----------

    private void UpdateStatus()
    {
        long total = _camera.FramesReceived;
        long fps = total - _lastFrameCount;
        _lastFrameCount = total;

        // Camera watchdog: a camera that stops delivering (USB glitch,
        // unplug/replug) gets restarted automatically.
        if (_camera.IsRunning && !_cameraRecovering)
        {
            _cameraStalledSeconds = fps == 0 ? _cameraStalledSeconds + 1 : 0;
            if (_cameraStalledSeconds >= 5)
            {
                _cameraStalledSeconds = 0;
                _ = RecoverCameraAsync();
            }
        }
        if (_camera.IsRunning && _camera.ActiveFormat is { } fmt)
        {
            Scene program;
            lock (_ptzLock)
            {
                program = _programScene;
            }
            bool mediaNoFeed = _display.LastFrameAgeMs == long.MaxValue || _display.IsTargetMinimized;
            var media = !_display.IsRunning ? "off"
                : _display.LastError is not null ? $"ERROR — {_display.LastError}"
                : mediaNoFeed ? (_display.IsTargetMinimized ? "no frames (window is minimized)" : "no frames yet")
                : $"{_display.Width}x{_display.Height}";
            var detect = _display.IsRunning
                ? $"   |   detect: {_detector.State}{(_settings.AutoScenes ? " → AUTO" : "")}"
                : "";
            var reminder = GetStockReminder() is { } hint ? $"   |   ⚠ {hint}" : "";
            StatusText.Text = $"{fmt}   |   live: {fps} fps   |   program: {program.Kind} {program.Ptz.Zoom:0.0}x" +
                              $"   |   media: {media}{detect}" +
                              (_vcamOn ? "   |   virtual camera: ON" : "") + reminder;
        }
        UpdateStockReminder();
    }

    private async Task RecoverCameraAsync()
    {
        if (_cameraRecovering || CameraCombo.SelectedItem is not CameraInfo cam)
            return;
        _cameraRecovering = true;
        try
        {
            StatusText.Text = "Camera stopped delivering frames — restarting it…";
            AppLog.Warn($"Camera stalled, restarting: {cam.Name}");
            await _camera.StopAsync();
            await Task.Delay(500);
            var format = await _camera.StartAsync(cam);
            StatusText.Text = $"Camera recovered: {cam.Name} — {format}";
            AppLog.Info($"Camera recovered: {cam.Name} — {format}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Camera restart failed ({ex.Message}) — will retry if it stays stalled.";
            AppLog.Error("Camera restart failed", ex);
        }
        finally
        {
            _cameraRecovering = false;
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        StopComposeLoop();
        _vcamOn = false;
        _vcam.Dispose();
        _vcamChannel.Dispose();
        await _camera.StopAsync();
        _display.Dispose();
        _compositor.Dispose();
        await ShutdownAudioMeterAsync();
        base.OnClosed(e);
    }
}
