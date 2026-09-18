using System.Collections.Immutable;

namespace ScanFlowOcr.Contracts;

// Source bytes/text are preserved in observations; derived business fields are separate.
public sealed record ScanRecord(
    Guid EventId, AnalysisId Analysis, FrameStamp Frame, DateTimeOffset CreatedUtc,
    ImmutableArray<OcrLine> TextLines,
    ImmutableDictionary<string, string> Fields);

public enum DeliveryDisposition
{
    LocallyAccepted,      // Local write completed; receiver processing is NOT confirmed.
    Acknowledged,        // Protocol/application explicitly acknowledged this EventId.
    NotDelivered,        // Known that no external delivery happened; retry may be considered.
    Unknown              // A side effect may have happened; never blindly retry.
}

public sealed record OutputSinkDescriptor(
    string Id, bool SupportsAcknowledgement, bool SupportsIdempotencyKey);

// The output formatter creates payload bytes once. Image buffers never enter this envelope.
// Attempt counts retries of the SAME EventId; it does not generate a new business event.
public sealed record OutputMessage(
    ScanRecord Record, string ContentType, ImmutableArray<byte> Payload, int Attempt);

public sealed record DeliveryReceipt(
    Guid EventId, string SinkId, DeliveryDisposition Disposition, string? Code, string? Message);

// One ordered sender loop per sink. Cancellation does not prove the side effect was undone.
// A sink never calls capture/recognition/UI. Factories/DI create initialized sinks.
public interface IOutputSink : IAsyncDisposable
{
    OutputSinkDescriptor Descriptor { get; }
    ValueTask<DeliveryReceipt> SendAsync(OutputMessage message, CancellationToken cancellationToken);
}
