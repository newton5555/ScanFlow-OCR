using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ScanFlowOcr.Contracts;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.ModelProvider;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Small;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;

namespace ScanFlowOcr.Ocr.SimdPaddle;

public sealed class SimdPaddleOcrFactory : IOcrReaderFactory
{
    public string ProviderId => SimdPaddleOcrMetadata.ProviderId;
    public ImmutableArray<AlgorithmParameterDescriptor> Parameters => SimdPaddleOcrMetadata.Parameters;

    public async ValueTask<IOcrReader> CreateAsync(OcrSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = ParsedOcrOptions.FromSettings(settings);
        PaddleOcrModelBundle model = parsed.Model.ToLowerInvariant() switch
        {
            "small" => ChineseV6SmallModels.Default,
            _ => ChineseV6TinyModels.Default
        };
        if (settings.Layout == OcrLayout.SingleLine)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Stream recognition = model.Recognition.OpenRead();
            using Stream dictionary = model.Dictionary.OpenRead();
            var recognizer = new PaddleOcrRecognizer(recognition, dictionary, parsed.ToEngineOptions().Recognizer);
            return new SimdPaddleOcrReader(parsed, settings.Layout, all: null, recognizer);
        }

        PaddleOcrAll all = await PaddleOcrAll.LoadAsync(model, parsed.ToEngineOptions(), cancellationToken).ConfigureAwait(false);
        return new SimdPaddleOcrReader(parsed, settings.Layout, all, recognizer: null);
    }

    internal readonly record struct ParsedOcrOptions(
        string Model,
        int DetectSideLength,
        bool UseDirectionClassification,
        int LineWorkerCount,
        int DetIntraOpThreads,
        int RecBatchLines,
        int MaxPooledSessions,
        int MinConfidencePercent)
    {
        public static ParsedOcrOptions FromSettings(OcrSettings settings)
        {
            ValidateLanguages(settings.Languages);
            var values = Defaults();
            JsonElement options = settings.ProviderOptions;
            if (options.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in options.EnumerateObject())
                {
                    if (!values.ContainsKey(property.Name))
                        throw new NotSupportedException($"不支持的 OCR 参数：{property.Name}。");
                    values[property.Name] = ReadJsonValue(property);
                }
            }
            else if (options.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            {
                throw new ArgumentException("ProviderOptions 必须为 JSON 对象。", nameof(settings));
            }

            if (!AlgorithmParameterValidator.Validate(SimdPaddleOcrMetadata.Parameters, values, out string? error))
                throw new ArgumentException(error, nameof(settings));

            return new(
                Model: values["model"],
                DetectSideLength: int.Parse(values["detectSideLength"], CultureInfo.InvariantCulture),
                UseDirectionClassification: bool.Parse(values["useDirectionClassification"]),
                LineWorkerCount: int.Parse(values["lineWorkerCount"], CultureInfo.InvariantCulture),
                DetIntraOpThreads: int.Parse(values["detIntraOpThreads"], CultureInfo.InvariantCulture),
                RecBatchLines: int.Parse(values["recBatchLines"], CultureInfo.InvariantCulture),
                MaxPooledSessions: int.Parse(values["maxPooledSessions"], CultureInfo.InvariantCulture),
                MinConfidencePercent: int.Parse(values["minConfidencePercent"], CultureInfo.InvariantCulture));
        }

        public PaddleOcrOptions ToEngineOptions() => new()
        {
            UseDirectionClassification = UseDirectionClassification,
            LineWorkerCount = LineWorkerCount,
            DetIntraOpThreads = DetIntraOpThreads,
            RecBatchLines = RecBatchLines,
            Detector = new PaddleOcrDetectorOptions
            {
                LimitSideLength = DetectSideLength,
                MaxPooledSessions = MaxPooledSessions
            },
            Classifier = new PaddleOcrClassifierOptions { MaxPooledSessions = MaxPooledSessions },
            Recognizer = new PaddleOcrRecognizerOptions { MaxPooledSessions = MaxPooledSessions }
        };

        private static Dictionary<string, string> Defaults()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in SimdPaddleOcrMetadata.Parameters)
                values[parameter.Key] = parameter.DefaultValue;
            return values;
        }

        private static string ReadJsonValue(JsonProperty property) => property.Value.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => property.Value.TryGetInt32(out int number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : throw new ArgumentException($"参数 {property.Name} 必须为整数。"),
            JsonValueKind.String => property.Value.GetString() ?? "",
            _ => throw new ArgumentException($"参数 {property.Name} 的 JSON 格式无效。")
        };

        private static void ValidateLanguages(ImmutableArray<string> languages)
        {
            if (languages.IsDefaultOrEmpty) return;
            foreach (string language in languages)
            {
                if (language is "zh" or "zh-CN" or "chi" or "zh-Hans") continue;
                throw new NotSupportedException($"当前 PP-OCRv6 中文模型不支持语言 {language}。");
            }
        }
    }

    private sealed class SimdPaddleOcrReader : IOcrReader
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ParsedOcrOptions _options;
        private readonly OcrLayout _layout;
        private PaddleOcrAll? _all;
        private PaddleOcrRecognizer? _recognizer;
        private bool _disposed;

        public EngineDescriptor Descriptor { get; } = new(
            SimdPaddleOcrMetadata.ProviderId,
            SimdPaddleOcrMetadata.EngineVersion,
            [PixelFormat.Bgr24],
            SupportsStridedInput: true,
            SupportsNativeTimeout: false,
            SupportsCooperativeStop: false,
            MaxInstances: 1);

        internal SimdPaddleOcrReader(ParsedOcrOptions options, OcrLayout layout, PaddleOcrAll? all, PaddleOcrRecognizer? recognizer)
        {
            _options = options;
            _layout = layout;
            _all = all;
            _recognizer = recognizer;
        }

        public async ValueTask<EngineBatch<OcrLine>> ReadAsync(ImageInput input, RecognitionRequest request, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ValidateBgr(input);
                if (cancellationToken.IsCancellationRequested)
                    return Empty(request.Analysis, StageStatus.Cancelled, TimeSpan.Zero);
                if (Stopwatch.GetTimestamp() >= request.DeadlineTimestamp)
                    return Empty(request.Analysis, StageStatus.TimedOut, TimeSpan.Zero);

                var plane = input.Layout.Planes[0];
                int needed = checked(plane.Offset + plane.StrideBytes * (input.Layout.Height - 1) + input.Layout.Width * 3);
                if (needed > input.Buffer.Length) throw new ArgumentException("BGR24 缓冲长度不足。", nameof(input));
                ReadOnlySpan<byte> pixels = input.Buffer.Span.Slice(plane.Offset, needed - plane.Offset);

                var watch = Stopwatch.StartNew();
                ImmutableArray<OcrLine> lines;
                try
                {
                    lines = _layout == OcrLayout.SingleLine
                        ? RecognizeLine(pixels, input.Layout.Width, input.Layout.Height, plane.StrideBytes)
                        : RecognizeBlock(pixels, input.Layout.Width, input.Layout.Height, plane.StrideBytes);
                }
                catch (Exception error) when (error is not ArgumentException and not ObjectDisposedException)
                {
                    return new EngineBatch<OcrLine>(request.Analysis, StageStatus.Faulted, [], watch.Elapsed,
                        new StageFault("ocr-failed", error.Message, null));
                }
                watch.Stop();

                StageStatus status = cancellationToken.IsCancellationRequested
                    ? StageStatus.Cancelled
                    : Stopwatch.GetTimestamp() >= request.DeadlineTimestamp ? StageStatus.TimedOut : StageStatus.Completed;
                return new EngineBatch<OcrLine>(request.Analysis, status, lines, watch.Elapsed, null);
            }
            finally
            {
                _gate.Release();
            }
        }

        private ImmutableArray<OcrLine> RecognizeBlock(ReadOnlySpan<byte> pixels, int width, int height, int stride)
        {
            PaddleOcrAll all = _all ?? throw new InvalidOperationException("全页 OCR 实例未加载。");
            PaddleOcrResult result = all.Run(pixels, width, height, stride);
            var lines = ImmutableArray.CreateBuilder<OcrLine>(result.Lines.Length);
            foreach (PaddleOcrLine line in result.Lines)
            {
                double? confidence = MapConfidence(line.RecognitionScore, line.EmittedCount);
                if (!PassesConfidence(confidence)) continue;
                var box = line.Box;
                var quad = new Quad(new(box.X1, box.Y1), new(box.X2, box.Y2), new(box.X3, box.Y3), new(box.X4, box.Y4));
                lines.Add(new OcrLine(
                    line.Text,
                    quad,
                    confidence,
                    Language: null,
                    Words: [],
                    ReadingAngleDegrees: QuadReadingAxis.Degrees(quad, line.AppliedRotationDegrees)));
            }
            return lines.ToImmutable();
        }

        private ImmutableArray<OcrLine> RecognizeLine(ReadOnlySpan<byte> pixels, int width, int height, int stride)
        {
            PaddleOcrRecognizer recognizer = _recognizer ?? throw new InvalidOperationException("单行 OCR 实例未加载。");
            PaddleOcrRecognitionResult result = recognizer.Recognize(pixels, width, height, stride);
            double? confidence = MapConfidence(result.Score, (uint)result.EmittedCount);
            if (string.IsNullOrWhiteSpace(result.Text) || !PassesConfidence(confidence))
                return [];
            var quad = new Quad(new(0, 0), new(width, 0), new(width, height), new(0, height));
            return
            [
                new OcrLine(
                    result.Text,
                    quad,
                    confidence,
                    Language: null,
                    Words: [],
                    // Direct REC receives the input unchanged: no crop rotation or CLS.
                    ReadingAngleDegrees: 0)
            ];
        }

        private bool PassesConfidence(double? confidence) =>
            _options.MinConfidencePercent <= 0 ||
            confidence is null ||
            confidence.Value * 100 >= _options.MinConfidencePercent;

        private static double? MapConfidence(float score, uint emittedCount)
        {
            if (float.IsNaN(score) || float.IsInfinity(score)) return null;
            if (score >= 0f && score <= 1f) return score;
            if (emittedCount > 0)
            {
                float avg = score / emittedCount;
                if (avg >= 0f && avg <= 1f) return avg;
                // Logit to sigmoid probability: 1 / (1 + exp(-avg))
                float prob = 1f / (1f + MathF.Exp(-avg));
                return Math.Clamp(prob, 0f, 1f);
            }
            if (score > 1f && score <= 100f) return score / 100.0;
            return null;
        }

        private static EngineBatch<OcrLine> Empty(AnalysisId analysis, StageStatus status, TimeSpan elapsed) =>
            new(analysis, status, [], elapsed, null);

        private static void ValidateBgr(ImageInput input)
        {
            var layout = input.Layout;
            if (layout.Encoding != FrameEncoding.Raw || layout.PixelFormat != PixelFormat.Bgr24 ||
                layout.Width < 1 || layout.Height < 1 || layout.Planes.Length != 1)
                throw new ArgumentException("OCR 需要 BGR24 图像。", nameof(input));
            var plane = layout.Planes[0];
            long row = (long)layout.Width * 3;
            long end = (long)plane.Offset + (long)(layout.Height - 1) * plane.StrideBytes;
            if (plane.Height < layout.Height || plane.RowBytes < row || Math.Abs((long)plane.StrideBytes) < row ||
                Math.Min(plane.Offset, end) < 0 || Math.Max(plane.Offset, end) + row > input.Buffer.Length)
                throw new ArgumentException("BGR24 图像布局无效。", nameof(input));
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                _disposed = true;
                _all?.Dispose();
                _all = null;
                _recognizer?.Dispose();
                _recognizer = null;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
