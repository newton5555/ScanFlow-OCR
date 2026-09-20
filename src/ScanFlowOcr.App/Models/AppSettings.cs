using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScanFlowOcr.Contracts;
using ScanFlowOcr.Ocr.SimdPaddle;
using ScanFlowOcr.Outputs;

namespace ScanFlowOcr.App.Models;

/// <summary>OCR-only app settings (mirrors private ScanFlow AppSettings without barcode fields).</summary>
public sealed class AppSettings
{
    public bool EnableRoi { get; set; }
    public bool ShowPreviewGuides { get; set; } = true;
    public double RoiX { get; set; } = 10.0;
    public double RoiY { get; set; } = 10.0;
    public double RoiWidth { get; set; } = 80.0;
    public double RoiHeight { get; set; } = 80.0;

    public DedupeMode DedupeMode { get; set; } = DedupeMode.Session;
    public int DedupeIntervalMs { get; set; } = 1500;
    public int DedupeMaxEntries { get; set; } = 10000;

    public bool PreviewEnabled { get; set; } = true;
    public int PreviewMaxFps { get; set; } = 15;
    public int PreviewMaxWidth { get; set; } = 1280;
    public int PreviewMaxHeight { get; set; } = 720;

    public int PreferredCameraIndex { get; set; }
    public int PreferredModeIndex { get; set; }

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 840;
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    public OcrLayout OcrLayout { get; set; } = OcrLayout.TextBlock;
    public int OcrTimeoutMs { get; set; } = 5000;

    public bool MqttEnabled { get; set; }
    public string MqttBroker { get; set; } = "localhost";
    public int MqttPort { get; set; } = 1883;
    public bool MqttTls { get; set; }
    public string MqttClientId { get; set; } = $"scanflow-ocr-{Environment.MachineName.ToLowerInvariant()}";
    public string MqttTopic { get; set; } = "scanflow-ocr/scans";
    public int MqttQos { get; set; } = 1;
    public string? MqttUsername { get; set; }
    public string? MqttProtectedPassword { get; set; }
    public int OutputQueueCapacity { get; set; } = 1000;
    public bool TcpEnabled { get; set; }
    public string TcpHost { get; set; } = "127.0.0.1";
    public int TcpPort { get; set; } = 9100;
    public bool KeyboardEnabled { get; set; }
    public string KeyboardTargetProcess { get; set; } = OperatingSystem.IsLinux() ? "gedit" : "notepad.exe";
    public KeyboardSuffix KeyboardSuffix { get; set; } = KeyboardSuffix.Enter;
    public KeyboardSendMode KeyboardSendMode { get; set; } = KeyboardSendMode.Combined;
    public string KeyboardSeparator { get; set; } = " | ";
    public KeyboardTargetAction KeyboardTargetAction { get; set; } = KeyboardTargetAction.KeepInQueue;

    public Dictionary<string, string> OcrParameters { get; set; } = InitDefaultOcrParameters();

    private static Dictionary<string, string> InitDefaultOcrParameters()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in SimdPaddleOcrMetadata.Parameters)
            dict[p.Key] = p.DefaultValue;
        return dict;
    }

    public string GetOcrModel() =>
        OcrParameters.TryGetValue("model", out var m) && !string.IsNullOrWhiteSpace(m) ? m : "tiny";

    public void SetOcrModel(string model)
    {
        OcrParameters["model"] = string.IsNullOrWhiteSpace(model) ? "tiny" : model.Trim().ToLowerInvariant();
    }

    public MqttRoute ToMqttRoute() => new(MqttBroker, MqttPort, MqttTls, MqttClientId,
        MqttTopic, MqttQos, MqttUsername, MqttProtectedPassword);

    public ImmutableArray<OutputRouteProfile> GetOutputRoutes()
    {
        var routes = ImmutableArray.CreateBuilder<OutputRouteProfile>();
        if (MqttEnabled)
            routes.Add(new("mqtt", true, OutputQueueCapacity, 1024 * 1024,
                JsonSerializer.SerializeToElement(ToMqttRoute())));
        else if (TcpEnabled)
            routes.Add(new("tcp", true, OutputQueueCapacity, 1024 * 1024,
                JsonSerializer.SerializeToElement(new TcpRoute(TcpHost, TcpPort))));
        else if (KeyboardEnabled)
            routes.Add(new("keyboard", true, OutputQueueCapacity, 8 * 1024,
                JsonSerializer.SerializeToElement(new KeyboardRoute(
                    KeyboardTargetProcess, KeyboardSuffix, KeyboardSendMode, KeyboardSeparator, KeyboardTargetAction))));
        return routes.ToImmutable();
    }

    public AppSettings Clone() => new()
    {
        EnableRoi = EnableRoi,
        ShowPreviewGuides = ShowPreviewGuides,
        RoiX = RoiX,
        RoiY = RoiY,
        RoiWidth = RoiWidth,
        RoiHeight = RoiHeight,
        DedupeMode = DedupeMode,
        DedupeIntervalMs = DedupeIntervalMs,
        DedupeMaxEntries = DedupeMaxEntries,
        PreviewEnabled = PreviewEnabled,
        PreviewMaxFps = PreviewMaxFps,
        PreviewMaxWidth = PreviewMaxWidth,
        PreviewMaxHeight = PreviewMaxHeight,
        PreferredCameraIndex = PreferredCameraIndex,
        PreferredModeIndex = PreferredModeIndex,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        WindowLeft = WindowLeft,
        WindowTop = WindowTop,
        WindowMaximized = WindowMaximized,
        OcrLayout = OcrLayout,
        OcrTimeoutMs = OcrTimeoutMs,
        MqttEnabled = MqttEnabled,
        MqttBroker = MqttBroker,
        MqttPort = MqttPort,
        MqttTls = MqttTls,
        MqttClientId = MqttClientId,
        MqttTopic = MqttTopic,
        MqttQos = MqttQos,
        MqttUsername = MqttUsername,
        MqttProtectedPassword = MqttProtectedPassword,
        OutputQueueCapacity = OutputQueueCapacity,
        TcpEnabled = TcpEnabled,
        TcpHost = TcpHost,
        TcpPort = TcpPort,
        KeyboardEnabled = KeyboardEnabled,
        KeyboardTargetProcess = KeyboardTargetProcess,
        KeyboardSuffix = KeyboardSuffix,
        KeyboardSendMode = KeyboardSendMode,
        KeyboardSeparator = KeyboardSeparator,
        KeyboardTargetAction = KeyboardTargetAction,
        OcrParameters = new Dictionary<string, string>(OcrParameters, StringComparer.OrdinalIgnoreCase)
    };

    public bool Validate(out string? error)
    {
        if (DedupeIntervalMs < 50 || DedupeIntervalMs > 60000)
        {
            error = "去重冷却时间必须在 50 ms 到 60,000 ms 之间。";
            return false;
        }
        if (OcrTimeoutMs < 500 || OcrTimeoutMs > 30000)
        {
            error = "OCR 最大推理时间必须在 500 到 30000 ms 之间。";
            return false;
        }
        if ((MqttEnabled ? 1 : 0) + (TcpEnabled ? 1 : 0) + (KeyboardEnabled ? 1 : 0) > 1)
        {
            error = "外部输出只能选择一种方式。";
            return false;
        }
        if (OutputQueueCapacity is < 1 or > 100000)
        {
            error = "输出队列容量必须在 1–100000 之间。";
            return false;
        }
        if (MqttEnabled)
        {
            error = ToMqttRoute().Validate();
            if (error != null) return false;
        }
        if (TcpEnabled)
        {
            error = new TcpRoute(TcpHost, TcpPort).Validate();
            if (error != null) return false;
        }
        if (KeyboardEnabled)
        {
            error = new KeyboardRoute(KeyboardTargetProcess, KeyboardSuffix, KeyboardSendMode, KeyboardSeparator, KeyboardTargetAction).Validate();
            if (error != null) return false;
        }
        if (EnableRoi)
        {
            if (!double.IsFinite(RoiX) || !double.IsFinite(RoiY) ||
                !double.IsFinite(RoiWidth) || !double.IsFinite(RoiHeight))
            {
                error = "识别区域数值无效。";
                return false;
            }
            if (RoiX < 0 || RoiY < 0 || RoiWidth <= 0 || RoiHeight <= 0 ||
                (RoiX + RoiWidth > 100.001) || (RoiY + RoiHeight > 100.001))
            {
                error = "ROI 起点与终点须在 0%–100% 内，且终点须大于起点。";
                return false;
            }
        }
        if (!AlgorithmParameterValidator.Validate(SimdPaddleOcrMetadata.Parameters, OcrParameters, out error))
            return false;
        error = null;
        return true;
    }

    public SessionProfile ToSessionProfile(CameraId deviceId, CaptureMode mode)
    {
        Rect2? region = null;
        if (EnableRoi)
        {
            int sourceW = mode.Width;
            int sourceH = mode.Height;
            if (sourceW <= 0 || sourceH <= 0)
                throw new InvalidOperationException($"相机模式分辨率无效 ({sourceW}x{sourceH})。");

            double pixelX = Math.Clamp(Math.Round(sourceW * (RoiX / 100.0)), 0, Math.Max(0, sourceW - 1));
            double pixelY = Math.Clamp(Math.Round(sourceH * (RoiY / 100.0)), 0, Math.Max(0, sourceH - 1));
            double pixelW = Math.Clamp(Math.Round(sourceW * (RoiWidth / 100.0)), 1, Math.Max(1, sourceW - pixelX));
            double pixelH = Math.Clamp(Math.Round(sourceH * (RoiHeight / 100.0)), 1, Math.Max(1, sourceH - pixelY));
            region = new Rect2(pixelX, pixelY, pixelW, pixelH);
        }

        var ocrBudget = TimeSpan.FromMilliseconds(OcrTimeoutMs);
        var scheduling = new SchedulingProfile(
            PendingFrames: 1,
            MaxQueueAge: TimeSpan.FromMilliseconds(OcrTimeoutMs),
            MaxResultAge: TimeSpan.FromMilliseconds(OcrTimeoutMs + 3000),
            OcrBudget: ocrBudget,
            MaxFrameBytes: 5120L * 5120L * 4L,
            // ~128 MiB: a few full BGR frames + JPEG/size-class idle for 1080p/4K MJPEG.
            // Must stay >= MaxFrameBytes (validation). Was ~400 MiB (×4) which let idle grow.
            RetainedByteLimit: 128L * 1024L * 1024L);

        return new SessionProfile(
            SchemaVersion: 1,
            Source: new CameraSourceProfile(new CameraOpenOptions(deviceId, mode.ModeId)),
            Workflow: WorkflowMode.OcrOnly,
            Ocr: new OcrStageProfile(
                ProviderId: SimdPaddleOcrMetadata.ProviderId,
                Settings: new OcrSettings([], OcrLayout, JsonSerializer.SerializeToElement(OcrParameters)),
                Region: region),
            Scheduling: scheduling,
            Preview: new PreviewProfile(PreviewEnabled, PreviewMaxFps, PreviewMaxWidth, PreviewMaxHeight),
            Dedupe: new DedupeProfile(DedupeMode, TimeSpan.FromMilliseconds(DedupeIntervalMs), DedupeMaxEntries),
            Outputs: GetOutputRoutes());
    }
}
