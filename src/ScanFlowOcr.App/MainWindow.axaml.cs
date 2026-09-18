using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ScanFlowOcr.Capture.FlashCap;
using ScanFlowOcr.Contracts;
using ScanFlowOcr.Imaging;
using ScanFlowOcr.Ocr.SimdPaddle;
using ScanFlowOcr.Outputs;

namespace ScanFlowOcr.App;

public partial class MainWindow : Window, IAsyncDisposable
{
    private readonly List<string> _imagePaths = [];
    private readonly OutputCoordinator _coordinator;
    private readonly SimdPaddleOcrFactory _ocrFactory = new();
    private readonly FlashCapProvider _cameraProvider = new();
    private ImmutableArray<CameraDescriptor> _cameras = [];
    private CameraPreviewController? _preview;
    private IOcrReader? _ocrReader;
    private string? _ocrReaderModel;
    private ImmutableArray<OcrLine> _lastLines = [];
    private FrameStamp? _lastStamp;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _coordinator = new OutputCoordinator();
        Append("ScanFlow-OCR — still images + camera MJPEG preview (TurboJPEG 3.2.0).");
        Append($"OS: {Environment.OSVersion}; 64-bit: {Environment.Is64BitProcess}");
        if (TurboJpegNative.TryResolveLibraryPath(out string? tj, out string tjErr))
            Append($"TurboJPEG OK: {tj}");
        else
            Append($"TurboJPEG not found yet (camera preview needs it): {tjErr}");
        Closed += OnClosed;
        _ = RefreshCamerasAsync();
    }

    private bool _disposed;

    private async void OnClosed(object? sender, EventArgs e)
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort shutdown */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        Closed -= OnClosed;
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
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }

    private async void OnOpenImages(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select image(s) for OCR",
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

            _imagePaths.Clear();
            foreach (var file in files)
            {
                string? path = file.TryGetLocalPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Append($"Skip (no local path): {file.Name}");
                    continue;
                }
                _imagePaths.Add(path);
            }

            ImageList.ItemsSource = _imagePaths.Select(Path.GetFileName).ToList();
            if (_imagePaths.Count > 0)
                ImageList.SelectedIndex = 0;

            RunOcrButton.IsEnabled = _imagePaths.Count > 0;
            SetStatus($"Loaded {_imagePaths.Count} image(s).");
            Append($"Opened {_imagePaths.Count} file(s).");
        }
        catch (Exception ex)
        {
            Append($"Open failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnImageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // selection alone does not run OCR; user clicks Run OCR
    }

    private async void OnRunOcr(object? sender, RoutedEventArgs e)
    {
        if (_busy || _imagePaths.Count == 0) return;
        int index = ImageList.SelectedIndex;
        if (index < 0 || index >= _imagePaths.Count) index = 0;
        string path = _imagePaths[index];
        string model = GetSelectedModel();

        await RunExclusiveAsync(async () =>
        {
            SetStatus($"OCR ({model})…");
            Append($"Decoding: {path}");
            StillImageBgra bgra = await Task.Run(() => StillImages.DecodeBgra(path)).ConfigureAwait(true);
            byte[] bgr = StillImages.CopyToBgr(bgra.Pixels, bgra.Width, bgra.Height);
            Append($"Decoded {bgra.Width}×{bgra.Height} BGR24 ({bgr.Length} bytes).");

            await RunOcrOnBgrAsync(bgr, bgra.Width, bgra.Height, model, "still:" + Path.GetFileName(path))
                .ConfigureAwait(true);
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
            if (_cameras.Length > 0)
                CameraDeviceCombo.SelectedIndex = 0;
            else
            {
                CameraModeCombo.ItemsSource = null;
                Append("FlashCap: no MJPEG camera devices (OK if none attached).");
            }

            Append($"FlashCap: {_cameras.Length} MJPEG device(s).");
            foreach (var d in _cameras)
                Append($"  - {d.DisplayName} [{d.Id.ProviderId}/{d.Id.DeviceKey}]");
            SetStatus($"Cameras: {_cameras.Length}");
        }
        catch (Exception ex)
        {
            Append($"Enumerate failed: {ex.GetType().Name}: {ex.Message}");
            SetCameraError($"Enumerate failed: {ex.Message}");
            SetStatus("Camera enumerate failed.");
        }
    }

    private async void OnCameraDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        int index = CameraDeviceCombo.SelectedIndex;
        if (index < 0 || index >= _cameras.Length)
        {
            CameraModeCombo.ItemsSource = null;
            return;
        }

        try
        {
            var device = _cameras[index];
            var modes = await _cameraProvider.GetModesAsync(device.Id, CancellationToken.None).ConfigureAwait(true);
            CameraModeCombo.ItemsSource = modes.Select(m =>
                $"{m.Width}×{m.Height} @ {m.FpsNumerator}/{Math.Max(1, m.FpsDenominator)} ({m.ModeId})").ToList();
            CameraModeCombo.Tag = modes;
            if (modes.Length > 0)
                CameraModeCombo.SelectedIndex = 0;
            Append($"Modes for {device.DisplayName}: {modes.Length}");
        }
        catch (Exception ex)
        {
            Append($"GetModes failed: {ex.Message}");
            SetCameraError($"GetModes failed: {ex.Message}");
        }
    }

    private async void OnStartPreview(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunExclusiveAsync(async () =>
        {
            if (!TurboJpegNative.TryResolveLibraryPath(out _, out string tjError))
            {
                SetCameraError(tjError);
                Append(tjError);
                SetStatus("TurboJPEG missing.");
                return;
            }

            int di = CameraDeviceCombo.SelectedIndex;
            int mi = CameraModeCombo.SelectedIndex;
            if (di < 0 || di >= _cameras.Length)
            {
                SetCameraError("Select a camera device first (Refresh devices).");
                return;
            }

            if (CameraModeCombo.Tag is not ImmutableArray<CaptureMode> modes || mi < 0 || mi >= modes.Length)
            {
                SetCameraError("Select an MJPEG mode.");
                return;
            }

            if (_preview is not null)
            {
                await _preview.DisposeAsync().ConfigureAwait(true);
                _preview = null;
            }

            var device = _cameras[di];
            var mode = modes[mi];
            ICameraSession session = await _cameraProvider
                .OpenAsync(new CameraOpenOptions(device.Id, mode.ModeId), CancellationToken.None)
                .ConfigureAwait(true);

            _preview = new CameraPreviewController(
                Append,
                SetCameraError,
                bmp => PreviewImage.Source = bmp,
                () =>
                {
                    StartPreviewButton.IsEnabled = true;
                    StopPreviewButton.IsEnabled = false;
                    OcrFrameButton.IsEnabled = false;
                });

            try
            {
                await _preview.StartAsync(session, CancellationToken.None).ConfigureAwait(true);
                StartPreviewButton.IsEnabled = false;
                StopPreviewButton.IsEnabled = true;
                OcrFrameButton.IsEnabled = true;
                SetStatus("Preview running.");
            }
            catch
            {
                // Session ownership transferred into controller (disposed there on failure).
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
            SetStatus("Preview stopped.");
            Append("Camera preview stopped.");
        }).ConfigureAwait(true);
    }

    private async void OnOcrCurrentFrame(object? sender, RoutedEventArgs e)
    {
        if (_busy || _preview is null) return;
        if (!_preview.TryGetLatestBgra(out byte[] bgra, out int w, out int h, out FrameStamp stamp))
        {
            Append("No decoded frame yet — wait for preview.");
            return;
        }

        string model = GetSelectedModel();
        await RunExclusiveAsync(async () =>
        {
            SetStatus($"OCR frame ({model})…");
            byte[] bgr = await Task.Run(() => StillImages.CopyToBgr(bgra, w, h)).ConfigureAwait(true);
            Append($"OCR current frame {w}×{h} from camera.");
            await RunOcrOnBgrAsync(bgr, w, h, model, stamp.SourceId).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RunOcrOnBgrAsync(byte[] bgr, int width, int height, string model, string sourceId)
    {
        IOcrReader reader = await EnsureOcrReaderAsync(model).ConfigureAwait(true);
        var stamp = new FrameStamp(
            new FrameId(Guid.NewGuid(), 1),
            sourceId,
            width, height,
            Stopwatch.GetTimestamp(),
            null);
        var layout = new ImageLayout(
            FrameEncoding.Raw, PixelFormat.Bgr24, width, height,
            [new PlaneLayout(0, width * 3, width * 3, height)],
            ColorRange.Full, ColorMatrix.Unspecified);
        var input = new ImageInput(stamp, layout, bgr, ImageTransform.Identity);
        var request = new RecognitionRequest(
            new AnalysisId(stamp.Id, 1),
            Stopwatch.GetTimestamp() + Stopwatch.Frequency * 120);

        EngineBatch<OcrLine> batch = await reader.ReadAsync(input, request, CancellationToken.None)
            .ConfigureAwait(true);

        _lastStamp = stamp;
        _lastLines = batch.Items;
        SendOutputButton.IsEnabled = batch.Items.Length > 0;

        if (batch.Status == StageStatus.Faulted)
        {
            OcrText.Text = "";
            Append($"OCR faulted: {batch.Fault?.Code} {batch.Fault?.Message}");
            SetStatus("OCR faulted.");
            return;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < batch.Items.Length; i++)
        {
            OcrLine line = batch.Items[i];
            string conf = line.Confidence is double c
                ? (c * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"
                : "?";
            sb.AppendLine(line.Text);
            Append($"  [{i}] conf={conf}  {line.Text}");
        }

        OcrText.Text = sb.ToString().TrimEnd();
        SetStatus($"OCR {batch.Status}: {batch.Items.Length} line(s) in {batch.EngineTime.TotalMilliseconds:0} ms.");
        Append($"Done: {batch.Items.Length} line(s), {batch.EngineTime.TotalMilliseconds:0} ms, status={batch.Status}.");
    }

    private void OnApplyOutput(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (SinkNone.IsChecked == true)
            {
                _coordinator.Configure([]);
                Append("Output disabled.");
                SetStatus("Output: none");
                return;
            }

            if (SinkKeyboard.IsChecked == true)
            {
                var route = new KeyboardRoute(KeyboardTarget.Text?.Trim() ?? "notepad.exe");
                string? err = route.Validate();
                if (err is not null) throw new ArgumentException(err);
                _coordinator.Configure([
                    new OutputRouteProfile("keyboard", true, 100, 1024 * 1024,
                        JsonSerializer.SerializeToElement(route))
                ]);
                Append($"Output: keyboard → {route.TargetProcess}");
                SetStatus("Output: keyboard");
                return;
            }

            if (SinkMqtt.IsChecked == true)
            {
                if (!int.TryParse(MqttPort.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
                    throw new ArgumentException("MQTT port invalid.");
                var route = new MqttRoute(
                    MqttBroker.Text?.Trim() ?? "localhost",
                    port,
                    false,
                    $"scanflow-ocr-{Environment.MachineName.ToLowerInvariant()}",
                    MqttTopic.Text?.Trim() ?? "scanflow-ocr/scans",
                    1, null, null);
                string? err = route.Validate();
                if (err is not null) throw new ArgumentException(err);
                _coordinator.Configure([
                    new OutputRouteProfile("mqtt", true, 100, 1024 * 1024,
                        JsonSerializer.SerializeToElement(route))
                ]);
                Append($"Output: MQTT {route.Broker}:{route.Port} topic={route.Topic}");
                SetStatus("Output: mqtt");
                return;
            }

            if (SinkTcp.IsChecked == true)
            {
                if (!int.TryParse(TcpPort.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
                    throw new ArgumentException("TCP port invalid.");
                var route = new TcpRoute(TcpHost.Text?.Trim() ?? "127.0.0.1", port, false);
                string? err = route.Validate();
                if (err is not null) throw new ArgumentException(err);
                _coordinator.Configure([
                    new OutputRouteProfile("tcp", true, 100, 1024 * 1024,
                        JsonSerializer.SerializeToElement(route))
                ]);
                Append($"Output: TCP {route.Host}:{route.Port}");
                SetStatus("Output: tcp");
                return;
            }
        }
        catch (Exception ex)
        {
            Append($"Apply output failed: {ex.Message}");
            SetStatus("Output config error.");
        }
    }

    private async void OnSendOutput(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_lastLines.IsDefaultOrEmpty || _lastStamp is null)
        {
            Append("Nothing to send — run OCR first.");
            return;
        }

        await RunExclusiveAsync(async () =>
        {
            var stamp = _lastStamp.Value;
            var record = new ScanRecord(
                Guid.NewGuid(),
                new AnalysisId(stamp.Id, 1),
                stamp,
                DateTimeOffset.UtcNow,
                _lastLines,
                ImmutableDictionary<string, string>.Empty);

            bool accepted = await _coordinator.TryAcceptAsync(record, CancellationToken.None).ConfigureAwait(true);
            var status = _coordinator.GetStatus();
            Append(accepted
                ? $"Queued EventId={record.EventId:N} pending={status.PendingCount} delivered={status.DeliveredCount}"
                : $"Admission rejected: {status.LastError ?? "(unknown)"}");
            SetStatus(accepted ? "Queued for output." : "Admission rejected.");

            await Task.Delay(800).ConfigureAwait(true);
            status = _coordinator.GetStatus();
            Append($"Output status: enabled={status.Enabled} pending={status.PendingCount} delivered={status.DeliveredCount} uncertain={status.UncertainCount} last={status.LastReceiptCode ?? status.LastError ?? "-"}");
        }).ConfigureAwait(true);
    }

    private string GetSelectedModel()
    {
        if (ModelCombo.SelectedItem is ComboBoxItem item && item.Content is string s)
            return s;
        return "tiny";
    }

    private async Task<IOcrReader> EnsureOcrReaderAsync(string model)
    {
        if (_ocrReader is not null && string.Equals(_ocrReaderModel, model, StringComparison.OrdinalIgnoreCase))
            return _ocrReader;

        if (_ocrReader is not null)
        {
            await _ocrReader.DisposeAsync().ConfigureAwait(true);
            _ocrReader = null;
            _ocrReaderModel = null;
        }

        Append($"Loading SimdPaddle model '{model}' (first load may download)…");
        using var doc = JsonDocument.Parse($"{{\"model\":\"{model}\"}}");
        var settings = new OcrSettings([], OcrLayout.TextBlock, doc.RootElement.Clone());
        _ocrReader = await _ocrFactory.CreateAsync(settings, CancellationToken.None).ConfigureAwait(true);
        _ocrReaderModel = model;
        Append($"OCR reader ready: {_ocrReader.Descriptor.ProviderId} {_ocrReader.Descriptor.Version}");
        return _ocrReader;
    }

    private async Task RunExclusiveAsync(Func<Task> action)
    {
        _busy = true;
        SetBusyUi(false);
        try
        {
            await action().ConfigureAwait(true);
        }
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
        OpenImagesButton.IsEnabled = enabled;
        RunOcrButton.IsEnabled = enabled && _imagePaths.Count > 0;
        ApplyOutputButton.IsEnabled = enabled;
        SendOutputButton.IsEnabled = enabled && !_lastLines.IsDefaultOrEmpty;
        ModelCombo.IsEnabled = enabled;
        RefreshCamerasButton.IsEnabled = enabled;
        CameraDeviceCombo.IsEnabled = enabled && !(_preview?.IsRunning ?? false);
        CameraModeCombo.IsEnabled = enabled && !(_preview?.IsRunning ?? false);
        bool previewing = _preview?.IsRunning ?? false;
        StartPreviewButton.IsEnabled = enabled && !previewing;
        StopPreviewButton.IsEnabled = enabled && previewing;
        OcrFrameButton.IsEnabled = enabled && previewing;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void SetCameraError(string text)
    {
        CameraErrorText.Text = text;
        CameraErrorText.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private void Append(string line)
    {
        Log.Text = string.IsNullOrEmpty(Log.Text) ? line : Log.Text + Environment.NewLine + line;
        Log.CaretIndex = Log.Text?.Length ?? 0;
    }
}
