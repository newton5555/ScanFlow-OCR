using System.Collections.Immutable;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Ocr.SimdPaddle;

public static class SimdPaddleOcrMetadata
{
    public const string ProviderId = "SimdPaddle";
    public const string EngineVersion = "1.3.0";

    public static readonly ImmutableArray<AlgorithmParameterDescriptor> Parameters =
    [
        new("model", "模型档", AlgorithmParameterType.Choice, "OCR",
            "PP-OCRv6 中文模型；支持 Tiny/Small。建议按设备实测。", "tiny",
            Options: [new("tiny", "V6 Tiny", "轻量默认档"), new("small", "V6 Small", "更大模型，供精度对比")]),
        new("detectSideLength", "检测长边", AlgorithmParameterType.Choice, "OCR",
            "DET 缩放长边，只允许 640 或 960。", "640",
            Options: [new("640", "640", "较低耗时"), new("960", "960", "较大画幅")]),
        new("useDirectionClassification", "方向自动纠正", AlgorithmParameterType.Boolean, "OCR",
            "CLS 仅判断 0/180 度，不是任意页面方向。", "true"),
        new("lineWorkerCount", "行并行度", AlgorithmParameterType.Integer, "OCR",
            "一行一组 CLS/REC worker。1 仍可能启用识别内部线程。", "1",
            IsAdvanced: true, MinIntValue: 1, MaxIntValue: 8),
        new("detIntraOpThreads", "检测线程", AlgorithmParameterType.Integer, "OCR",
            "检测图内卷积线程。", "2",
            IsAdvanced: true, MinIntValue: 1, MaxIntValue: 8),
        new("recBatchLines", "识别批大小", AlgorithmParameterType.Integer, "OCR",
            "一次送入 REC 的行数。", "1",
            IsAdvanced: true, MinIntValue: 1, MaxIntValue: 8),
        new("maxPooledSessions", "会话池上限", AlgorithmParameterType.Integer, "OCR",
            "空闲会话池数量，不是进程内存上限。", "1",
            IsAdvanced: true, MinIntValue: 1, MaxIntValue: 8),
        new("minConfidencePercent", "最低 OCR 置信度", AlgorithmParameterType.Integer, "OCR",
            "仅当引擎分数位于 0–1 时生效；低于该阈值的识别结果将被过滤。", "90",
            MinIntValue: 0, MaxIntValue: 100)
    ];
}
