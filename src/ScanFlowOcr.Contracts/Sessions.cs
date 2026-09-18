using System.Collections.Immutable;
using System.Text.Json;

namespace ScanFlowOcr.Contracts;

public enum WorkflowMode { OcrOnly }
public enum SessionState { Created, Starting, Running, Reconfiguring, Reconnecting, Stopping, Stopped, Faulted, Disposed }
public enum DedupeMode { Cooldown, UntilAbsent, Session, IntraFrame }

public abstract record SourceProfile;
public sealed record CameraSourceProfile(CameraOpenOptions Options) : SourceProfile;
public sealed record ReplaySourceProfile(string ManifestPath, bool Loop) : SourceProfile;

public sealed record OcrStageProfile(string ProviderId, OcrSettings Settings, Rect2? Region);

// Bounds apply to retained frames/bytes, not only to items inside a channel.
// Phase 1 uses a bounded pending-frame queue and one in-flight OCR request.
public sealed record SchedulingProfile(
    int PendingFrames, TimeSpan MaxQueueAge, TimeSpan MaxResultAge,
    TimeSpan OcrBudget, long MaxFrameBytes, long RetainedByteLimit);
public sealed record PreviewProfile(bool Enabled, int MaxFps, int MaxWidth, int MaxHeight);
public sealed record DedupeProfile(DedupeMode Mode, TimeSpan Interval, int MaxEntries);
public sealed record OutputRouteProfile(
    string SinkId, bool Required, int Capacity, int MaxPayloadBytes, JsonElement ProviderOptions);

// Configuration snapshots are immutable. Mode/engine/ROI changes use a revision barrier.
// Rejects OCR mode if no provider is registered, before opening the camera.
public sealed record SessionProfile(
    int SchemaVersion, SourceProfile Source, WorkflowMode Workflow,
    OcrStageProfile? Ocr,
    SchedulingProfile Scheduling, PreviewProfile Preview, DedupeProfile Dedupe,
    ImmutableArray<OutputRouteProfile> Outputs);

public sealed record OutputRouteSnapshot(
    string SinkId, int PendingCount, bool AdmissionPaused, DeliveryReceipt? LastReceipt);
public sealed record SessionSnapshot(
    SessionState State, SessionProfile Profile, CaptureMode? NegotiatedCaptureMode,
    Guid? StreamEpoch, long ProfileRevision, string? FaultCode,
    long FramesReceived, long FramesDropped, long LiveFrameBytes,
    ImmutableArray<OutputRouteSnapshot> Outputs);

// Events are UI/diagnostic notifications, not a reliable business output channel.
public abstract record SessionEvent(long Timestamp);
public sealed record StateChanged(long Timestamp, SessionState State, string? Reason) : SessionEvent(Timestamp);
public sealed record AnalysisReady(long Timestamp, ScanAnalysis Analysis) : SessionEvent(Timestamp);
public sealed record ScanRecordReady(long Timestamp, ScanRecord Record) : SessionEvent(Timestamp);
public sealed record OutputCompleted(long Timestamp, DeliveryReceipt Receipt) : SessionEvent(Timestamp);
public sealed record EventsSkipped(long Timestamp, long Count) : SessionEvent(Timestamp);
public sealed record DedupeOverflowWarning(long Timestamp, string Message) : SessionEvent(Timestamp);

// Created by composition with concrete capture, OCR and output dependencies.
// Implementations serialize lifecycle calls; no mutable process-wide camera singleton.
public interface IScanSession : IAsyncDisposable
{
    SessionSnapshot GetSnapshot();
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
    ValueTask ReconfigureAsync(SessionProfile profile, CancellationToken cancellationToken);
    ValueTask ClearHistoryAsync(CancellationToken cancellationToken);

    // One reader each in P1. Cancelling a reader unsubscribes, not stops the session.
    IAsyncEnumerable<SessionEvent> ReadEventsAsync(CancellationToken cancellationToken);
    // Each yielded lease is owned by the consumer and must be disposed promptly.
    // Already yielded leases remain valid until disposed, even after session Stop.
    IAsyncEnumerable<IImageLease> ReadPreviewAsync(CancellationToken cancellationToken);
}
