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
    private readonly SimdPaddleOcrFactory _ocrFactory;
    private readonly ImagePlaylistCameraProvider _cameraProvider;
    private readonly PreviewBitmapRenderer _sessionPreviewRenderer = new();
    private readonly ObservableCollection<ScanResultItem> _allResults = [];
    private readonly ObservableCollection<ScanResultItem> _filteredResults = [];
    private readonly ObservableCollection<PlaylistThumbnailItem> _playlist = [];
    private readonly ScaleTransform _viewportScale = new(1.0, 1.0);
    private readonly TranslateTransform _viewportTranslate = new(0, 0);
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
    private bool _suppressDeviceSelectionChanged;
    private bool _canClose;
    private bool _isClosing;
    private OcrSettings? _activeOcrSettings;
    private bool _disposed;
    private double _zoomFactor = 1.0;
    private int _sourceWidth;
    private int _sourceHeight;
    private DispatcherTimer? _metricsTimer;
    private string _metricSource = "";
    private long _metricLastCount;
    private long _metricLastTicks;
    private double _metricFps;
    private int _modeRequestRevision;
    private Point _dragStartPoint;
    private bool _isDraggingViewport;
    private object? _hoveredAnnotation;

    public MainWindow() : this(
        App.Services?.GetService(typeof(ISettingsManager)) as ISettingsManager ?? new SettingsManager(),
        App.Services?.GetService(typeof(ImagePlaylistCameraProvider)) as ImagePlaylistCameraProvider ?? new ImagePlaylistCameraProvider(new FlashCapProvider()),
        App.Services?.GetService(typeof(SimdPaddleOcrFactory)) as SimdPaddleOcrFactory ?? new SimdPaddleOcrFactory(),
        App.Services?.GetService(typeof(OutputCoordinator)) as OutputCoordinator ?? new OutputCoordinator())
    {
    }

    public MainWindow(ISettingsManager settingsManager) : this(
        settingsManager,
        App.Services?.GetService(typeof(ImagePlaylistCameraProvider)) as ImagePlaylistCameraProvider ?? new ImagePlaylistCameraProvider(new FlashCapProvider()),
        App.Services?.GetService(typeof(SimdPaddleOcrFactory)) as SimdPaddleOcrFactory ?? new SimdPaddleOcrFactory(),
        App.Services?.GetService(typeof(OutputCoordinator)) as OutputCoordinator ?? new OutputCoordinator())
    {
    }

    internal MainWindow(
        ISettingsManager settingsManager,
        ImagePlaylistCameraProvider cameraProvider,
        SimdPaddleOcrFactory ocrFactory,
        OutputCoordinator coordinator)
    {
        _settingsManager = settingsManager;
        _cameraProvider = cameraProvider;
        _ocrFactory = ocrFactory;
        _coordinator = coordinator;
        _settings = settingsManager.Current.Clone();
        InitializeComponent();
        RestoreWindowLayout();
        ViewportCanvasArea.RenderTransform = new TransformGroup
        {
            Children = [ _viewportScale, _viewportTranslate ]
        };
        try { _coordinator.Configure(_settings.GetOutputRoutes()); } catch { /* ignore bad saved routes */ }

        ResultsList.ItemsSource = _filteredResults;
        PlaylistItems.ItemsSource = _playlist;
        _cameraProvider.ActiveImageChanged += OnActiveImageChanged;

        SyncToolbarFromSettings();
        UpdateZoomLevelText();
        UpdateThemeIcon(Application.Current?.RequestedThemeVariant == ThemeVariant.Dark);
        UpdateCurrentSourceCard();

        Append("ScanFlow-OCR — continuous OCR session + still images + MJPEG preview.");
        Append($"Settings: {_settingsManager.ActiveFilePath}");
        if (TurboJpegNative.TryResolveLibraryPath(out string? tj, out string tjErr))
            Append($"TurboJPEG OK: {tj}");
        else
            Append($"TurboJPEG missing (camera needs it): {tjErr}");

        Closing += MainWindow_Closing;
        Closed += OnClosed;

        if (OperatingSystem.IsLinux())
        {
            WindowDecorations = Avalonia.Controls.WindowDecorations.Full;
            PanelCaptionButtons.IsVisible = false;
            CaptionDivider.IsVisible = false;
        }

        PropertyChanged += (s, e) =>
        {
            if (e.Property == WindowStateProperty && PathMaximizeIcon is not null && Application.Current is not null)
            {
                PathMaximizeIcon.Data = WindowState == WindowState.Maximized
                    ? (StreamGeometry)Application.Current.FindResource("GeoWindowRestore")!
                    : (StreamGeometry)Application.Current.FindResource("GeoWindowMaximize")!;
                ToolTip.SetTip(BtnMaximizeRestoreWindow, WindowState == WindowState.Maximized ? "向下还原" : "最大化");
            }
        };

        _ = RefreshCamerasAsync();
        StartMetricsTimer();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                BeginMoveDrag(e);
            }
        }
    }

    private void OnMinimizeWindowClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeRestoreWindowClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseWindowClick(object? sender, RoutedEventArgs e)
    {
        Close();
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

            if (TxtFpsHud is not null)
            {
                TxtFpsHud.Text = $"{fps:0.0} FPS";
                TxtFpsHud.IsVisible = true;
                TxtHudSeparator.IsVisible = true;
                TxtHudTitle.Text = "实时画面";
                DotHudLive.Fill = ResolveBrush("SuccessBrush", Brushes.LimeGreen);
            }
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

            if (TxtFpsHud is not null)
            {
                TxtFpsHud.Text = $"{fps:0.0} FPS";
                TxtFpsHud.IsVisible = true;
                TxtHudSeparator.IsVisible = true;
                TxtHudTitle.Text = "实时画面";
                DotHudLive.Fill = ResolveBrush("SuccessBrush", Brushes.LimeGreen);
            }
            return;
        }

        UpdateMetricRate("idle", 0);
        TxtSessionMetrics.Text = "帧 0 · 0.0 FPS / 丢 0";
        TxtPreviewMetrics.Text = PreviewImage.Source is not null && _sourceWidth > 0
            ? $"静态图 {_sourceWidth}×{_sourceHeight}"
            : "未启用";

        if (TxtFpsHud is not null)
        {
            TxtFpsHud.IsVisible = false;
            TxtHudSeparator.IsVisible = false;
            if (PreviewImage.Source is not null)
            {
                TxtHudTitle.Text = "图像预览";
                DotHudLive.Fill = ResolveBrush("BrandBrush", Brushes.DodgerBlue);
            }
            else
            {
                TxtHudTitle.Text = "取景就绪";
                DotHudLive.Fill = ResolveBrush("TextMutedBrush", Brushes.Gray);
            }
        }
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

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_canClose) return;

        var outputStatus = _coordinator.GetStatus();
        if (outputStatus is { PendingCount: > 0 })
        {
            e.Cancel = true;
            if (_isClosing) return;
            _isClosing = true;
            _ = PromptPendingOutputOnCloseAsync(outputStatus.PendingCount);
            return;
        }
    }

    private async Task PromptPendingOutputOnCloseAsync(int pendingCount)
    {
        var dialog = new Window
        {
            Title = "待发送记录",
            Width = 380,
            Height = 160,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        panel.Children.Add(new TextBlock
        {
            Text = $"仍有 {pendingCount} 条输出任务未完成。\n退出后任务保留，下次启动继续发送。是否退出？",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        });
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };
        var btnCancel = new Button { Content = "取消", Width = 70, Height = 28 };
        var btnOk = new Button { Content = "退出", Width = 70, Height = 28, Classes = { "danger" } };
        buttons.Children.Add(btnCancel);
        buttons.Children.Add(btnOk);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        btnCancel.Click += (_, _) => dialog.Close(false);
        btnOk.Click += (_, _) => dialog.Close(true);

        bool confirmed = await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        if (confirmed)
        {
            _canClose = true;
            if (_activeSession != null)
            {
                try { await StopScanningAsync().ConfigureAwait(true); }
                catch { }
            }
            Close();
        }
        else
        {
            _isClosing = false;
        }
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
        RedrawOverlay();
    }

    private async Task OpenSettingsDialogAsync(bool selectCameraTab = false, bool selectOutputTab = false)
    {
        if (_activeSession is not null)
        {
            Append("扫描运行中已锁定设置，请先停止。");
            return;
        }
        int currentDeviceIdx = CameraDeviceCombo.SelectedIndex;
        int currentModeIdx = CameraModeCombo.SelectedIndex;
        var win = new SettingsWindow(
            _settings,
            _settingsManager.ActiveFilePath,
            _coordinator,
            _cameras,
            _cameraProvider,
            currentDeviceIdx,
            currentModeIdx);

        if (selectCameraTab)
        {
            win.SelectCameraTab();
        }
        else if (selectOutputTab)
        {
            win.SelectOutputTab();
        }
        var result = await win.ShowDialog<AppSettings?>(this).ConfigureAwait(true);
        if (result is null) return;
        await _settingsManager.SaveAsync(result).ConfigureAwait(true);
        _settings = result.Clone();
        try { _coordinator.Configure(_settings.GetOutputRoutes()); } catch (Exception ex) { Append($"输出配置: {ex.Message}"); }
        SyncToolbarFromSettings();

        await SelectDeviceAndModeAsync(
            win.SelectedDevice,
            win.SelectedMode,
            _settings.PreferredCameraIndex,
            _settings.PreferredModeIndex).ConfigureAwait(true);

        Append($"设置已保存至 {System.IO.Path.GetFileName(_settingsManager.ActiveFilePath)}。");
        SetStatus("设置已更新");
    }

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        await OpenSettingsDialogAsync(false).ConfigureAwait(true);
    }

    private void OnQuickOcr(object? sender, RoutedEventArgs e)
    {
        if (_playlist.Count > 0 && _preview is null && _activeSession is null)
        {
            OnRunOcr(sender, e);
            return;
        }

        OnOcrCurrentFrame(sender, e);
    }

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        var app = Application.Current;
        if (app is null) return;
        bool toDark = app.RequestedThemeVariant != ThemeVariant.Dark;
        app.RequestedThemeVariant = toDark ? ThemeVariant.Dark : ThemeVariant.Light;
        UpdateThemeIcon(toDark);
    }

    private void UpdateThemeIcon(bool isDark)
    {
        if (this.TryFindResource(isDark ? "GeoMoon" : "GeoSun", out object? geo) && geo is Geometry g)
        {
            PathThemeIcon.Data = g;
        }
    }

    private void UpdateCurrentSourceCard()
    {
        int index = CameraDeviceCombo.SelectedIndex;
        if (index >= 0 && index < _cameras.Length)
        {
            var device = _cameras[index];
            TxtCurrentSourceName.Text = device.DisplayName;
            string? modeText = CameraModeCombo.SelectedItem?.ToString();
            TxtCurrentSourceMode.Text = !string.IsNullOrEmpty(modeText) ? modeText : "-";
        }
        else
        {
            TxtCurrentSourceName.Text = "未选择设备";
            TxtCurrentSourceMode.Text = "-";
        }
    }

    private async void OnCurrentSourceCardClick(object? sender, RoutedEventArgs e)
    {
        await OpenSettingsDialogAsync(true).ConfigureAwait(true);
    }

    private bool _isDrawerExpanded = true;
    private void OnToggleDrawer(object? sender, RoutedEventArgs e)
    {
        _isDrawerExpanded = !_isDrawerExpanded;
        DrawerBody.IsVisible = _isDrawerExpanded;
        if (this.TryFindResource(_isDrawerExpanded ? "GeoChevronDown" : "GeoChevronRight", out object? geo) && geo is Geometry g)
        {
            PathDrawerChevron.Data = g;
        }
    }

    private int _logLinesCount;
    private bool _isLogDrawerExpanded;

    private void OnToggleLogDrawer(object? sender, RoutedEventArgs e)
    {
        _isLogDrawerExpanded = !_isLogDrawerExpanded;
        LogDrawerBody.IsVisible = _isLogDrawerExpanded;
        if (this.TryFindResource(_isLogDrawerExpanded ? "GeoChevronDown" : "GeoChevronUp", out object? geo) && geo is Geometry g)
        {
            PathLogDrawerChevron.Data = g;
        }
    }

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not null && !string.IsNullOrEmpty(Log.Text))
        {
            await Clipboard.SetTextAsync(Log.Text);
            SetStatus("已复制运行日志");
        }
    }

    private void OnClearLog(object? sender, RoutedEventArgs e)
    {
        Log.Text = "";
        _logLinesCount = 0;
        TxtLogLineCount.Text = "0 行";
        SetStatus("已清空运行日志");
    }

    private async void OnCopyResultItem(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScanResultItem item } && Clipboard is not null && !string.IsNullOrEmpty(item.Text))
        {
            await Clipboard.SetTextAsync(item.Text);
            SetStatus($"已复制: {item.Text}");
        }
    }

    private void ShowBanner(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowBanner(text));
            return;
        }
        TxtBannerMessage.Text = text;
        BannerNotice.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private void OnDismissBanner(object? sender, RoutedEventArgs e)
    {
        BannerNotice.IsVisible = false;
    }

    private async void OnCopyBanner(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not null && !string.IsNullOrEmpty(TxtBannerMessage.Text))
        {
            await Clipboard.SetTextAsync(TxtBannerMessage.Text);
            SetStatus("已复制错误信息");
        }
    }

    private void OnToggleAdvancedToolbar(object? sender, RoutedEventArgs e)
    {
        AdvancedToolbar.IsVisible = !AdvancedToolbar.IsVisible;
        AdvancedToolbarButton.Content = AdvancedToolbar.IsVisible ? "收起控制⌃" : "更多控制⌄";
    }

    private void OnClearResults(object? sender, RoutedEventArgs e)
    {
        _allResults.Clear();
        ApplyResultFilter();
        _lastLines = [];
        _lastStamp = null;
        SendOutputButton.IsEnabled = false;
        ClearResultsButton.IsEnabled = false;
        ResultsList.SelectedItem = null;
        SyncFrameAnnotations();
        TxtInspectorText.Text = "选择一条结果查看详情";
        ClearInspectorMetadata();
        SelectedResultCopyButton.IsEnabled = false;
        Log.Text = "";
        _logLinesCount = 0;
        TxtLogLineCount.Text = "0 行";
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
                if (deviceIndex >= 0)
                {
                    await SelectDeviceAndModeAsync(_cameras[deviceIndex], null, deviceIndex, 0).ConfigureAwait(true);
                }
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

    private void OnClearPlaylist(object? sender, RoutedEventArgs e)
    {
        OnClearImportedAndRefresh(sender, e);
    }

    private void OnPlaylistScrollLeft(object? sender, RoutedEventArgs e)
    {
        if (DrawerBody is not null)
        {
            DrawerBody.Offset = new Avalonia.Vector(Math.Max(0, DrawerBody.Offset.X - 140), DrawerBody.Offset.Y);
        }
    }

    private void OnPlaylistScrollRight(object? sender, RoutedEventArgs e)
    {
        if (DrawerBody is not null)
        {
            DrawerBody.Offset = new Avalonia.Vector(DrawerBody.Offset.X + 140, DrawerBody.Offset.Y);
        }
    }

    private async void OnClearImportedAndRefresh(object? sender, RoutedEventArgs e)
    {
        if (_activeSession != null)
        {
            Append("请先停止当前扫描再刷新设备。");
            return;
        }

        _cameraProvider.ClearImportedImages();
        foreach (var item in _playlist)
            item.Thumbnail?.Dispose();
        _playlist.Clear();

        if (DrawerPlaylist is not null)
            DrawerPlaylist.IsVisible = false;

        TxtImageCount.Text = "0 张";
        _lastLines = [];
        SyncFrameAnnotations();
        PreviewImage.Source = null;
        _stillPreviewBitmap?.Dispose();
        _stillPreviewBitmap = null;
        PanelPlaceholder.IsVisible = true;

        SetHeaderSessionStatus("服务已就绪", active: false);
        SetPipelineStatus("取景就绪", active: false);
        SetStatus("已清空导入的图片，正在刷新硬件设备...");
        Append("已清空导入的图片并刷新输入源。");
        await RefreshCamerasAsync().ConfigureAwait(true);
    }

    private async void OnPlaylistItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not PlaylistThumbnailItem item) return;
        foreach (var p in _playlist) p.IsActive = false;
        item.IsActive = true;
        _cameraProvider.SetStartIndex(item.Index);
        if (_activeSession is null && _preview is null)
        {
            _lastLines = [];
            SyncFrameAnnotations();
            await ShowStillPreviewAsync(item.FilePath).ConfigureAwait(true);
        }
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
            SetPipelineStatus("单图识别中", active: true);
            SetStatus($"OCR ({model})…");
            StillImageBgra bgra = await Task.Run(() => StillImages.DecodeBgra(active.FilePath)).ConfigureAwait(true);
            int w = bgra.Width;
            int h = bgra.Height;
            int cropX = 0, cropY = 0;
            int ow = w, oh = h;
            byte[] bgr;

            if (_settings.EnableRoi)
            {
                cropX = (int)Math.Clamp(Math.Round(w * (_settings.RoiX / 100.0)), 0, Math.Max(0, w - 1));
                cropY = (int)Math.Clamp(Math.Round(h * (_settings.RoiY / 100.0)), 0, Math.Max(0, h - 1));
                ow = (int)Math.Clamp(Math.Round(w * (_settings.RoiWidth / 100.0)), 1, Math.Max(1, w - cropX));
                oh = (int)Math.Clamp(Math.Round(h * (_settings.RoiHeight / 100.0)), 1, Math.Max(1, h - cropY));
                byte[] cropped = new byte[ow * oh * 4];
                for (int row = 0; row < oh; row++)
                    Buffer.BlockCopy(bgra.Pixels, ((cropY + row) * w + cropX) * 4, cropped, row * ow * 4, ow * 4);
                bgr = StillImages.CopyToBgr(cropped, ow, oh);
            }
            else
            {
                bgr = StillImages.CopyToBgr(bgra.Pixels, w, h);
            }

            await RunOcrOnBgrAsync(bgr, ow, oh, model, "still:" + active.FileName, fullWidth: w, fullHeight: h, offsetX: cropX, offsetY: cropY).ConfigureAwait(true);
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
            _suppressDeviceSelectionChanged = true;
            try
            {
                CameraDeviceCombo.ItemsSource = _cameras.Select(c => c.DisplayName).ToList();
            }
            finally
            {
                _suppressDeviceSelectionChanged = false;
            }

            if (_cameras.Length > 0)
            {
                int prefer = Math.Clamp(_settings.PreferredCameraIndex, 0, _cameras.Length - 1);
                await SelectDeviceAndModeAsync(_cameras[prefer], null, prefer, _settings.PreferredModeIndex).ConfigureAwait(true);
            }
            else
            {
                CameraModeCombo.ItemsSource = null;
                UpdateCurrentSourceCard();
            }

            Append($"输入源: {_cameras.Length} 个（相机 / 图片）。");
            SetPipelineStatus(_cameras.Length == 0 ? "无输入源" : "待扫描", active: false);
            SetStatus(_cameras.Length == 0 ? "未找到输入源" : $"已就绪 · {_cameras.Length} 个输入源");
            UpdateToolbarActions();
        }
        catch (Exception ex)
        {
            Append($"Enumerate failed: {ex.Message}");
            SetCameraError(ex.Message);
        }
    }

    private async void OnCameraDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceSelectionChanged) return;
        int index = CameraDeviceCombo.SelectedIndex;
        if (index < 0 || index >= _cameras.Length)
        {
            CameraModeCombo.ItemsSource = null;
            UpdateToolbarActions();
            return;
        }
        await SelectDeviceAndModeAsync(_cameras[index], null, index, _settings.PreferredModeIndex).ConfigureAwait(true);
    }

    private void OnCameraModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceSelectionChanged) return;
        UpdateCurrentSourceCard();
        UpdateToolbarActions();
    }

    private async Task SelectDeviceAndModeAsync(
        CameraDescriptor? targetDevice,
        CaptureMode? targetMode,
        int preferredDeviceIndex = -1,
        int preferredModeIndex = -1)
    {
        if (_cameras.IsDefaultOrEmpty)
        {
            UpdateCurrentSourceCard();
            return;
        }

        int targetDeviceIndex = -1;
        if (targetDevice != null)
        {
            for (int i = 0; i < _cameras.Length; i++)
            {
                if (_cameras[i].Id == targetDevice.Id)
                {
                    targetDeviceIndex = i;
                    break;
                }
            }
        }

        if (targetDeviceIndex < 0 && preferredDeviceIndex >= 0 && preferredDeviceIndex < _cameras.Length)
        {
            targetDeviceIndex = preferredDeviceIndex;
        }

        if (targetDeviceIndex < 0)
        {
            targetDeviceIndex = 0;
        }

        int revision = ++_modeRequestRevision;
        _suppressDeviceSelectionChanged = true;
        try
        {
            CameraDeviceCombo.SelectedIndex = targetDeviceIndex;
        }
        finally
        {
            _suppressDeviceSelectionChanged = false;
        }

        var device = _cameras[targetDeviceIndex];

        bool isImage = ImagePlaylistCameraProvider.IsImageDevice(device.Id);
        if (DrawerPlaylist is not null)
        {
            DrawerPlaylist.IsVisible = isImage && _playlist.Count > 0;
        }

        if (!isImage && _activeSession is null)
        {
            PreviewImage.Source = null;
            _stillPreviewBitmap?.Dispose();
            _stillPreviewBitmap = null;
            PanelPlaceholder.IsVisible = true;
            _lastLines = [];
            SyncFrameAnnotations();
        }
        else if (isImage && _activeSession is null && _playlist.Count > 0)
        {
            var active = _playlist.FirstOrDefault(p => p.IsActive) ?? _playlist[0];
            await ShowStillPreviewAsync(active.FilePath).ConfigureAwait(true);
        }

        try
        {
            var modes = await _cameraProvider.GetModesAsync(device.Id, CancellationToken.None).ConfigureAwait(true);
            if (revision != _modeRequestRevision || CameraDeviceCombo.SelectedIndex != targetDeviceIndex) return;

            _suppressDeviceSelectionChanged = true;
            try
            {
                CameraModeCombo.ItemsSource = modes.Select(m =>
                    isImage
                        ? $"{m.Width}×{m.Height} · {m.FpsDenominator} ms/张"
                        : $"{m.Width}×{m.Height} @ {m.FpsNumerator}/{Math.Max(1, m.FpsDenominator)} · {(m.Encoding == FrameEncoding.Jpeg ? "MJPEG" : "RGB24/32")}").ToList();
                CameraModeCombo.Tag = modes;

                int targetModeIndex = -1;
                if (targetMode != null)
                {
                    for (int i = 0; i < modes.Length; i++)
                    {
                        if (modes[i].Width == targetMode.Width &&
                            modes[i].Height == targetMode.Height &&
                            modes[i].Encoding == targetMode.Encoding &&
                            modes[i].FpsNumerator == targetMode.FpsNumerator &&
                            modes[i].FpsDenominator == targetMode.FpsDenominator)
                        {
                            targetModeIndex = i;
                            break;
                        }
                    }
                }

                if (targetModeIndex < 0 && preferredModeIndex >= 0 && preferredModeIndex < modes.Length)
                {
                    targetModeIndex = preferredModeIndex;
                }

                if (targetModeIndex < 0)
                {
                    targetModeIndex = Math.Clamp(_settings.PreferredModeIndex, 0, Math.Max(0, modes.Length - 1));
                }

                if (modes.Length > 0)
                {
                    CameraModeCombo.SelectedIndex = targetModeIndex;
                }
            }
            finally
            {
                _suppressDeviceSelectionChanged = false;
            }

            SetPipelineStatus("待扫描", active: false);
            SetStatus($"已就绪 · {device.DisplayName} ({modes.Length} 种可用模式)");
        }
        catch (Exception ex)
        {
            Append($"获取工作模式失败: {ex.Message}");
            SetCameraError(ex.Message);
        }
        finally
        {
            UpdateCurrentSourceCard();
            UpdateToolbarActions();
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
        StartScanButton.IsVisible = false;
        StopScanButton.IsEnabled = true;
        StopScanButton.IsVisible = true;
        StartPreviewButton.IsEnabled = false;
        StopPreviewButton.IsEnabled = false;
        OcrFrameButton.IsEnabled = false;
        SettingsButton.IsEnabled = false;
        CameraDeviceCombo.IsEnabled = false;
        CameraModeCombo.IsEnabled = false;
        SetPreviewChrome(true, scanning: true);
        SetHeaderSessionStatus("连续会话运行中", active: true);
        SetPipelineStatus("连续扫描中", active: true);
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
        StartScanButton.IsVisible = true;
        StopScanButton.IsEnabled = false;
        StopScanButton.IsVisible = false;
        StartPreviewButton.IsEnabled = true;
        SettingsButton.IsEnabled = true;
        CameraDeviceCombo.IsEnabled = true;
        CameraModeCombo.IsEnabled = true;
        SetPreviewChrome(false, scanning: false);
        SetHeaderSessionStatus("服务已就绪", active: false);
        SetPipelineStatus("取景就绪", active: false);
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
                SetStatus(state.State switch
                {
                    SessionState.Created => "已创建",
                    SessionState.Starting => "启动中",
                    SessionState.Running => "正在扫描",
                    SessionState.Reconfiguring => "正在切换配置",
                    SessionState.Reconnecting => "正在重连相机",
                    SessionState.Stopping => "停止中",
                    SessionState.Stopped => "已停止",
                    SessionState.Faulted => "会话故障",
                    SessionState.Disposed => "已关闭",
                    _ => state.State.ToString()
                });
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
        ClearResultsButton.IsEnabled = _allResults.Count > 0;
        while (_allResults.Count > 500)
            _allResults.RemoveAt(_allResults.Count - 1);
        ApplyResultFilter();
        SyncFrameAnnotations();
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
        PanelEmptyResults.IsVisible = _filteredResults.Count == 0;
    }

    private void OnResultSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ScanResultItem item)
        {
            TxtInspectorText.Text = "选择一条结果查看详情";
            ClearInspectorMetadata();
            SelectedResultCopyButton.IsEnabled = false;
            return;
        }
        SelectedResultCopyButton.IsEnabled = !string.IsNullOrWhiteSpace(item.Text);
        TxtInspectorText.Text = item.Text;
        InspectorMetadataPanel.IsVisible = true;
        TxtInspectorTime.Text = item.Time;
        TxtInspectorConfidence.Text = item.Confidence;
        TxtInspectorBounds.Text = item.BoundsSummary;
        TxtInspectorSource.Text = item.SourceId;
        TxtInspectorEventId.Text = item.EventId.ToString("N");
    }

    private void ClearInspectorMetadata()
    {
        InspectorMetadataPanel.IsVisible = false;
        TxtInspectorTime.Text = "";
        TxtInspectorConfidence.Text = "";
        TxtInspectorBounds.Text = "";
        TxtInspectorSource.Text = "";
        TxtInspectorEventId.Text = "";
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

    private void RefreshOutputQueueStatus() => UpdateOutputStatusIndicator();

    private void UpdateOutputStatusIndicator()
    {
        if (TxtOutputSinkName is null) return;
        var status = _coordinator.GetStatus();
        if (status is null || !status.Enabled)
        {
            if (this.TryFindResource("TextMutedBrush", out object? mutedBrush) && mutedBrush is IBrush mb)
            {
                DotOutputStatus.Fill = mb;
                TxtOutputSinkName.Foreground = mb;
            }
            TxtOutputSinkName.Text = "输出未启用";
            TxtOutputPendingCount.Text = "";
            BtnClearOutputQueue.IsEnabled = false;
            BtnClearOutputQueue.IsVisible = false;
            ToolTip.SetTip(BtnOutputStatusPill, "外部数据推送未启用。点击打开【数据输出】设置");
            return;
        }

        string sinkName = "外部输出";
        if (status.Routes.Length > 0)
        {
            var r = status.Routes.FirstOrDefault(static route => route.Enabled) ?? status.Routes[0];
            sinkName = r.SinkId switch
            {
                "mqtt" => "MQTT",
                "tcp" => "TCP",
                "keyboard" => "键盘模拟",
                _ => r.SinkId
            };
        }

        int pending = status.PendingCount;
        int uncertain = status.UncertainCount;

        TxtOutputSinkName.Text = sinkName;
        if (this.TryFindResource("TextPrimaryBrush", out object? primBrush) && primBrush is IBrush pb)
            TxtOutputSinkName.Foreground = pb;

        if (pending > 0 || uncertain > 0)
        {
            if (this.TryFindResource("WarningBrush", out object? warnBrush) && warnBrush is IBrush wb)
            {
                DotOutputStatus.Fill = wb;
                TxtOutputPendingCount.Foreground = wb;
            }
            string countText = $"待发 {pending} 条";
            if (uncertain > 0) countText += $" (待核对 {uncertain})";
            TxtOutputPendingCount.Text = countText;
            BtnClearOutputQueue.IsEnabled = true;
            BtnClearOutputQueue.IsVisible = true;
        }
        else
        {
            if (this.TryFindResource("SuccessBrush", out object? succBrush) && succBrush is IBrush sb)
            {
                DotOutputStatus.Fill = sb;
            }
            if (this.TryFindResource("TextSecondaryBrush", out object? secBrush) && secBrush is IBrush scb)
            {
                TxtOutputPendingCount.Foreground = scb;
            }
            TxtOutputPendingCount.Text = "待发 0 条";
            BtnClearOutputQueue.IsEnabled = false;
            BtnClearOutputQueue.IsVisible = true;
        }

        ToolTip.SetTip(BtnOutputStatusPill, $"输出通道: {sinkName}\n待发送队列: {pending} 条\n待核对: {uncertain} 条\n已成功送达: {status.DeliveredCount} 条\n点击打开【数据输出】设置");
    }

    private async void OnOutputStatusPillClick(object? sender, RoutedEventArgs e)
    {
        await OpenSettingsDialogAsync(selectCameraTab: false, selectOutputTab: true).ConfigureAwait(true);
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
                SetPipelineStatus("预览中", active: true);
                SetStatus("实时预览中");
                UpdateToolbarActions();
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
            SetPipelineStatus("待扫描", active: false);
            SetStatus("预览已停止");
            UpdateToolbarActions();
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
            int cropX = 0, cropY = 0;
            if (_settings.EnableRoi)
            {
                cropX = (int)Math.Clamp(Math.Round(w * (_settings.RoiX / 100.0)), 0, Math.Max(0, w - 1));
                cropY = (int)Math.Clamp(Math.Round(h * (_settings.RoiY / 100.0)), 0, Math.Max(0, h - 1));
                ow = (int)Math.Clamp(Math.Round(w * (_settings.RoiWidth / 100.0)), 1, Math.Max(1, w - cropX));
                oh = (int)Math.Clamp(Math.Round(h * (_settings.RoiHeight / 100.0)), 1, Math.Max(1, h - cropY));
            }
            await RunOcrOnBgrAsync(bgr, ow, oh, model, stamp.SourceId, fullWidth: w, fullHeight: h, offsetX: cropX, offsetY: cropY).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RunOcrOnBgrAsync(byte[] bgr, int width, int height, string model, string sourceId,
        int fullWidth = 0, int fullHeight = 0, int offsetX = 0, int offsetY = 0)
    {
        IOcrReader reader = await EnsureOcrReaderAsync(model).ConfigureAwait(true);
        int finalW = fullWidth > 0 ? fullWidth : width;
        int finalH = fullHeight > 0 ? fullHeight : height;
        var stamp = new FrameStamp(new FrameId(Guid.NewGuid(), 1), sourceId, finalW, finalH, Stopwatch.GetTimestamp(), null);
        var layout = new ImageLayout(FrameEncoding.Raw, PixelFormat.Bgr24, width, height,
            [new PlaneLayout(0, width * 3, width * 3, height)], ColorRange.Full, ColorMatrix.Unspecified);
        var input = new ImageInput(stamp, layout, bgr, ImageTransform.Identity);
        long timeoutTicks = (long)(Math.Max(500, _settings.OcrTimeoutMs) * Stopwatch.Frequency / 1000.0);
        var request = new RecognitionRequest(new AnalysisId(stamp.Id, 1), Stopwatch.GetTimestamp() + timeoutTicks);
        EngineBatch<OcrLine> batch = await reader.ReadAsync(input, request, CancellationToken.None).ConfigureAwait(true);

        ImmutableArray<OcrLine> items = batch.Items;
        if ((offsetX > 0 || offsetY > 0) && !items.IsDefaultOrEmpty)
        {
            var mapped = ImmutableArray.CreateBuilder<OcrLine>(items.Length);
            foreach (var line in items)
            {
                var q = new Quad(
                    new Point2(line.Bounds.P0.X + offsetX, line.Bounds.P0.Y + offsetY),
                    new Point2(line.Bounds.P1.X + offsetX, line.Bounds.P1.Y + offsetY),
                    new Point2(line.Bounds.P2.X + offsetX, line.Bounds.P2.Y + offsetY),
                    new Point2(line.Bounds.P3.X + offsetX, line.Bounds.P3.Y + offsetY));
                mapped.Add(new OcrLine(line.Text, q, line.Confidence, line.Language, line.Words));
            }
            items = mapped.MoveToImmutable();
        }

        _lastStamp = stamp;
        _lastLines = items;
        SendOutputButton.IsEnabled = items.Length > 0;
        if (batch.Status == StageStatus.Faulted)
        {
            Append($"OCR faulted: {batch.Fault?.Code} {batch.Fault?.Message}");
            SetStatus("OCR 识别故障");
            return;
        }
        var record = new ScanRecord(Guid.NewGuid(), new AnalysisId(stamp.Id, 1), stamp, DateTimeOffset.UtcNow,
            items, ImmutableDictionary<string, string>.Empty);
        AddRecord(record);
        SetPipelineStatus(_preview is not null ? "预览中" : "待扫描", active: _preview is not null);
        SetStatus($"OCR {batch.Status} · {items.Length} 行 · {batch.EngineTime.TotalMilliseconds:0} ms");
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
            SetStatus("输出设置已应用");
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
        _settings.SetOcrModel(model);
        var ocrParamsElement = JsonSerializer.SerializeToElement(_settings.OcrParameters);
        var targetSettings = new OcrSettings([], _settings.OcrLayout, ocrParamsElement);

        if (_ocrReader is not null &&
            string.Equals(_ocrReaderModel, model, StringComparison.OrdinalIgnoreCase) &&
            _activeOcrSettings is not null &&
            _activeOcrSettings.Layout == targetSettings.Layout &&
            _activeOcrSettings.ProviderOptions.GetRawText() == ocrParamsElement.GetRawText())
        {
            return _ocrReader;
        }

        if (_ocrReader is not null)
        {
            await _ocrReader.DisposeAsync().ConfigureAwait(true);
            _ocrReader = null;
        }

        _ocrReader = await _ocrFactory.CreateAsync(targetSettings, CancellationToken.None).ConfigureAwait(true);
        _ocrReaderModel = model;
        _activeOcrSettings = targetSettings;
        Append($"OCR reader ready: {_ocrReader.Descriptor.ProviderId} ({model}, {_settings.OcrLayout})");
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
            SetStatus("运行出错");
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
        StartScanButton.IsVisible = !scanning;
        StopScanButton.IsEnabled = enabled && scanning && !roiEditing;
        StopScanButton.IsVisible = scanning;
        StartPreviewButton.IsEnabled = enabled && !scanning && !previewing && !roiEditing;
        StopPreviewButton.IsEnabled = enabled && previewing && !roiEditing;
        OcrFrameButton.IsEnabled = enabled && previewing && !roiEditing;
        CameraDeviceCombo.IsEnabled = enabled && !scanning && !previewing && !roiEditing;
        CameraModeCombo.IsEnabled = enabled && !scanning && !previewing && !roiEditing;
        RefreshCamerasButton.IsEnabled = enabled && !scanning && !previewing && !roiEditing;
        BtnEditRoi.IsEnabled = enabled && !_roiBusy && !_isEditingRoi && !scanning && _sourceWidth > 0 && _sourceHeight > 0;
        SendOutputButton.IsEnabled = enabled && !_lastLines.IsDefaultOrEmpty;
        UpdateToolbarActions();
    }

    private void UpdateToolbarActions()
    {
        if (QuickOcrButton is null) return;

        bool scanning = _activeSession is not null;
        bool previewing = _preview?.IsRunning == true;
        bool hasSelection = HasValidCaptureSelection();
        bool imageSource = hasSelection &&
                           ImagePlaylistCameraProvider.IsImageDevice(_cameras[CameraDeviceCombo.SelectedIndex].Id);
        bool canStartScan = hasSelection && (!imageSource || _playlist.Count > 0);

        bool canStillOcr = imageSource && _playlist.Count > 0 && !scanning && !previewing;
        bool canFrameOcr = previewing && !scanning;
        QuickOcrButton.IsVisible = canStillOcr || canFrameOcr;
        QuickOcrButton.IsEnabled = !_busy;
        QuickOcrText.Text = canFrameOcr ? "识别当前帧" : "识别当前图片";
        StartScanButton.IsEnabled = !_busy && !scanning && !previewing && !_isEditingRoi && canStartScan;
    }

    private bool HasValidCaptureSelection()
    {
        int deviceIndex = CameraDeviceCombo.SelectedIndex;
        int modeIndex = CameraModeCombo.SelectedIndex;
        return deviceIndex >= 0 && deviceIndex < _cameras.Length &&
               CameraModeCombo.Tag is ImmutableArray<CaptureMode> modes &&
               modeIndex >= 0 && modeIndex < modes.Length;
    }

    private void SetStatus(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetStatus(text));
            return;
        }
        StatusText.Text = text;
    }

    private void SetPipelineStatus(string text, bool active = false, bool fault = false)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetPipelineStatus(text, active, fault));
            return;
        }
        TxtSessionState.Text = text;
        UpdateStatusPill(text, active, fault);
    }

    private void SetHeaderSessionStatus(string text, bool active = false, bool fault = false)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetHeaderSessionStatus(text, active, fault));
            return;
        }
        TxtHeaderSession.Text = text;
        DotHeaderStatus.Fill = ResolveBrush(fault ? "DangerBrush" : active ? "SuccessBrush" : "TextMutedBrush", Brushes.Gray);
    }

    private void UpdateStatusPill(string text, bool active, bool fault)
    {
        bool isFault = fault || text.Contains("错误", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("fault", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed", StringComparison.OrdinalIgnoreCase);
        bool isActive = active || text.Contains("扫描", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("running", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("识别", StringComparison.OrdinalIgnoreCase);
        bool isWarning = text.Contains("警告", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("拒绝", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("暂停", StringComparison.OrdinalIgnoreCase);

        string background = isFault || isWarning ? "SurfaceSubtleBackgroundBrush" :
            isActive ? "BrandSoftBrush" : "SurfaceSubtleBackgroundBrush";
        string border = isFault ? "DangerBrush" : isWarning ? "WarningBrush" :
            isActive ? "BrandBrush" : "BorderSubtleBrush";
        string foreground = isFault ? "DangerBrush" : isWarning ? "WarningBrush" :
            isActive ? "BrandBrush" : "TextPrimaryBrush";

        PillScanStatus.Background = ResolveBrush(background, Brushes.Transparent);
        PillScanStatus.BorderBrush = ResolveBrush(border, Brushes.Transparent);
        DotState.Fill = ResolveBrush(border, Brushes.Gray);
        TxtSessionState.Foreground = ResolveBrush(foreground, Brushes.White);
    }

    private void SetPreviewChrome(bool running, bool scanning)
    {
        bool showPlaceholder = (!running && PreviewImage.Source is null) || (running && !_settings.PreviewEnabled);
        PanelPlaceholder.IsVisible = showPlaceholder;

        if (showPlaceholder)
        {
            if (running && !_settings.PreviewEnabled)
            {
                TxtPlaceholderTitle.Text = "预览已关闭";
                TxtPlaceholderSub.Text = "连续采样与识别仍在后台正常运行";
            }
            else if (_cameras.Length == 0 && _playlist.Count == 0)
            {
                TxtPlaceholderTitle.Text = "未检测到相机设备";
                TxtPlaceholderSub.Text = "请插入 USB 相机或点击【导入图片】加载本地图集";
            }
            else if (CameraDeviceCombo.SelectedItem is null)
            {
                TxtPlaceholderTitle.Text = "未选择相机";
                TxtPlaceholderSub.Text = "请在上方工具栏或系统设置中选择输入设备";
            }
            else
            {
                TxtPlaceholderTitle.Text = "未启动扫描";
                TxtPlaceholderSub.Text = "选择相机与工作模式后，点击上方【启动扫描】";
            }
        }

        var success = ResolveBrush("SuccessBrush", Brushes.LimeGreen);
        var muted = ResolveBrush("TextMutedBrush", Brushes.Gray);
        DotHudLive.Fill = running ? success : muted;
        DotState.Fill = running ? success : muted;
        TxtHudTitle.Text = scanning ? "连续扫描" : running ? "实时画面" : "取景预览";
        TxtPreviewMetrics.Text = running ? (scanning ? "会话预览中" : "预览中") : "未启用";
        RedrawOverlay();
    }

    // ==================== Viewport 缩放、平移与交互 ====================

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (ResultToastScrollerOcr != null)
        {
            ResultToastScrollerOcr.MaxHeight = Math.Max(0, e.NewSize.Height - 100);
            ResultToastScrollerOcr.Width = Math.Min(260, Math.Max(0, e.NewSize.Width - 24));
        }
        RedrawOverlay();
    }

    private void OnViewportMouseWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        double delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.001) return;
        double factor = delta > 0 ? 1.15 : (1.0 / 1.15);
        Point cursorInHost = e.GetPosition(ViewportHost);
        ZoomAtPoint(factor, cursorInHost);
    }

    private void OnViewportMouseDown(object? sender, PointerPressedEventArgs e)
    {
        if (_isEditingRoi)
        {
            HandleRoiMouseDown(e);
            return;
        }

        if (e.ClickCount == 2)
        {
            StopViewportPan();
            ResetViewportZoom();
            e.Handled = true;
            return;
        }

        var props = e.GetCurrentPoint(ViewportCanvasArea).Properties;
        if (props.IsLeftButtonPressed)
        {
            _dragStartPoint = e.GetPosition(ViewportHost);
            _isDraggingViewport = true;
            e.Pointer.Capture(ViewportCanvasArea);
            ViewportCanvasArea.Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
        }
    }

    private void OnViewportMouseMove(object? sender, PointerEventArgs e)
    {
        if (_isEditingRoi)
        {
            HandleRoiMouseMove(e);
            return;
        }

        if (_isDraggingViewport)
        {
            Point current = e.GetPosition(ViewportHost);
            double dx = current.X - _dragStartPoint.X;
            double dy = current.Y - _dragStartPoint.Y;
            _dragStartPoint = current;
            _viewportTranslate.X += dx;
            _viewportTranslate.Y += dy;
            e.Handled = true;
        }
    }

    private void OnViewportMouseUp(object? sender, PointerReleasedEventArgs e)
    {
        if (_isEditingRoi)
        {
            HandleRoiMouseUp(e);
            return;
        }

        if (_isDraggingViewport && e.InitialPressMouseButton == MouseButton.Left)
        {
            StopViewportPan();
            e.Handled = true;
        }
    }

    private void OnViewportLostMouseCapture(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_isEditingRoi)
        {
            OnRoiLostMouseCapture(sender, e);
            return;
        }

        StopViewportPan();
    }

    private void StopViewportPan()
    {
        _isDraggingViewport = false;
        ViewportCanvasArea.Cursor = _isEditingRoi ? new Cursor(StandardCursorType.Cross) : new Cursor(StandardCursorType.Arrow);
    }

    private void OnViewportDoubleTap(object? sender, TappedEventArgs e)
    {
        StopViewportPan();
        ResetViewportZoom();
        e.Handled = true;
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) =>
        ZoomAtPoint(1.25, new Point(ViewportHost.Bounds.Width * 0.5, ViewportHost.Bounds.Height * 0.5));

    private void OnZoomOut(object? sender, RoutedEventArgs e) =>
        ZoomAtPoint(1.0 / 1.25, new Point(ViewportHost.Bounds.Width * 0.5, ViewportHost.Bounds.Height * 0.5));

    private void OnZoomReset(object? sender, RoutedEventArgs e) => ResetViewportZoom();

    private void ZoomAtPoint(double factor, Point center)
    {
        double oldZoom = _zoomFactor;
        double newZoom = Math.Clamp(_zoomFactor * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 0.001) return;

        double k = newZoom / oldZoom;
        _zoomFactor = newZoom;
        _viewportScale.ScaleX = _zoomFactor;
        _viewportScale.ScaleY = _zoomFactor;

        double originX = ViewportHost.Bounds.Width * 0.5;
        double originY = ViewportHost.Bounds.Height * 0.5;
        _viewportTranslate.X = (center.X - originX) * (1 - k) + _viewportTranslate.X * k;
        _viewportTranslate.Y = (center.Y - originY) * (1 - k) + _viewportTranslate.Y * k;

        UpdateZoomLevelText();
        RedrawOverlay();
    }

    private void ResetViewportZoom()
    {
        _zoomFactor = 1.0;
        _viewportScale.ScaleX = 1.0;
        _viewportScale.ScaleY = 1.0;
        _viewportTranslate.X = 0;
        _viewportTranslate.Y = 0;
        UpdateZoomLevelText();
        RedrawOverlay();
    }

    private void UpdateZoomLevelText() =>
        TxtZoomLevel.Text = $"{Math.Round(_zoomFactor * 100)}%";

    private void OnToggleGuides(object? sender, RoutedEventArgs e)
    {
        _settings.ShowPreviewGuides = !_settings.ShowPreviewGuides;
        RedrawOverlay();
    }

    private void UpdatePreviewSurfaceGeometry()
    {
        if (_sourceWidth <= 0 || _sourceHeight <= 0) return;
        RedrawOverlay();
        SetBusyUi(!_busy);
    }

    private void ClearPreviewSurfaceGeometry()
    {
        _sourceWidth = 0;
        _sourceHeight = 0;
        _lastLines = [];
        SyncFrameAnnotations();
        SetBusyUi(!_busy);
    }

    // ==================== Overlay 绘制与 OCR 浮动气泡 ====================

    private void SyncFrameAnnotations()
    {
        ReplaceResultToasts(_lastLines);
        RedrawOverlay();
    }

    private void RedrawOverlay()
    {
        OverlayCanvas.Children.Clear();
        if (_isEditingRoi)
        {
            DrawRoiEditorOverlay();
            return;
        }

        if (_sourceWidth <= 0 || _sourceHeight <= 0) return;
        double canvasW = OverlayCanvas.Bounds.Width;
        double canvasH = OverlayCanvas.Bounds.Height;
        if (canvasW <= 0 || canvasH <= 0) return;

        double scale = Math.Min(canvasW / _sourceWidth, canvasH / _sourceHeight);
        double displayW = _sourceWidth * scale;
        double displayH = _sourceHeight * scale;
        double offsetX = (canvasW - displayW) / 2.0;
        double offsetY = (canvasH - displayH) / 2.0;

        if (_settings.ShowPreviewGuides)
            DrawPreviewGuides(offsetX, offsetY, displayW, displayH);

        // Draw bounding boxes for OCR lines
        if (!_lastLines.IsDefaultOrEmpty)
        {
            foreach (var item in _lastLines)
            {
                bool isHovered = ReferenceEquals(_hoveredAnnotation, item) ||
                                 (_hoveredAnnotation is OcrLine o &&
                                  o.Text == item.Text &&
                                  o.Bounds.Equals(item.Bounds));

                var poly = new Polygon
                {
                    StrokeThickness = isHovered ? 3 : 2,
                    Points =
                    [
                        new Point(offsetX + item.Bounds.P0.X * scale, offsetY + item.Bounds.P0.Y * scale),
                        new Point(offsetX + item.Bounds.P1.X * scale, offsetY + item.Bounds.P1.Y * scale),
                        new Point(offsetX + item.Bounds.P2.X * scale, offsetY + item.Bounds.P2.Y * scale),
                        new Point(offsetX + item.Bounds.P3.X * scale, offsetY + item.Bounds.P3.Y * scale)
                    ]
                };

                if (isHovered)
                {
                    poly.Fill = ResolveBrush("SymbologyBadgeOcrTintBrush", new SolidColorBrush(Color.FromArgb(50, 0, 122, 255)));
                    poly.Stroke = ResolveBrush("SymbologyBadgeOcrTextBrush", Brushes.DodgerBlue);
                }
                else
                {
                    poly.Fill = Brushes.Transparent;
                    poly.Stroke = ResolveBrush("BrandBrush", Brushes.DodgerBlue);
                }

                var captured = item;
                poly.PointerEntered += (_, _) =>
                {
                    _hoveredAnnotation = captured;
                    RedrawOverlay();
                };
                poly.PointerExited += (_, _) =>
                {
                    if (ReferenceEquals(_hoveredAnnotation, captured))
                    {
                        _hoveredAnnotation = null;
                        RedrawOverlay();
                    }
                };

                OverlayCanvas.Children.Add(poly);
            }
        }

        // Draw ROI rectangle if enabled
        if (_settings.EnableRoi && TryGetRoiGeometry(out var ix, out var iy, out var iw, out var ih))
        {
            var accent = ResolveBrush("BrandBrush", Brushes.DodgerBlue);
            double rx = ix + (_settings.RoiX / 100.0) * iw;
            double ry = iy + (_settings.RoiY / 100.0) * ih;
            double rw = (_settings.RoiWidth / 100.0) * iw;
            double rh = (_settings.RoiHeight / 100.0) * ih;
            var rect = new Rectangle
            {
                Width = Math.Max(0, rw),
                Height = Math.Max(0, rh),
                Stroke = accent,
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 2],
                Fill = new SolidColorBrush(Color.FromArgb(16, 0, 122, 255)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rect, rx);
            Canvas.SetTop(rect, ry);
            OverlayCanvas.Children.Add(rect);
        }
    }

    private void DrawPreviewGuides(double offsetX, double offsetY, double displayW, double displayH)
    {
        var brush = ResolveBrush("ReticleAccentBrush", Brushes.DeepSkyBlue);
        double midX = offsetX + displayW * 0.5;
        double midY = offsetY + displayH * 0.5;
        var lineH = new Line
        {
            StartPoint = new Point(offsetX, midY),
            EndPoint = new Point(offsetX + displayW, midY),
            Stroke = brush,
            StrokeThickness = 1.2,
            Opacity = 0.85,
            IsHitTestVisible = false
        };
        var lineV = new Line
        {
            StartPoint = new Point(midX, offsetY),
            EndPoint = new Point(midX, offsetY + displayH),
            Stroke = brush,
            StrokeThickness = 1.2,
            Opacity = 0.85,
            IsHitTestVisible = false
        };
        OverlayCanvas.Children.Add(lineH);
        OverlayCanvas.Children.Add(lineV);
    }

    private void ReplaceResultToasts(ImmutableArray<OcrLine> ocrLines)
    {
        ResultToastStackOcr.Children.Clear();
        if (ocrLines.IsDefaultOrEmpty)
        {
            ResultToastScrollerOcr.IsVisible = false;
            return;
        }

        foreach (var line in ocrLines)
        {
            var capturedLine = line;
            string confText = line.Confidence.HasValue ? $" ({line.Confidence.Value * 100:0.#}%)" : "";
            var heading = new TextBlock
            {
                Text = $"OCR{confText}",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = ResolveBrush("SymbologyBadgeOcrTextBrush", Brushes.DeepSkyBlue)
            };
            var content = new TextBlock
            {
                Text = line.Text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 36,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = ResolveBrush("TextPrimaryBrush", Brushes.White)
            };
            var body = new StackPanel();
            body.Children.Add(heading);
            body.Children.Add(content);

            bool isHovered = ReferenceEquals(_hoveredAnnotation, capturedLine) ||
                             (_hoveredAnnotation is OcrLine o && o.Text == capturedLine.Text && o.Bounds.Equals(capturedLine.Bounds));
            var card = new Border
            {
                Tag = capturedLine,
                Child = body,
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 7),
                Margin = new Thickness(0, 0, 4, 6),
                BorderBrush = ResolveBrush("SymbologyBadgeOcrBorderBrush", Brushes.DeepSkyBlue),
                Background = ResolveBrush(isHovered ? "SymbologyBadgeOcrBorderBrush" : "SymbologyBadgeOcrTintBrush",
                    new SolidColorBrush(Color.FromArgb(140, 22, 34, 53)))
            };
            ToolTip.SetTip(card, line.Text);

            card.PointerPressed += (_, pe) =>
            {
                if (pe.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                {
                    var match = _allResults.FirstOrDefault(r => r.Text == capturedLine.Text);
                    if (match != null) ResultsList.SelectedItem = match;
                }
            };
            card.PointerEntered += (_, _) =>
            {
                _hoveredAnnotation = capturedLine;
                RedrawOverlay();
            };
            card.PointerExited += (_, _) =>
            {
                if (ReferenceEquals(_hoveredAnnotation, capturedLine))
                {
                    _hoveredAnnotation = null;
                    RedrawOverlay();
                }
            };
            ResultToastStackOcr.Children.Add(card);
        }

        ResultToastScrollerOcr.IsVisible = ResultToastStackOcr.Children.Count > 0;
        if (ResultToastStackOcr.Children.Count > 0)
        {
            ResultToastScrollerOcr.ScrollToEnd();
        }
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_isEditingRoi)
        {
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
            return;
        }

        if (e.Key == Key.Space)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is not TextBox)
            {
                if (_activeSession is not null)
                    await StopScanningAsync();
                else
                    await StartScanningAsync();
                e.Handled = true;
            }
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
        _logLinesCount++;
        TxtLogLineCount.Text = $"{_logLinesCount} 行";
        Log.Text = string.IsNullOrEmpty(Log.Text) ? line : Log.Text + Environment.NewLine + line;
        Log.CaretIndex = Log.Text?.Length ?? 0;
    }
}
