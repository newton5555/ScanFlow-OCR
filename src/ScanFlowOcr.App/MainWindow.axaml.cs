using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ScanFlowOcr.App.Configuration;
using ScanFlowOcr.App.Capture;
using ScanFlowOcr.App.Models;
using ScanFlowOcr.App.Rendering;
using ScanFlowOcr.Capture.FlashCap;
using ScanFlowOcr.Contracts;
using ScanFlowOcr.Imaging;
using ScanFlowOcr.Ocr.SimdPaddle;
using ScanFlowOcr.Outputs;
using ScanFlowOcr.Runtime;

namespace ScanFlowOcr.App;

public partial class MainWindow : Window, IAsyncDisposable
{
    private const double MinZoom = 0.2;
    private const double MaxZoom = 10.0;

    private readonly ISettingsManager _settingsManager;
    private readonly OutputCoordinator _coordinator;
    private readonly SimdPaddleOcrFactory _ocrFactory = new();
    private readonly ImagePlaylistCameraProvider _cameraProvider = new(new FlashCapProvider());
    private readonly PreviewBitmapRenderer _sessionPreviewRenderer = new();
    private readonly ObservableCollection<ScanResultItem> _allResults = [];
    private readonly ObservableCollection<ScanResultItem> _filteredResults = [];
    private readonly ObservableCollection<PlaylistThumbnailItem> _playlist = [];
    private Bitmap? _stillPreviewBitmap;

    private AppSettings _settings;
    private ImmutableArray<CameraDescriptor> _cameras = [];
    private CameraPreviewController? _preview;
    private ScanSession? _activeSession;
    private CancellationTokenSource? _sessionCts;
    private Task? _eventsTask;
    private Task? _previewTask;
    private Task? _stoppingTask;
    private IImageLease? _pendingLease;
    private int _previewUpdateScheduled;
    private IOcrReader? _ocrReader;
    private string? _ocrReaderModel;
    private ImmutableArray<OcrLine> _lastLines = [];
    private FrameStamp? _lastStamp;
    private bool _busy;
    private bool _disposed;
    private double _zoomFactor = 1.0;
    private readonly ScaleTransform _previewZoom = new(1, 1);
    private int _sourceWidth;
    private int _sourceHeight;
    private DispatcherTimer? _metricsTimer;
    private string _metricSource = "";
    private long _metricLastCount;
    private long _metricLastTicks;
    private double _metricFps;
    private int _modeRequestRevision;

    private enum RoiDragMode { None, Create, Move, NorthWest, NorthEast, SouthWest, SouthEast }

    private bool _isEditingRoi;
    private bool _roiBusy;
    private RoiDragMode _roiDragMode;
    private Rect _roiDraft;
    private Rect _roiDragOrigin;
    private Point _roiDragStart;

    public MainWindow() : this(new SettingsManager()) { }

    public MainWindow(ISettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        _settings = settingsManager.Current.Clone();
        InitializeComponent();
        RestoreWindowLayout();
        PreviewScaleRoot.RenderTransform = _previewZoom;
        _coordinator = new OutputCoordinator();
        try { _coordinator.Configure(_settings.GetOutputRoutes()); } catch { /* ignore bad saved routes */ }

        ResultsList.ItemsSource = _filteredResults;
        PlaylistItems.ItemsSource = _playlist;
        _cameraProvider.ActiveImageChanged += OnActiveImageChanged;

        SyncToolbarFromSettings();
        UpdateZoomLevelText();
        UpdateGuidesVisibility();
        LayoutUpdated += (_, _) => UpdateOverlayGeometry();

        Append("ScanFlow-OCR — continuous OCR session + still images + MJPEG preview.");
        Append($"Settings: {_settingsManager.ActiveFilePath}");
        if (TurboJpegNative.TryResolveLibraryPath(out string? tj, out string tjErr))
            Append($"TurboJPEG OK: {tj}");
        else
            Append($"TurboJPEG missing (camera needs it): {tjErr}");

        Closed += OnClosed;
        _ = RefreshCamerasAsync();
        StartMetricsTimer();
    }

    private void StartMetricsTimer()
    {
        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _metricsTimer.Tick += (_, _) => RefreshSessionMetrics();
        _metricsTimer.Start();
    }

    private void RefreshSessionMetrics()
    {
        RefreshOutputQueueStatus();
        if (_activeSession is not null)
        {
            var snap = _activeSession.GetSnapshot();
            double fps = UpdateMetricRate("session", snap.FramesReceived);
            TxtSessionMetrics.Text = $"帧 {snap.FramesReceived} · {fps:0.0} FPS / 丢 {snap.FramesDropped}";
            TxtPreviewMetrics.Text = _sourceWidth > 0
                ? $"{_sourceWidth}×{_sourceHeight} · {fps:0.0} FPS"
                : "会话预览中";
            return;
        }

        if (_preview is { IsRunning: true } preview)
        {
            var metrics = preview.GetMetrics();
            double fps = UpdateMetricRate("preview", metrics.FramesDecoded);
            TxtSessionMetrics.Text = $"预览 {metrics.FramesDecoded}/{metrics.FramesReceived} · {fps:0.0} FPS / 丢 {metrics.FramesDropped}";
            TxtPreviewMetrics.Text = _sourceWidth > 0
                ? $"{_sourceWidth}×{_sourceHeight} · {fps:0.0} FPS"
                : "预览中";
            return;
        }

        UpdateMetricRate("idle", 0);
        TxtSessionMetrics.Text = "帧 0 · 0.0 FPS / 丢 0";
        TxtPreviewMetrics.Text = PreviewImage.Source is not null && _sourceWidth > 0
            ? $"静态图 {_sourceWidth}×{_sourceHeight}"
            : "未启用";
    }

    private double UpdateMetricRate(string source, long count)
    {
        long now = Stopwatch.GetTimestamp();
        if (!string.Equals(_metricSource, source, StringComparison.Ordinal))
        {
            _metricSource = source;
            _metricLastCount = count;
            _metricLastTicks = now;
            _metricFps = 0;
            return 0;
        }

        long previousCount = _metricLastCount;
        long previousTicks = _metricLastTicks;
        _metricLastCount = count;
        _metricLastTicks = now;
        double seconds = (now - previousTicks) / (double)Stopwatch.Frequency;
        long delta = count - previousCount;
        if (seconds > 0 && delta >= 0)
        {
            double instant = delta / seconds;
            _metricFps = _metricFps <= 0 ? instant : (_metricFps * 0.65) + (instant * 0.35);
        }
        return _metricFps;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        try { await SaveWindowLayoutAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }
        try { await DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }
    }

    private void RestoreWindowLayout()
    {
        if (double.IsFinite(_settings.WindowWidth) && _settings.WindowWidth >= MinWidth)
            Width = Math.Clamp(_settings.WindowWidth, MinWidth, 4096);
        if (double.IsFinite(_settings.WindowHeight) && _settings.WindowHeight >= MinHeight)
            Height = Math.Clamp(_settings.WindowHeight, MinHeight, 4096);
        if (_settings.WindowLeft is int left && _settings.WindowTop is int top)
            Position = new PixelPoint(left, top);
        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private async Task SaveWindowLayoutAsync()
    {
        var next = _settings.Clone();
        if (WindowState == WindowState.Maximized)
        {
            next.WindowMaximized = true;
        }
        else
        {
            next.WindowMaximized = false;
            if (Bounds.Width >= MinWidth) next.WindowWidth = Bounds.Width;
            if (Bounds.Height >= MinHeight) next.WindowHeight = Bounds.Height;
            next.WindowLeft = Position.X;
            next.WindowTop = Position.Y;
        }

        if (next.WindowWidth == _settings.WindowWidth &&
            next.WindowHeight == _settings.WindowHeight &&
            next.WindowLeft == _settings.WindowLeft &&
            next.WindowTop == _settings.WindowTop &&
            next.WindowMaximized == _settings.WindowMaximized)
            return;

        await _settingsManager.SaveAsync(next).ConfigureAwait(false);
        _settings = next;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        Closed -= OnClosed;
        _metricsTimer?.Stop();
        if (_activeSession is not null)
        {
            try { await StopScanningInternalAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
        if (_preview is not null)
        {
            await _preview.DisposeAsync().ConfigureAwait(false);
            _preview = null;
        }
        if (_ocrReader is not null)
        {
            await _ocrReader.DisposeAsync().ConfigureAwait(false);
            _ocrReader = null;
        }
        _sessionPreviewRenderer.Dispose();
        _stillPreviewBitmap?.Dispose();
        foreach (var item in _playlist) item.Thumbnail?.Dispose();
        _cameraProvider.ActiveImageChanged -= OnActiveImageChanged;
        await _coordinator.DisposeAsync().ConfigureAwait(false);
        _settingsManager.Dispose();
    }

    private void SyncToolbarFromSettings()
    {
        string model = _settings.GetOcrModel();
        for (int i = 0; i < ModelCombo.ItemCount; i++)
        {
            if (ModelCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), model, StringComparison.OrdinalIgnoreCase))
            {
                ModelCombo.SelectedIndex = i;
                break;
            }
        }
        SinkNone.IsChecked = !_settings.MqttEnabled && !_settings.TcpEnabled && !_settings.KeyboardEnabled;
        SinkKeyboard.IsChecked = _settings.KeyboardEnabled;
        SinkMqtt.IsChecked = _settings.MqttEnabled;
        SinkTcp.IsChecked = _settings.TcpEnabled;
        KeyboardTarget.Text = _settings.KeyboardTargetProcess;
        MqttBroker.Text = _settings.MqttBroker;
        MqttPort.Text = _settings.MqttPort.ToString(CultureInfo.InvariantCulture);
        MqttTopic.Text = _settings.MqttTopic;
        TcpHost.Text = _settings.TcpHost;
        TcpPort.Text = _settings.TcpPort.ToString(CultureInfo.InvariantCulture);
        TxtDedupeHud.Text = $"去重:{_settings.DedupeMode}";
        UpdateGuidesVisibility();
        UpdateRoiOverlay();
    }

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        if (_activeSession is not null)
        {
            Append("扫描运行中已锁定设置，请先停止。");
            return;
        }
        var win = new SettingsWindow(_settings, _settingsManager.ActiveFilePath);
        var result = await win.ShowDialog<AppSettings?>(this).ConfigureAwait(true);
        if (result is null) return;
        await _settingsManager.SaveAsync(result).ConfigureAwait(true);
        _settings = result.Clone();
        try { _coordinator.Configure(_settings.GetOutputRoutes()); } catch (Exception ex) { Append($"输出配置: {ex.Message}"); }
        SyncToolbarFromSettings();
        Append("设置已保存。");
        SetStatus("设置已更新");
    }

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        var app = Application.Current;
        if (app is null) return;
        bool toDark = app.RequestedThemeVariant != ThemeVariant.Dark;
        app.RequestedThemeVariant = toDark ? ThemeVariant.Dark : ThemeVariant.Light;
        ThemeToggleGlyph.Text = toDark ? "☾" : "☀";
    }

    private void OnClearResults(object? sender, RoutedEventArgs e)
    {
        _allResults.Clear();
        ApplyResultFilter();
        _lastLines = [];
        _lastStamp = null;
        SendOutputButton.IsEnabled = false;
        LatestResultToast.IsVisible = false;
        TxtInspectorText.Text = "选择一条结果查看详情";
        TxtInspectorMeta.Text = "";
        Log.Text = "";
        SetStatus("已清空结果");
    }

    private async void OnClearDedupe(object? sender, RoutedEventArgs e)
    {
        if (_activeSession is null) { Append("无活动会话。"); return; }
        await _activeSession.ClearHistoryAsync(CancellationToken.None).ConfigureAwait(true);
        Append("已重置去重历史。");
    }

    private async void OnOpenImages(object? sender, RoutedEventArgs e)
    {
        if (_busy || _activeSession is not null || _preview is not null) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 OCR 图片",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Images")
                    {
                        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.tif", "*.tiff"],
                        MimeTypes = ["image/*"]
                    },
                    FilePickerFileTypes.All
                ]
            }).ConfigureAwait(true);
            if (files.Count == 0) return;
            await ImportImagesAsync(files.Select(static file => file.TryGetLocalPath())
                .Where(static path => !string.IsNullOrWhiteSpace(path))!
                .Cast<string>()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Append($"Open failed: {ex.Message}");
        }
    }

    private async void OnOpenImageFolder(object? sender, RoutedEventArgs e)
    {
        if (_busy || _activeSession is not null || _preview is not null) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择图片文件夹",
                AllowMultiple = false
            }).ConfigureAwait(true);
            string? folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (string.IsNullOrWhiteSpace(folder)) return;
            string[] extensions = [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"];
            var paths = Directory.EnumerateFiles(folder)
                .Where(path => extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase);
            await ImportImagesAsync(paths).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Append($"导入文件夹失败: {ex.Message}");
        }
    }

    private async Task ImportImagesAsync(IEnumerable<string> paths)
    {
        try
        {
            string[] files = paths.Where(File.Exists).ToArray();
            if (files.Length == 0) throw new InvalidOperationException("没有找到可用图片。");
            CameraDescriptor imported = await _cameraProvider.ImportPlaylistAsync(files, CancellationToken.None)
                .ConfigureAwait(true);
            foreach (var item in _playlist)
                item.Thumbnail?.Dispose();
            _playlist.Clear();

            int index = 0;
            foreach (var file in files)
            {
                string path = file;
                Bitmap? thumb = null;
                try
                {
                    await using var fs = File.OpenRead(path);
                    thumb = Bitmap.DecodeToWidth(fs, 120);
                }
                catch { /* no thumb */ }
                _playlist.Add(new PlaylistThumbnailItem
                {
                    Index = index++,
                    FilePath = path,
                    FileName = System.IO.Path.GetFileName(path),
                    Thumbnail = thumb
                });
            }

            if (_playlist.Count > 0)
            {
                await RefreshCamerasAsync().ConfigureAwait(true);
                int deviceIndex = -1;
                for (int i = 0; i < _cameras.Length; i++)
                    if (_cameras[i].Id == imported.Id) { deviceIndex = i; break; }
                if (deviceIndex >= 0) CameraDeviceCombo.SelectedIndex = deviceIndex;
                foreach (var p in _playlist) p.IsActive = false;
                _playlist[0].IsActive = true;
                await ShowStillPreviewAsync(_playlist[0].FilePath).ConfigureAwait(true);
            }
            RunOcrButton.IsEnabled = _playlist.Count > 0;
            TxtImageCount.Text = $"{_playlist.Count} 张";
            SetStatus($"已加载 {_playlist.Count} 张图片");
            Append($"Opened {_playlist.Count} file(s).");
        }
        catch (Exception ex)
        {
            Append($"Open failed: {ex.Message}");
        }
    }

    private async void OnPlaylistItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not PlaylistThumbnailItem item) return;
        foreach (var p in _playlist) p.IsActive = false;
        item.IsActive = true;
        _cameraProvider.SetStartIndex(item.Index);
        if (_activeSession is null && _preview is null)
            await ShowStillPreviewAsync(item.FilePath).ConfigureAwait(true);
        if (e.ClickCount >= 2)
            OnRunOcr(sender, new RoutedEventArgs());
    }

    private void OnActiveImageChanged(int index, string path)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in _playlist)
                item.IsActive = item.Index == index;
        }, DispatcherPriority.Background);
    }

    private async Task ShowStillPreviewAsync(string path)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var bmp = new Bitmap(fs);
            var old = _stillPreviewBitmap;
            _stillPreviewBitmap = bmp;
            PreviewImage.Source = bmp;
            old?.Dispose();
            PanelPlaceholder.IsVisible = false;
            _sourceWidth = bmp.PixelSize.Width;
            _sourceHeight = bmp.PixelSize.Height;
            UpdatePreviewSurfaceGeometry();
        }
        catch (Exception ex)
        {
            Append($"Preview still failed: {ex.Message}");
        }
    }

    private async void OnRunOcr(object? sender, RoutedEventArgs e)
    {
        if (_busy || _playlist.Count == 0) return;
        var active = _playlist.FirstOrDefault(p => p.IsActive) ?? _playlist[0];
        string model = GetSelectedModel();
        await RunExclusiveAsync(async () =>
        {
            SetStatus($"OCR ({model})…");
            StillImageBgra bgra = await Task.Run(() => StillImages.DecodeBgra(active.FilePath)).ConfigureAwait(true);
            byte[] bgr = StillImages.CopyToBgr(bgra.Pixels, bgra.Width, bgra.Height);
            await RunOcrOnBgrAsync(bgr, bgra.Width, bgra.Height, model, "still:" + active.FileName).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async void OnRefreshCameras(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunExclusiveAsync(() => RefreshCamerasAsync()).ConfigureAwait(true);
    }

    private async Task RefreshCamerasAsync()
    {
        try
        {
            SetCameraError("");
            _cameras = await _cameraProvider.EnumerateAsync(CancellationToken.None).ConfigureAwait(true);
            CameraDeviceCombo.ItemsSource = _cameras.Select(c => c.DisplayName).ToList();
            int prefer = Math.Clamp(_settings.PreferredCameraIndex, 0, Math.Max(0, _cameras.Length - 1));
            if (_cameras.Length > 0) CameraDeviceCombo.SelectedIndex = prefer;
            else CameraModeCombo.ItemsSource = null;
            Append($"输入源: {_cameras.Length} 个（相机 / 图片）。");
            SetStatus($"Cameras: {_cameras.Length}");
        }
        catch (Exception ex)
        {
            Append($"Enumerate failed: {ex.Message}");
            SetCameraError(ex.Message);
        }
    }

    private async void OnCameraDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        int revision = ++_modeRequestRevision;
        int index = CameraDeviceCombo.SelectedIndex;
        if (index < 0 || index >= _cameras.Length) { CameraModeCombo.ItemsSource = null; return; }
        try
        {
            var device = _cameras[index];
            var modes = await _cameraProvider.GetModesAsync(device.Id, CancellationToken.None).ConfigureAwait(true);
            if (revision != _modeRequestRevision || CameraDeviceCombo.SelectedIndex != index) return;
            CameraModeCombo.ItemsSource = modes.Select(m =>
                ImagePlaylistCameraProvider.IsImageDevice(device.Id)
                    ? $"{m.Width}×{m.Height} · {m.FpsDenominator} ms/张"
                    : $"{m.Width}×{m.Height} @ {m.FpsNumerator}/{Math.Max(1, m.FpsDenominator)} · {(m.Encoding == FrameEncoding.Jpeg ? "MJPEG" : "RGB24/32")}").ToList();
            CameraModeCombo.Tag = modes;
            int prefer = Math.Clamp(_settings.PreferredModeIndex, 0, Math.Max(0, modes.Length - 1));
            if (modes.Length > 0) CameraModeCombo.SelectedIndex = prefer;
        }
        catch (Exception ex)
        {
            Append($"GetModes failed: {ex.Message}");
        }
    }

    private async void OnStartScan(object? sender, RoutedEventArgs e)
    {
        if (_busy || _activeSession is not null) return;
        await RunExclusiveAsync(StartScanningAsync).ConfigureAwait(true);
    }

    private async void OnStopScan(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunExclusiveAsync(StopScanningAsync).ConfigureAwait(true);
    }

    private async Task StartScanningAsync()
    {
        if (!TurboJpegNative.TryResolveLibraryPath(out string? jpegPath, out string tjError))
        {
            SetCameraError(tjError);
            Append(tjError);
            return;
        }

        if (_preview is not null)
        {
            await _preview.DisposeAsync().ConfigureAwait(true);
            _preview = null;
        }

        int di = CameraDeviceCombo.SelectedIndex;
        int mi = CameraModeCombo.SelectedIndex;
        if (di < 0 || di >= _cameras.Length ||
            CameraModeCombo.Tag is not ImmutableArray<CaptureMode> modes || mi < 0 || mi >= modes.Length)
        {
            SetCameraError("请先选择相机与模式。");
            return;
        }

        // Persist toolbar model into settings for session profile
        _settings.SetOcrModel(GetSelectedModel());
        SessionProfile profile;
        try { profile = _settings.ToSessionProfile(_cameras[di].Id, modes[mi]); }
        catch (Exception ex)
        {
            Append($"配置生成失败: {ex.Message}");
            return;
        }

        try { _coordinator.Configure(profile.Outputs); }
        catch (Exception ex) { Append($"输出: {ex.Message}"); }

        ScanSession session;
        try
        {
            session = new ScanSession(_cameraProvider, _ocrFactory, jpegPath!, profile, _coordinator);
        }
        catch (Exception ex)
        {
            Append($"创建会话失败: {ex.Message}");
            return;
        }

        _activeSession = session;
        _sessionCts = new CancellationTokenSource();
        var token = _sessionCts.Token;
        _eventsTask = ConsumeEventsAsync(session, token);
        _previewTask = ConsumePreviewAsync(session, token);

        try
        {
            await session.StartAsync(token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Append($"启动扫描失败: {ex.Message}");
            await StopScanningInternalAsync().ConfigureAwait(true);
            return;
        }

        StartScanButton.IsEnabled = false;
        StopScanButton.IsEnabled = true;
        StartPreviewButton.IsEnabled = false;
        StopPreviewButton.IsEnabled = false;
        OcrFrameButton.IsEnabled = false;
        SettingsButton.IsEnabled = false;
        CameraDeviceCombo.IsEnabled = false;
        CameraModeCombo.IsEnabled = false;
        SetPreviewChrome(true, scanning: true);
        TxtDedupeHud.Text = $"去重:{_settings.DedupeMode}";
        SetStatus("正在扫描");
        Append($"连续扫描已启动: {_cameras[di].DisplayName} {modes[mi].Width}×{modes[mi].Height}");
    }

    private async Task StopScanningAsync() => await StopScanningInternalAsync().ConfigureAwait(true);

    private async Task StopScanningInternalAsync()
    {
        if (_stoppingTask is not null)
        {
            await _stoppingTask.ConfigureAwait(true);
            return;
        }
        _stoppingTask = StopScanningCoreAsync();
        try { await _stoppingTask.ConfigureAwait(true); }
        finally { _stoppingTask = null; }
    }

    private async Task StopScanningCoreAsync()
    {
        var session = _activeSession;
        _activeSession = null;
        if (session is null) return;

        try { await session.StopAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { Append($"停止异常: {ex.Message}"); }

        _sessionCts?.Cancel();
        try
        {
            if (_eventsTask is not null && _previewTask is not null)
                await Task.WhenAll(_eventsTask, _previewTask).ConfigureAwait(true);
        }
        catch { /* cancel */ }

        try { await session.DisposeAsync().ConfigureAwait(true); } catch { /* ignore */ }
        _sessionCts?.Dispose();
        _sessionCts = null;
        _eventsTask = null;
        _previewTask = null;

        var leftover = Interlocked.Exchange(ref _pendingLease, null);
        leftover?.Dispose();
        Interlocked.Exchange(ref _previewUpdateScheduled, 0);

        StartScanButton.IsEnabled = true;
        StopScanButton.IsEnabled = false;
        StartPreviewButton.IsEnabled = true;
        SettingsButton.IsEnabled = true;
        CameraDeviceCombo.IsEnabled = true;
        CameraModeCombo.IsEnabled = true;
        SetPreviewChrome(false, scanning: false);
        SetStatus("已停止");
        Append("连续扫描已停止。");
    }

    private async Task ConsumePreviewAsync(ScanSession session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var lease in session.ReadPreviewAsync(cancellationToken).ConfigureAwait(false))
            {
                var old = Interlocked.Exchange(ref _pendingLease, lease);
                old?.Dispose();
                if (Interlocked.CompareExchange(ref _previewUpdateScheduled, 1, 0) == 0)
                    Dispatcher.UIThread.Post(() => ProcessPendingPreview(session), DispatcherPriority.Render);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Append($"预览异常: {ex.Message}"));
        }
        finally
        {
            if (ReferenceEquals(_activeSession, session))
            {
                Interlocked.Exchange(ref _pendingLease, null)?.Dispose();
            }
        }
    }

    private void ProcessPendingPreview(ScanSession session)
    {
        Interlocked.Exchange(ref _previewUpdateScheduled, 0);
        if (!ReferenceEquals(_activeSession, session)) return;
        var lease = Interlocked.Exchange(ref _pendingLease, null);
        if (lease is null) return;
        try
        {
            if (!ReferenceEquals(_activeSession, session)) return;
            var input = lease.Input;
            _sourceWidth = input.Stamp.SourceWidth > 0 ? input.Stamp.SourceWidth : input.Layout.Width;
            _sourceHeight = input.Stamp.SourceHeight > 0 ? input.Stamp.SourceHeight : input.Layout.Height;
            if (_sessionPreviewRenderer.TryRender(lease, out var bmp) && bmp is not null)
            {
                PreviewImage.Source = bmp;
                PanelPlaceholder.IsVisible = false;
                UpdatePreviewSurfaceGeometry();
            }
        }
        finally { lease.Dispose(); }
    }

    private async Task ConsumeEventsAsync(ScanSession session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in session.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_activeSession, session))
                        HandleSessionEvent(evt);
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Append($"事件流异常: {ex.Message}"));
        }
    }

    private void HandleSessionEvent(SessionEvent evt)
    {
        switch (evt)
        {
            case ScanRecordReady ready:
                AddRecord(ready.Record);
                break;
            case StateChanged state:
                SetStatus(state.State.ToString());
                if (state.State == SessionState.Faulted)
                    Append($"会话故障: {state.Reason}");
                break;
            case DedupeOverflowWarning warn:
                Append($"去重警告: {warn.Message}");
                break;
            case OutputAdmissionWarning outWarn:
                Append($"输出拒绝: {outWarn.Message}");
                break;
            case EventsSkipped skipped:
                Append($"事件丢弃: {skipped.Count}");
                break;
        }
    }

    private void AddRecord(ScanRecord record)
    {
        _lastLines = record.TextLines;
        _lastStamp = record.Frame;
        SendOutputButton.IsEnabled = !record.TextLines.IsDefaultOrEmpty;
        string time = DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        foreach (var line in record.TextLines)
        {
            string conf = line.Confidence is double c
                ? (c * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"
                : "-";
            _allResults.Insert(0, new ScanResultItem
            {
                Index = _allResults.Count + 1,
                Time = time,
                Text = line.Text,
                Confidence = conf,
                EventId = record.EventId,
                Bounds = line.Bounds,
                SourceId = record.Frame.SourceId
            });
        }
        if (!record.TextLines.IsDefaultOrEmpty)
        {
            string previewText = string.Join(" · ", record.TextLines.Take(2).Select(static line => line.Text));
            if (previewText.Length > 140) previewText = previewText[..140] + "…";
            TxtLatestResult.Text = previewText;
            TxtLatestResultMeta.Text = $"{time} · {record.TextLines.Length} 行";
            LatestResultToast.IsVisible = true;
        }
        while (_allResults.Count > 500)
            _allResults.RemoveAt(_allResults.Count - 1);
        ApplyResultFilter();
        Append($"OCR 接纳 {record.TextLines.Length} 行 EventId={record.EventId:N}");
    }

    private void OnResultSearchChanged(object? sender, TextChangedEventArgs e) => ApplyResultFilter();

    private void ApplyResultFilter()
    {
        string q = ResultSearchBox.Text?.Trim() ?? "";
        _filteredResults.Clear();
        IEnumerable<ScanResultItem> src = string.IsNullOrEmpty(q)
            ? _allResults
            : _allResults.Where(r => r.SearchBlob.Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var item in src.Take(200))
            _filteredResults.Add(item);
        TxtResultCount.Text = $"{_filteredResults.Count}/{_allResults.Count} 行";
    }

    private void OnResultSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ScanResultItem item)
        {
            TxtInspectorText.Text = "选择一条结果查看详情";
            TxtInspectorMeta.Text = "";
            return;
        }
        TxtInspectorText.Text = item.Text;
        TxtInspectorMeta.Text = $"{item.Time}  {item.ConfidenceDisplay}\n{item.BoundsSummary}\nEventId={item.EventId:N}\nSource={item.SourceId}";
    }

    private async void OnCopySelectedResult(object? sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ScanResultItem item || string.IsNullOrEmpty(item.Text)) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) throw new InvalidOperationException("剪贴板不可用。");
            await clipboard.SetTextAsync(item.Text).ConfigureAwait(true);
            SetStatus("已复制 OCR 文本");
        }
        catch (Exception ex) { Append($"复制失败: {ex.Message}"); }
    }

    private void RefreshOutputQueueStatus()
    {
        var status = _coordinator.GetStatus();
        string sink = status.Routes.FirstOrDefault(static route => route.Enabled)?.SinkId switch
        {
            "mqtt" => "MQTT",
            "tcp" => "TCP",
            "keyboard" => "键盘",
            _ => "输出未启用"
        };
        TxtOutputQueueStatus.Text = status.Enabled
            ? $"{sink} · 待发 {status.PendingCount} · 待核对 {status.UncertainCount}"
            : sink;
        BtnClearOutputQueue.IsEnabled = status.PendingCount + status.UncertainCount > 0;
    }

    private async void OnClearOutputQueue(object? sender, RoutedEventArgs e)
    {
        var status = _coordinator.GetStatus();
        int total = status.PendingCount + status.UncertainCount;
        if (total == 0) return;

        var dialog = new Window
        {
            Title = "确认清空发送队列",
            Width = 390,
            Height = 150,
            MinWidth = 390,
            MinHeight = 150,
            MaxWidth = 390,
            MaxHeight = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = $"要丢弃 {total} 条待发送或待核对记录吗？此操作无法撤销。", TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "取消", IsCancel = true },
                            new Button { Content = "清空", IsDefault = true, Tag = "confirm" }
                        }
                    }
                }
            }
        };
        var buttons = ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children;
        ((Button)buttons[0]).Click += (_, _) => dialog.Close(false);
        ((Button)buttons[1]).Click += (_, _) => dialog.Close(true);
        if (!await dialog.ShowDialog<bool>(this).ConfigureAwait(true)) return;
        try
        {
            int cleared = _coordinator.ClearPending();
            RefreshOutputQueueStatus();
            SetStatus($"已清空 {cleared} 条发送记录");
        }
        catch (Exception ex) { Append($"清空队列失败: {ex.Message}"); }
    }

    private async void OnStartPreview(object? sender, RoutedEventArgs e)
    {
        if (_busy || _activeSession is not null) return;
        await RunExclusiveAsync(async () =>
        {
            if (!TurboJpegNative.TryResolveLibraryPath(out _, out string tjError))
            {
                SetCameraError(tjError);
                return;
            }
            int di = CameraDeviceCombo.SelectedIndex;
            int mi = CameraModeCombo.SelectedIndex;
            if (di < 0 || di >= _cameras.Length ||
                CameraModeCombo.Tag is not ImmutableArray<CaptureMode> modes || mi < 0 || mi >= modes.Length)
            {
                SetCameraError("Select camera + mode.");
                return;
            }
            if (_preview is not null)
            {
                await _preview.DisposeAsync().ConfigureAwait(true);
                _preview = null;
            }
            var session = await _cameraProvider
                .OpenAsync(new CameraOpenOptions(_cameras[di].Id, modes[mi].ModeId), CancellationToken.None)
                .ConfigureAwait(true);
            _preview = new CameraPreviewController(
                Append, SetCameraError,
                bmp =>
                {
                    PreviewImage.Source = bmp;
                    if (bmp is not null)
                    {
                        PanelPlaceholder.IsVisible = false;
                        _sourceWidth = bmp.PixelSize.Width;
                        _sourceHeight = bmp.PixelSize.Height;
                        UpdatePreviewSurfaceGeometry();
                    }
                },
                () =>
                {
                    StartPreviewButton.IsEnabled = _activeSession is null;
                    StopPreviewButton.IsEnabled = false;
                    OcrFrameButton.IsEnabled = false;
                });
            try
            {
                await _preview.StartAsync(session, CancellationToken.None).ConfigureAwait(true);
                StartPreviewButton.IsEnabled = false;
                StopPreviewButton.IsEnabled = true;
                OcrFrameButton.IsEnabled = true;
                StartScanButton.IsEnabled = false;
                SetPreviewChrome(true, scanning: false);
                SetStatus("Preview running.");
            }
            catch
            {
                await _preview.DisposeAsync().ConfigureAwait(true);
                _preview = null;
                throw;
            }
        }).ConfigureAwait(true);
    }

    private async void OnStopPreview(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunExclusiveAsync(async () =>
        {
            if (_preview is null) return;
            await _preview.StopAsync().ConfigureAwait(true);
            await _preview.DisposeAsync().ConfigureAwait(true);
            _preview = null;
            PreviewImage.Source = null;
            ClearPreviewSurfaceGeometry();
            StartPreviewButton.IsEnabled = true;
            StopPreviewButton.IsEnabled = false;
            OcrFrameButton.IsEnabled = false;
            StartScanButton.IsEnabled = true;
            SetPreviewChrome(false, scanning: false);
            SetStatus("Preview stopped.");
        }).ConfigureAwait(true);
    }

    private async void OnOcrCurrentFrame(object? sender, RoutedEventArgs e)
    {
        if (_busy || _preview is null) return;
        if (!_preview.TryGetLatestBgra(out byte[] bgra, out int w, out int h, out FrameStamp stamp))
        {
            Append("No decoded frame yet.");
            return;
        }
        string model = GetSelectedModel();
        await RunExclusiveAsync(async () =>
        {
            byte[] bgr = await Task.Run(() =>
            {
                if (_settings.EnableRoi)
                {
                    int x = (int)Math.Clamp(Math.Round(w * (_settings.RoiX / 100.0)), 0, Math.Max(0, w - 1));
                    int y = (int)Math.Clamp(Math.Round(h * (_settings.RoiY / 100.0)), 0, Math.Max(0, h - 1));
                    int rw = (int)Math.Clamp(Math.Round(w * (_settings.RoiWidth / 100.0)), 1, Math.Max(1, w - x));
                    int rh = (int)Math.Clamp(Math.Round(h * (_settings.RoiHeight / 100.0)), 1, Math.Max(1, h - y));
                    byte[] cropped = new byte[rw * rh * 4];
                    for (int row = 0; row < rh; row++)
                        Buffer.BlockCopy(bgra, ((y + row) * w + x) * 4, cropped, row * rw * 4, rw * 4);
                    return StillImages.CopyToBgr(cropped, rw, rh);
                }
                return StillImages.CopyToBgr(bgra, w, h);
            }).ConfigureAwait(true);

            int ow = _settings.EnableRoi
                ? (int)Math.Clamp(Math.Round(w * (_settings.RoiWidth / 100.0)), 1, w)
                : w;
            int oh = _settings.EnableRoi
                ? (int)Math.Clamp(Math.Round(h * (_settings.RoiHeight / 100.0)), 1, h)
                : h;
            // Recompute exact crop size from same formula as above
            if (_settings.EnableRoi)
            {
                int x = (int)Math.Clamp(Math.Round(w * (_settings.RoiX / 100.0)), 0, Math.Max(0, w - 1));
                int y = (int)Math.Clamp(Math.Round(h * (_settings.RoiY / 100.0)), 0, Math.Max(0, h - 1));
                ow = (int)Math.Clamp(Math.Round(w * (_settings.RoiWidth / 100.0)), 1, Math.Max(1, w - x));
                oh = (int)Math.Clamp(Math.Round(h * (_settings.RoiHeight / 100.0)), 1, Math.Max(1, h - y));
            }
            await RunOcrOnBgrAsync(bgr, ow, oh, model, stamp.SourceId).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RunOcrOnBgrAsync(byte[] bgr, int width, int height, string model, string sourceId)
    {
        IOcrReader reader = await EnsureOcrReaderAsync(model).ConfigureAwait(true);
        var stamp = new FrameStamp(new FrameId(Guid.NewGuid(), 1), sourceId, width, height, Stopwatch.GetTimestamp(), null);
        var layout = new ImageLayout(FrameEncoding.Raw, PixelFormat.Bgr24, width, height,
            [new PlaneLayout(0, width * 3, width * 3, height)], ColorRange.Full, ColorMatrix.Unspecified);
        var input = new ImageInput(stamp, layout, bgr, ImageTransform.Identity);
        var request = new RecognitionRequest(new AnalysisId(stamp.Id, 1), Stopwatch.GetTimestamp() + Stopwatch.Frequency * 120);
        EngineBatch<OcrLine> batch = await reader.ReadAsync(input, request, CancellationToken.None).ConfigureAwait(true);
        _lastStamp = stamp;
        _lastLines = batch.Items;
        SendOutputButton.IsEnabled = batch.Items.Length > 0;
        if (batch.Status == StageStatus.Faulted)
        {
            Append($"OCR faulted: {batch.Fault?.Code} {batch.Fault?.Message}");
            SetStatus("OCR faulted.");
            return;
        }
        var record = new ScanRecord(Guid.NewGuid(), new AnalysisId(stamp.Id, 1), stamp, DateTimeOffset.UtcNow,
            batch.Items, ImmutableDictionary<string, string>.Empty);
        AddRecord(record);
        SetStatus($"OCR {batch.Status}: {batch.Items.Length} line(s) in {batch.EngineTime.TotalMilliseconds:0} ms.");
    }

    private void OnApplyOutput(object? sender, RoutedEventArgs e)
    {
        try
        {
            _settings.KeyboardEnabled = SinkKeyboard.IsChecked == true;
            _settings.MqttEnabled = SinkMqtt.IsChecked == true;
            _settings.TcpEnabled = SinkTcp.IsChecked == true;
            _settings.KeyboardTargetProcess = KeyboardTarget.Text?.Trim() ?? "notepad.exe";
            _settings.MqttBroker = MqttBroker.Text?.Trim() ?? "localhost";
            _settings.MqttPort = int.TryParse(MqttPort.Text, out int mp) ? mp : 1883;
            _settings.MqttTopic = MqttTopic.Text?.Trim() ?? "scanflow-ocr/scans";
            _settings.TcpHost = TcpHost.Text?.Trim() ?? "127.0.0.1";
            _settings.TcpPort = int.TryParse(TcpPort.Text, out int tp) ? tp : 9100;
            _coordinator.Configure(_settings.GetOutputRoutes());
            Append("输出路由已应用（未自动落盘；请用设置窗口保存）。");
            SetStatus("Output applied");
        }
        catch (Exception ex)
        {
            Append($"Apply output failed: {ex.Message}");
        }
    }

    private async void OnSendOutput(object? sender, RoutedEventArgs e)
    {
        if (_busy || _lastLines.IsDefaultOrEmpty || _lastStamp is null) return;
        await RunExclusiveAsync(async () =>
        {
            var stamp = _lastStamp.Value;
            var record = new ScanRecord(Guid.NewGuid(), new AnalysisId(stamp.Id, 1), stamp, DateTimeOffset.UtcNow,
                _lastLines, ImmutableDictionary<string, string>.Empty);
            bool accepted = await _coordinator.TryAcceptAsync(record, CancellationToken.None).ConfigureAwait(true);
            Append(accepted ? $"Queued EventId={record.EventId:N}" : $"Admission rejected: {_coordinator.GetStatus().LastError}");
        }).ConfigureAwait(true);
    }

    private string GetSelectedModel()
    {
        if (ModelCombo.SelectedItem is ComboBoxItem item && item.Content is string s) return s;
        return _settings.GetOcrModel();
    }

    private async Task<IOcrReader> EnsureOcrReaderAsync(string model)
    {
        if (_ocrReader is not null && string.Equals(_ocrReaderModel, model, StringComparison.OrdinalIgnoreCase))
            return _ocrReader;
        if (_ocrReader is not null)
        {
            await _ocrReader.DisposeAsync().ConfigureAwait(true);
            _ocrReader = null;
        }
        using var doc = JsonDocument.Parse($"{{\"model\":\"{model}\"}}");
        var settings = new OcrSettings([], OcrLayout.TextBlock, doc.RootElement.Clone());
        _ocrReader = await _ocrFactory.CreateAsync(settings, CancellationToken.None).ConfigureAwait(true);
        _ocrReaderModel = model;
        Append($"OCR reader ready: {_ocrReader.Descriptor.ProviderId}");
        return _ocrReader;
    }

    private async Task RunExclusiveAsync(Func<Task> action)
    {
        _busy = true;
        SetBusyUi(false);
        try { await action().ConfigureAwait(true); }
        catch (Exception ex)
        {
            Append($"Error: {ex.GetType().Name}: {ex.Message}");
            SetStatus("Error.");
        }
        finally
        {
            _busy = false;
            SetBusyUi(true);
        }
    }

    private void SetBusyUi(bool enabled)
    {
        bool scanning = _activeSession is not null;
        bool previewing = _preview?.IsRunning ?? false;
        bool roiEditing = _isEditingRoi;
        RunOcrButton.IsEnabled = enabled && _playlist.Count > 0 && !scanning && !roiEditing;
        ModelCombo.IsEnabled = enabled && !scanning && !roiEditing;
        SettingsButton.IsEnabled = enabled && !scanning && !roiEditing;
        StartScanButton.IsEnabled = enabled && !scanning && !previewing && !roiEditing;
        StopScanButton.IsEnabled = enabled && scanning && !roiEditing;
        StartPreviewButton.IsEnabled = enabled && !scanning && !previewing && !roiEditing;
        StopPreviewButton.IsEnabled = enabled && previewing && !roiEditing;
        OcrFrameButton.IsEnabled = enabled && previewing && !roiEditing;
        CameraDeviceCombo.IsEnabled = enabled && !scanning && !previewing && !roiEditing;
        CameraModeCombo.IsEnabled = enabled && !scanning && !previewing && !roiEditing;
        RefreshCamerasButton.IsEnabled = enabled && !scanning && !previewing && !roiEditing;
        BtnEditRoi.IsEnabled = enabled && !_roiBusy && !_isEditingRoi && !scanning && _sourceWidth > 0 && _sourceHeight > 0;
        SendOutputButton.IsEnabled = enabled && !_lastLines.IsDefaultOrEmpty;
    }

    private void SetStatus(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetStatus(text));
            return;
        }
        StatusText.Text = text;
        TxtSessionState.Text = text;
        TxtHeaderSession.Text = text;
        UpdateStatusPill(text);
    }

    private void UpdateStatusPill(string text)
    {
        bool fault = text.Contains("错误", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("fault", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed", StringComparison.OrdinalIgnoreCase);
        bool active = text.Contains("扫描", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("running", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("preview", StringComparison.OrdinalIgnoreCase);
        bool warning = text.Contains("警告", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("拒绝", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("暂停", StringComparison.OrdinalIgnoreCase);

        string background = fault || warning ? "SurfaceSubtleBackgroundBrush" :
            active ? "BrandSoftBrush" : "SurfaceSubtleBackgroundBrush";
        string border = fault ? "DangerBrush" : warning ? "WarningBrush" :
            active ? "BrandBrush" : "BorderSubtleBrush";
        string foreground = fault ? "DangerBrush" : warning ? "WarningBrush" :
            active ? "BrandBrush" : "TextPrimaryBrush";

        PillScanStatus.Background = ResolveBrush(background, Brushes.Transparent);
        PillScanStatus.BorderBrush = ResolveBrush(border, Brushes.Transparent);
        DotState.Fill = ResolveBrush(border, Brushes.Gray);
        TxtSessionState.Foreground = ResolveBrush(foreground, Brushes.White);
        DotHeaderStatus.Fill = ResolveBrush(border, Brushes.Gray);
    }

    private void SetPreviewChrome(bool running, bool scanning)
    {
        PanelPlaceholder.IsVisible = !running && PreviewImage.Source is null;
        var success = ResolveBrush("SuccessBrush", Brushes.LimeGreen);
        var muted = ResolveBrush("TextMutedBrush", Brushes.Gray);
        DotHudLive.Fill = running ? success : muted;
        DotState.Fill = running ? success : muted;
        TxtHudTitle.Text = scanning ? "连续扫描" : running ? "实时画面" : "取景预览";
        TxtPreviewMetrics.Text = running ? (scanning ? "会话预览中" : "预览中") : "未启用";
        UpdateRoiOverlay();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Zoom(1.25);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Zoom(1.0 / 1.25);
    private void OnZoomReset(object? sender, RoutedEventArgs e) => ResetZoom();
    private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        Zoom(e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15);
        e.Handled = true;
    }
    private void OnViewportDoubleTap(object? sender, TappedEventArgs e) => ResetZoom();

    private void Zoom(double factor)
    {
        _zoomFactor = Math.Clamp(_zoomFactor * factor, MinZoom, MaxZoom);
        _previewZoom.ScaleX = _zoomFactor;
        _previewZoom.ScaleY = _zoomFactor;
        UpdateZoomLevelText();
    }

    private void ResetZoom()
    {
        _zoomFactor = 1.0;
        _previewZoom.ScaleX = 1;
        _previewZoom.ScaleY = 1;
        UpdateZoomLevelText();
    }

    private void UpdateZoomLevelText() =>
        TxtZoomLevel.Text = $"{(_zoomFactor * 100):0}%";

    private void OnToggleGuides(object? sender, RoutedEventArgs e)
    {
        _settings.ShowPreviewGuides = !_settings.ShowPreviewGuides;
        UpdateGuidesVisibility();
    }

    private void UpdateGuidesVisibility()
    {
        bool show = _settings.ShowPreviewGuides;
        CrosshairH.IsVisible = show;
        CrosshairV.IsVisible = show;
    }

    private void UpdateRoiOverlay()
    {
        RoiRect.IsVisible = _isEditingRoi || _settings.EnableRoi;
        UpdateOverlayGeometry();
    }

    private void UpdatePreviewSurfaceGeometry()
    {
        if (_sourceWidth <= 0 || _sourceHeight <= 0) return;

        PreviewScaleRoot.Width = _sourceWidth;
        PreviewScaleRoot.Height = _sourceHeight;
        PreviewImage.Width = _sourceWidth;
        PreviewImage.Height = _sourceHeight;
        OverlayCanvas.Width = _sourceWidth;
        OverlayCanvas.Height = _sourceHeight;
        RoiEditorVisualCanvas.Width = _sourceWidth;
        RoiEditorVisualCanvas.Height = _sourceHeight;
        UpdateOverlayGeometry();
        SetBusyUi(!_busy);
    }

    private void ClearPreviewSurfaceGeometry()
    {
        _sourceWidth = 0;
        _sourceHeight = 0;
        PreviewScaleRoot.Width = 0;
        PreviewScaleRoot.Height = 0;
        PreviewImage.Width = 0;
        PreviewImage.Height = 0;
        OverlayCanvas.Width = 0;
        OverlayCanvas.Height = 0;
        RoiEditorVisualCanvas.Children.Clear();
        RoiRect.IsVisible = false;
        SetBusyUi(!_busy);
    }

    private void UpdateOverlayGeometry()
    {
        if (_sourceWidth <= 0 || _sourceHeight <= 0) return;

        double w = _sourceWidth;
        double h = _sourceHeight;
        CrosshairH.StartPoint = new Avalonia.Point(0, h * 0.5);
        CrosshairH.EndPoint = new Avalonia.Point(w, h * 0.5);
        CrosshairV.StartPoint = new Avalonia.Point(w * 0.5, 0);
        CrosshairV.EndPoint = new Avalonia.Point(w * 0.5, h);

        Rect roi = _isEditingRoi
            ? _roiDraft
            : new Rect(_settings.RoiX / 100.0, _settings.RoiY / 100.0,
                _settings.RoiWidth / 100.0, _settings.RoiHeight / 100.0);
        roi = ClampRoi(roi);
        Canvas.SetLeft(RoiRect, w * roi.Left);
        Canvas.SetTop(RoiRect, h * roi.Top);
        RoiRect.Width = Math.Max(1, w * roi.Width);
        RoiRect.Height = Math.Max(1, h * roi.Height);
        UpdateRoiEditorVisuals(roi, w, h);
    }

    private void UpdateRoiEditorVisuals(Rect roi, double imageWidth, double imageHeight)
    {
        RoiEditorVisualCanvas.Children.Clear();
        if (!_isEditingRoi) return;

        double left = roi.Left * imageWidth;
        double top = roi.Top * imageHeight;
        double right = roi.Right * imageWidth;
        double bottom = roi.Bottom * imageHeight;
        IBrush shade = new SolidColorBrush(Color.FromArgb(112, 0, 0, 0));
        IBrush handleBrush = ResolveBrush("BrandBrush", Brushes.DeepSkyBlue);

        void AddShade(double x, double y, double width, double height)
        {
            if (width <= 0 || height <= 0) return;
            var shape = new Rectangle
            {
                Width = width,
                Height = height,
                Fill = shade,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(shape, x);
            Canvas.SetTop(shape, y);
            RoiEditorVisualCanvas.Children.Add(shape);
        }

        AddShade(0, 0, imageWidth, top);
        AddShade(0, top, left, bottom - top);
        AddShade(right, top, imageWidth - right, bottom - top);
        AddShade(0, bottom, imageWidth, imageHeight - bottom);

        double handleSize = Math.Clamp(Math.Min(imageWidth, imageHeight) * 0.012, 8, 18);
        foreach (var corner in new[]
        {
            new Point(left, top), new Point(right, top),
            new Point(left, bottom), new Point(right, bottom)
        })
        {
            var handle = new Border
            {
                Width = handleSize,
                Height = handleSize,
                Background = Brushes.White,
                BorderBrush = handleBrush,
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(handle, corner.X - handleSize / 2);
            Canvas.SetTop(handle, corner.Y - handleSize / 2);
            RoiEditorVisualCanvas.Children.Add(handle);
        }
    }

    private void OnBeginRoiEdit(object? sender, RoutedEventArgs e)
    {
        if (_isEditingRoi || _roiBusy) return;
        if (_activeSession is not null)
        {
            Append("连续扫描运行中，请先停止扫描再编辑 ROI。");
            return;
        }
        if (PreviewImage.Source is null || _sourceWidth <= 0 || _sourceHeight <= 0)
        {
            Append("请先启动预览或显示一张图片，再编辑 ROI。");
            return;
        }

        _roiDraft = ClampRoi(new Rect(
            _settings.RoiX / 100.0,
            _settings.RoiY / 100.0,
            _settings.RoiWidth / 100.0,
            _settings.RoiHeight / 100.0));
        _roiDragMode = RoiDragMode.None;
        _isEditingRoi = true;
        RoiEditorBar.IsVisible = true;
        BtnEditRoi.IsVisible = false;
        OverlayCanvas.IsHitTestVisible = true;
        UpdateRoiDraftText();
        UpdateRoiOverlay();
        SetBusyUi(true);
        SetStatus("编辑 ROI");
        Append("ROI 编辑已开启：拖动空白处框选，拖动区域移动，四角调整。");
    }

    private async void OnSaveRoi(object? sender, RoutedEventArgs e)
    {
        if (!_isEditingRoi || _roiBusy || !IsRoiDraftValid()) return;
        _roiBusy = true;
        BtnRoiSave.IsEnabled = false;
        try
        {
            var settings = _settings.Clone();
            settings.EnableRoi = true;
            settings.RoiX = _roiDraft.Left * 100;
            settings.RoiY = _roiDraft.Top * 100;
            settings.RoiWidth = _roiDraft.Width * 100;
            settings.RoiHeight = _roiDraft.Height * 100;
            if (!settings.Validate(out string? error))
            {
                Append(error ?? "ROI 配置无效。");
                return;
            }

            await _settingsManager.SaveAsync(settings).ConfigureAwait(true);
            _settings = settings.Clone();
            try { _coordinator.Configure(_settings.GetOutputRoutes()); } catch { /* preserve saved ROI */ }
            EndRoiEdit();
            Append("ROI 已保存；下次启动扫描时生效。");
            SetStatus("ROI 已保存");
        }
        catch (Exception ex)
        {
            Append($"ROI 保存失败: {ex.Message}");
        }
        finally
        {
            _roiBusy = false;
            if (_isEditingRoi)
            {
                UpdateRoiDraftText();
                BtnRoiSave.IsEnabled = IsRoiDraftValid();
            }
        }
    }

    private void OnCancelRoi(object? sender, RoutedEventArgs e)
    {
        if (!_roiBusy) EndRoiEdit();
    }

    private void OnFullRoi(object? sender, RoutedEventArgs e)
    {
        if (!_isEditingRoi || _roiBusy) return;
        _roiDraft = new Rect(0, 0, 1, 1);
        UpdateRoiDraftText();
        UpdateOverlayGeometry();
    }

    private void EndRoiEdit()
    {
        if (!_isEditingRoi) return;
        _roiDragMode = RoiDragMode.None;
        _roiBusy = false;
        OverlayCanvas.IsHitTestVisible = false;
        RoiEditorBar.IsVisible = false;
        BtnEditRoi.IsVisible = true;
        _isEditingRoi = false;
        UpdateRoiOverlay();
        SetBusyUi(true);
    }

    private bool IsRoiDraftValid() =>
        _sourceWidth > 0 && _sourceHeight > 0 &&
        _roiDraft.Width >= 1.0 / _sourceWidth &&
        _roiDraft.Height >= 1.0 / _sourceHeight;

    private void UpdateRoiDraftText()
    {
        TxtRoiDraftCoordinates.Text = string.Create(CultureInfo.InvariantCulture,
            $"X1 {_roiDraft.Left * 100:0.0}%   Y1 {_roiDraft.Top * 100:0.0}%   X2 {_roiDraft.Right * 100:0.0}%   Y2 {_roiDraft.Bottom * 100:0.0}%");
        BtnRoiSave.IsEnabled = IsRoiDraftValid() && !_roiBusy;
    }

    private bool TryGetNormalizedPointer(PointerEventArgs e, out Point point)
    {
        point = default;
        if (_sourceWidth <= 0 || _sourceHeight <= 0) return false;
        Point local = e.GetPosition(OverlayCanvas);
        point = new Point(
            Math.Clamp(local.X / _sourceWidth, 0, 1),
            Math.Clamp(local.Y / _sourceHeight, 0, 1));
        return true;
    }

    private void OnRoiPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_isEditingRoi || _roiBusy ||
            e.GetCurrentPoint(OverlayCanvas).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed ||
            !TryGetNormalizedPointer(e, out Point point)) return;

        double hitX = Math.Clamp(14.0 / Math.Max(1, _sourceWidth), 0.004, 0.08);
        double hitY = Math.Clamp(14.0 / Math.Max(1, _sourceHeight), 0.004, 0.08);
        bool Near(double x, double y) => Math.Abs(point.X - x) <= hitX && Math.Abs(point.Y - y) <= hitY;

        _roiDragStart = point;
        _roiDragOrigin = _roiDraft;
        _roiDragMode = Near(_roiDraft.Left, _roiDraft.Top) ? RoiDragMode.NorthWest :
            Near(_roiDraft.Right, _roiDraft.Top) ? RoiDragMode.NorthEast :
            Near(_roiDraft.Left, _roiDraft.Bottom) ? RoiDragMode.SouthWest :
            Near(_roiDraft.Right, _roiDraft.Bottom) ? RoiDragMode.SouthEast :
            _roiDraft.Contains(point) ? RoiDragMode.Move : RoiDragMode.Create;
        e.Pointer.Capture(OverlayCanvas);
        e.Handled = true;
    }

    private void OnRoiPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isEditingRoi || _roiBusy || _roiDragMode == RoiDragMode.None ||
            !TryGetNormalizedPointer(e, out Point point)) return;

        Rect roi = _roiDragOrigin;
        double minW = 1.0 / Math.Max(1, _sourceWidth);
        double minH = 1.0 / Math.Max(1, _sourceHeight);
        switch (_roiDragMode)
        {
            case RoiDragMode.Create:
                roi = new Rect(new Point(Math.Min(_roiDragStart.X, point.X), Math.Min(_roiDragStart.Y, point.Y)),
                    new Point(Math.Max(_roiDragStart.X, point.X), Math.Max(_roiDragStart.Y, point.Y)));
                break;
            case RoiDragMode.Move:
                roi = new Rect(
                    Math.Clamp(_roiDragOrigin.X + point.X - _roiDragStart.X, 0, 1 - _roiDragOrigin.Width),
                    Math.Clamp(_roiDragOrigin.Y + point.Y - _roiDragStart.Y, 0, 1 - _roiDragOrigin.Height),
                    _roiDragOrigin.Width, _roiDragOrigin.Height);
                break;
            case RoiDragMode.NorthWest:
                double west = Math.Clamp(point.X, 0, _roiDragOrigin.Right - minW);
                double north = Math.Clamp(point.Y, 0, _roiDragOrigin.Bottom - minH);
                roi = new Rect(west, north, _roiDragOrigin.Right - west, _roiDragOrigin.Bottom - north);
                break;
            case RoiDragMode.NorthEast:
                double eastY = Math.Clamp(point.Y, 0, _roiDragOrigin.Bottom - minH);
                double eastX = Math.Clamp(point.X, _roiDragOrigin.Left + minW, 1);
                roi = new Rect(_roiDragOrigin.Left, eastY, eastX - _roiDragOrigin.Left, _roiDragOrigin.Bottom - eastY);
                break;
            case RoiDragMode.SouthWest:
                double southWestX = Math.Clamp(point.X, 0, _roiDragOrigin.Right - minW);
                double southWestY = Math.Clamp(point.Y, _roiDragOrigin.Top + minH, 1);
                roi = new Rect(southWestX, _roiDragOrigin.Top,
                    _roiDragOrigin.Right - southWestX, southWestY - _roiDragOrigin.Top);
                break;
            case RoiDragMode.SouthEast:
                double southEastX = Math.Clamp(point.X, _roiDragOrigin.Left + minW, 1);
                double southEastY = Math.Clamp(point.Y, _roiDragOrigin.Top + minH, 1);
                roi = new Rect(_roiDragOrigin.Left, _roiDragOrigin.Top,
                    southEastX - _roiDragOrigin.Left, southEastY - _roiDragOrigin.Top);
                break;
        }

        _roiDraft = ClampRoi(roi);
        UpdateRoiDraftText();
        UpdateOverlayGeometry();
        e.Handled = true;
    }

    private void OnRoiPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isEditingRoi || _roiDragMode == RoiDragMode.None ||
            e.InitialPressMouseButton != MouseButton.Left) return;
        if (!IsRoiDraftValid()) _roiDraft = _roiDragOrigin;
        _roiDragMode = RoiDragMode.None;
        e.Pointer.Capture(null);
        UpdateRoiDraftText();
        UpdateOverlayGeometry();
        e.Handled = true;
    }

    private void OnRoiPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isEditingRoi || _roiDragMode == RoiDragMode.None) return;
        _roiDragMode = RoiDragMode.None;
        if (!IsRoiDraftValid()) _roiDraft = _roiDragOrigin;
        UpdateRoiDraftText();
        UpdateOverlayGeometry();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isEditingRoi) return;
        if (e.Key == Key.Escape)
        {
            EndRoiEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && IsRoiDraftValid())
        {
            OnSaveRoi(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private static Rect ClampRoi(Rect roi)
    {
        double left = Math.Clamp(roi.Left, 0, 1);
        double top = Math.Clamp(roi.Top, 0, 1);
        double right = Math.Clamp(roi.Right, left, 1);
        double bottom = Math.Clamp(roi.Bottom, top, 1);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static IBrush ResolveBrush(string key, IBrush fallback)
    {
        var app = Application.Current;
        if (app is null) return fallback;
        if (app.TryGetResource(key, app.ActualThemeVariant, out object? value) && value is IBrush brush)
            return brush;
        return fallback;
    }

    private void SetCameraError(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetCameraError(text));
            return;
        }
        CameraErrorText.Text = text;
        CameraErrorText.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private void Append(string line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Append(line));
            return;
        }
        Log.Text = string.IsNullOrEmpty(Log.Text) ? line : Log.Text + Environment.NewLine + line;
        Log.CaretIndex = Log.Text?.Length ?? 0;
    }
}
