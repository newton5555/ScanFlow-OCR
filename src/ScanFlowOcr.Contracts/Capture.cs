using System.Collections.Immutable;

namespace ScanFlowOcr.Contracts;

public readonly record struct CameraId(string ProviderId, string DeviceKey);
public enum DeviceIdentityKind { SerialNumber, ConnectionPath, BackendId }
public enum SourceState { Open, Starting, Running, Stopping, Stopped, Faulted, Disposed }

public sealed record CameraDescriptor(
    CameraId Id, string DisplayName, DeviceIdentityKind IdentityKind,
    string? SerialNumber, string? ConnectionPath);

// ModeId maps to an actually enumerated backend mode, never a guessed list index.
public sealed record CaptureMode(
    string ModeId, int Width, int Height, int FpsNumerator, int FpsDenominator,
    FrameEncoding Encoding, PixelFormat PixelFormat);

public sealed record CameraOpenOptions(CameraId Device, string ModeId);
public sealed record CaptureFault(string Code, string Message, bool CanReconnect);

public interface ICameraProvider
{
    string Id { get; }
    ValueTask<ImmutableArray<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken);
    ValueTask<ImmutableArray<CaptureMode>> GetModesAsync(CameraId device, CancellationToken cancellationToken);
    ValueTask<ICameraSession> OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken);
}

// Runtime-owned receiver: short synchronous validation/copy/enqueue only.
// OnFault can race with OnFrame. Neither may throw across a native callback boundary.
public interface IFrameReceiver
{
    void OnFrame(in CapturedFrame frame);
    void OnFault(CaptureFault fault);
}

// A source has one receiver. Camera and file replay use the same source contract.
// Frame callbacks from one source must be serialized; no async-void callbacks.
public interface IFrameSource : IAsyncDisposable
{
    string SourceId { get; }
    SourceState State { get; }
    ValueTask StartAsync(Guid streamEpoch, IFrameReceiver receiver, CancellationToken cancellationToken);
    // Successful completion means all callbacks have finished and no more can begin.
    // Cancelling the caller's wait does not authorize release of active native resources.
    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface ICameraSession : IFrameSource
{
    CameraDescriptor Device { get; }
    CaptureMode NegotiatedMode { get; }
    ICameraControls? Controls { get; }
}

public enum CameraControlMode { Manual, Auto }
public sealed record CameraControlDescriptor(
    string Id, string DisplayName, string Unit, double Min, double Max, double Step,
    ImmutableArray<CameraControlMode> Modes, bool CanChangeWhileRunning);
public readonly record struct CameraControlValue(double Value, CameraControlMode Mode);

// Stable ids such as exposure-time and gain. Vendor extensions are namespaced.
// Report native units if conversion to physical units is unknown; never invent units.
public interface ICameraControls
{
    ValueTask<ImmutableArray<CameraControlDescriptor>> DescribeAsync(CancellationToken cancellationToken);
    ValueTask<CameraControlValue> GetAsync(string id, CancellationToken cancellationToken);
    // Returns read-back/effective value. Unsupported controls fail explicitly.
    ValueTask<CameraControlValue> SetAsync(string id, CameraControlValue value, CancellationToken cancellationToken);
}
