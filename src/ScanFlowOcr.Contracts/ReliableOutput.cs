using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace ScanFlowOcr.Contracts;

// Business admission is synchronous with deduplication, before lossy UI events.
public interface IRecordDelivery
{
    ValueTask<bool> TryAcceptAsync(ScanRecord record, CancellationToken cancellationToken);
    OutputQueueStatus GetStatus();
}

public sealed record OutputQueueStatus(bool Enabled, int PendingCount, int Capacity, bool AdmissionPaused, string? LastError, int DeliveredCount = 0, int UncertainCount = 0, string? LastReceiptCode = null, ImmutableArray<OutputRouteStatus> Routes = default);
public sealed record OutputRouteStatus(string SinkId, bool Enabled, int PendingCount, int DeliveredCount, int UncertainCount, int Capacity);
public sealed record OutputAdmissionWarning(long Timestamp, string Message) : SessionEvent(Timestamp);
