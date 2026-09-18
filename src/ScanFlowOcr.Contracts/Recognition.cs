using System.Collections.Immutable;
using System.Text.Json;

namespace ScanFlowOcr.Contracts;

public enum StageStatus { NotRun, Completed, TimedOut, Cancelled, Faulted }
public enum ObservationCompleteness { Complete, Partial }
public enum OcrLayout { SingleLine, TextBlock, SparseText }

public sealed record EngineDescriptor(
    string ProviderId, string Version, ImmutableArray<PixelFormat> InputFormats,
    bool SupportsStridedInput, bool SupportsNativeTimeout, bool SupportsCooperativeStop,
    int? MaxInstances);

// DeadlineTimestamp is an absolute Stopwatch timestamp within the host process.
// A budget/cancellation request does not guarantee interruption of native execution.
public readonly record struct RecognitionRequest(AnalysisId Analysis, long DeadlineTimestamp);
public sealed record StageFault(string Code, string Message, string? NativeCode);

// Engine coordinates are relative to ImageInput, including any crop/rotation.
// Runtime maps them through ImageToSource exactly once before publishing ScanAnalysis.
// Results own their small data and never reference native callback memory or image pixels.
public sealed record EngineBatch<T>(
    AnalysisId Analysis, StageStatus Status, ImmutableArray<T> Items,
    TimeSpan EngineTime, StageFault? Fault);


public sealed record OcrWord(string Text, Quad Bounds, double? Confidence);
public sealed record OcrLine(
    string Text, Quad Bounds, double? Confidence, string? Language,
    ImmutableArray<OcrWord> Words);

// Common parameters are typed. Vendor options are validated once during creation.
// JsonElement must be cloned/owned independently of a disposed JsonDocument.
public sealed record OcrSettings(
    ImmutableArray<string> Languages, OcrLayout Layout, JsonElement ProviderOptions);

// One concurrent request per reader instance. Runtime owns parallelism.
// No implementation may complete the returned operation while native code still uses input.
// No native callback may outlive disposal or unwind a managed exception into native code.

public interface IOcrReader : IAsyncDisposable
{
    EngineDescriptor Descriptor { get; }
    ValueTask<EngineBatch<OcrLine>> ReadAsync(
        ImageInput input, RecognitionRequest request, CancellationToken cancellationToken);
}

public interface IAlgorithmParameterInspector
{
    int ReadIntSetting(int tag);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Parameter types model metadata primitives.")]
public enum AlgorithmParameterType { Boolean, Integer, Choice, String }

public sealed record ParameterChoiceOption(
    string Value,
    string DisplayName,
    string? Description = null,
    int? IntValue = null);

public sealed record AlgorithmParameterDescriptor(
    string Key,
    string DisplayName,
    AlgorithmParameterType Type,
    string Category,
    string Description,
    string DefaultValue,
    bool IsAdvanced = false,
    int? MinIntValue = null,
    int? MaxIntValue = null,
    ImmutableArray<ParameterChoiceOption> Options = default,
    string? DependsOnKey = null,
    string? DependsOnValue = null);

// Providers share process-wide native initialization, not mutable decoder instances.

public interface IOcrReaderFactory
{
    string ProviderId { get; }
    ImmutableArray<AlgorithmParameterDescriptor> Parameters => [];
    ValueTask<IOcrReader> CreateAsync(OcrSettings settings, CancellationToken cancellationToken);
}

// All coordinates below are in original source pixels, NOT preview coordinates.
// Completed + empty Items is a valid no-read; Faulted/NotRun is not a no-read.
public sealed record StageAnalysis<T>(
    StageStatus Status, ImmutableArray<T> Items, TimeSpan EngineTime, StageFault? Fault);
public sealed record ScanAnalysis(
    AnalysisId Id, FrameStamp Frame, long CompletedTimestamp,
    StageAnalysis<OcrLine> Ocr);
