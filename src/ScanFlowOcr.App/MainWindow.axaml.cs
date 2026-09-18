using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ScanFlowOcr.App.Configuration;
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
    private readonly FlashCapProvider _cameraProvider = new();
    private readonly PreviewBitmapRenderer _sessionPreviewRenderer = new();
    private readonly ObservableCollection<ScanResultItem> _allResults = [];
    private readonly ObservableCollection<ScanResultItem> _filteredResults = [];
    private readonly ObservableCollection<PlaylistThumbnailItem> _playlist = [];

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

    public MainWindow() : this(new SettingsManager()) { }

    public MainWindow(ISettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        _settings = settingsManager.Current.Clone();
        InitializeComponent();
        PreviewScaleRoot.RenderTransform = _previewZoom;
        _coordinator = new OutputCoordinator();
        try { _coordinator.Configure(_settings.GetOutputRoutes()); } catch { /* ignore bad saved routes */ }

        ResultsList.ItemsSource = _filteredResults;
        PlaylistItems.ItemsSource = _playlist;

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
        if (_activeSession is null)
        {
            TxtSessionMetrics.Text = "帧 0 / 丢 0";
            return;
        }
        var snap = _activeSession.GetSnapshot();
        TxtSessionMetrics.Text = $"帧 {snap.FramesReceived} / 丢 {snap.FramesDropped} / {snap.State}";
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }
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
        if (_busy || _activeSession is not null) return;
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
                        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif", "*.webp", "*.tif", "*.tiff"],
                        MimeTypes = ["image/*"]
                    },
                    FilePickerFileTypes.All
                ]
            }).ConfigureAwait(true);
            if (files.Count == 0) return;

            foreach (var item in _playlist)
                item.Thumbnail?.Dispose();
            _playlist.Clear();

            int index = 0;
            foreach (var file in files)
            {
                string? path = file.TryGetLocalPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
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
                    FileName = Path.GetFileName(path),
                    Thumbnail = thumb
                });
            }

            if (_playlist.Count > 0)
            {
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
        await ShowStillPreviewAsync(item.FilePath).ConfigureAwait(true);
    }

    private async Task ShowStillPreviewAsync(string path)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var bmp = new Bitmap(fs);
            PreviewImage.Source = bmp;
            PanelPlaceholder.IsVisible = false;
            _sourceWidth = bmp.PixelSize.Width;
            _sourceHeight = bmp.PixelSize.Height;
            UpdateOverlayGeometry();
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
            Append($"FlashCap: {_cameras.Length} MJPEG device(s).");
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
        int index = CameraDeviceCombo.SelectedIndex;
        if (index < 0 || index >= _cameras.Length) { CameraModeCombo.ItemsSource = null; return; }
        try
        {
            var device = _cameras[index];
            var modes = await _cameraProvider.GetModesAsync(device.Id, CancellationToken.None).ConfigureAwait(true);
            CameraModeCombo.ItemsSource = modes.Select(m =>
                $"{m.Width}×{m.Height} @ {m.FpsNumerator}/{Math.Max(1, m.FpsDenominator)}").ToList();
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
                UpdateOverlayGeometry();
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
                        UpdateOverlayGeometry();
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
        RunOcrButton.IsEnabled = enabled && _playlist.Count > 0 && !scanning;
        ModelCombo.IsEnabled = enabled && !scanning;
        SettingsButton.IsEnabled = enabled && !scanning;
        StartScanButton.IsEnabled = enabled && !scanning && !previewing;
        StopScanButton.IsEnabled = enabled && scanning;
        StartPreviewButton.IsEnabled = enabled && !scanning && !previewing;
        StopPreviewButton.IsEnabled = enabled && previewing;
        OcrFrameButton.IsEnabled = enabled && previewing;
        CameraDeviceCombo.IsEnabled = enabled && !scanning && !previewing;
        CameraModeCombo.IsEnabled = enabled && !scanning && !previewing;
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
        RoiRect.IsVisible = _settings.EnableRoi;
        UpdateOverlayGeometry();
    }

    private void UpdateOverlayGeometry()
    {
        if (ViewportHost is null || OverlayCanvas is null) return;
        double w = ViewportHost.Bounds.Width;
        double h = ViewportHost.Bounds.Height;
        if (w <= 1 || h <= 1) return;

        OverlayCanvas.Width = w;
        OverlayCanvas.Height = h;
        CrosshairH.StartPoint = new Avalonia.Point(w * 0.5 - 16, h * 0.5);
        CrosshairH.EndPoint = new Avalonia.Point(w * 0.5 + 16, h * 0.5);
        CrosshairV.StartPoint = new Avalonia.Point(w * 0.5, h * 0.5 - 16);
        CrosshairV.EndPoint = new Avalonia.Point(w * 0.5, h * 0.5 + 16);

        if (_settings.EnableRoi)
        {
            Canvas.SetLeft(RoiRect, w * (_settings.RoiX / 100.0));
            Canvas.SetTop(RoiRect, h * (_settings.RoiY / 100.0));
            RoiRect.Width = Math.Max(1, w * (_settings.RoiWidth / 100.0));
            RoiRect.Height = Math.Max(1, h * (_settings.RoiHeight / 100.0));
        }
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
