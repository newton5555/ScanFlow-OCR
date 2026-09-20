using System.Buffers;
using System.Runtime.InteropServices;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Imaging;

// Bounded image storage. The allocator accounts for both checked-out and idle
// native blocks, so the pool can reuse stable camera/preview sizes without
// allowing its committed footprint to grow past the session limit.
//
// Idle buckets are keyed by *capacity* (not the caller's length). Allocate
// rounds the requested length up to a size class (next power of two, minimum
// 64 KiB for large frames; smaller requests still use the next power of two)
// so variable JPEG sizes reuse the same idle blocks. The lease still exposes
// only `length` bytes to callers via Memory.Slice.
public sealed class ImageAllocator : IDisposable
{
    private const int MinSizeClass = 64 * 1024;
    private readonly object _gate = new();
    private readonly long _byteLimit;
    private readonly Dictionary<int, Stack<NativeImageMemory>> _idle = [];
    private long _live;
    private long _committed;
    private bool _disposed;

    public ImageAllocator(long byteLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLimit);
        _byteLimit = byteLimit;
    }

    public long LiveBytes => Interlocked.Read(ref _live);

    // Actual native bytes owned by this allocator, including idle reusable blocks.
    public long CommittedBytes => Interlocked.Read(ref _committed);

    public long IdleBytes => Math.Max(0, CommittedBytes - LiveBytes);

    public ImageLease Allocate(FrameStamp stamp, ImageLayout layout, int length, ImageTransform transform)
    {
        if (length <= 0 || length > _byteLimit) throw new InvalidOperationException("图像超过缓冲区限制。");

        int capacity = RoundUpCapacity(length);
        // If the size class would exceed the session limit, fall back to exact length
        // so a single near-limit frame can still allocate.
        if (capacity > _byteLimit) capacity = length;

        NativeImageMemory bytes;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_idle.TryGetValue(capacity, out var bucket) || bucket.Count == 0)
            {
                if (_committed > _byteLimit - capacity)
                    throw new InvalidOperationException("图像缓冲区已达到内存上限。");
                bytes = new NativeImageMemory(capacity);
                _committed += capacity;
            }
            else
            {
                bytes = bucket.Pop();
                if (bucket.Count == 0) _idle.Remove(capacity);
            }
            _live += length;
        }

        try
        {
            var storage = new ImageLease.Storage(
                bytes.Memory.Slice(0, length), length,
                () => Release(bytes, length));
            return new ImageLease(storage, stamp, layout, transform);
        }
        catch
        {
            Release(bytes, length);
            throw;
        }
    }

    // Round requested length up to a reusable size class.
    // Below 64 KiB: next power of two (keeps smoke/tiny buffers clustered).
    // At/above 64 KiB: next power of two from 64 KiB upward (MJPEG jitter reuses).
    internal static int RoundUpCapacity(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        int rounded = NextPowerOfTwo(length);
        if (length >= MinSizeClass)
            return Math.Max(rounded, MinSizeClass);
        return rounded;
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1) return 1;
        uint v = (uint)value - 1;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        v++;
        if (v > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        return (int)v;
    }

    // Stop/reconfigure (and periodic idle-threshold trim during continuous scan)
    // call this after queues are drained / between frames. Leases still held by a
    // consumer are released normally and return to idle for the next trim.
    public void TrimExcess()
    {
        List<NativeImageMemory> blocks;
        lock (_gate)
        {
            blocks = TakeIdleBlocks();
        }
        foreach (var block in blocks)
            ((IDisposable)block).Dispose();
    }

    public void Dispose()
    {
        List<NativeImageMemory> blocks;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            blocks = TakeIdleBlocks();
        }
        foreach (var block in blocks)
            ((IDisposable)block).Dispose();
    }

    private List<NativeImageMemory> TakeIdleBlocks()
    {
        var blocks = new List<NativeImageMemory>();
        foreach (var bucket in _idle.Values)
            blocks.AddRange(bucket);
        _idle.Clear();
        foreach (var block in blocks)
            _committed -= block.Capacity;
        return blocks;
    }

    private void Release(NativeImageMemory bytes, int length)
    {
        bool dispose;
        lock (_gate)
        {
            _live -= length;
            dispose = _disposed;
            if (dispose)
            {
                _committed -= bytes.Capacity;
            }
            else
            {
                // Key by capacity so size-class peers share one idle bucket.
                if (!_idle.TryGetValue(bytes.Capacity, out var bucket))
                    _idle.Add(bytes.Capacity, bucket = []);
                bucket.Push(bytes);
            }
        }
        if (dispose) ((IDisposable)bytes).Dispose();
    }
}

// Size-class native blocks owned by ImageAllocator; blocks are reusable only
// inside that allocator and are never retained by the process-wide ArrayPool.
internal sealed unsafe class NativeImageMemory : MemoryManager<byte>
{
    private byte* _pointer;
    private readonly int _length;
    public int Capacity => _length;

    public NativeImageMemory(int length)
    {
        _pointer = (byte*)NativeMemory.Alloc((nuint)length);
        _length = length;
        GC.AddMemoryPressure((long)length);
    }
    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_pointer == null, this);
        return new Span<byte>(_pointer, _length);
    }
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_pointer == null, this);
        if ((uint)elementIndex > (uint)_length) throw new ArgumentOutOfRangeException(nameof(elementIndex));
        return new MemoryHandle(_pointer + elementIndex);
    }
    public override void Unpin() { }
    protected override void Dispose(bool disposing)
    {
        if (_pointer == null) return;
        NativeMemory.Free(_pointer);
        _pointer = null;
        GC.RemoveMemoryPressure((long)_length);
    }
}

public sealed class ImageLease : IImageLease
{
    internal sealed class Storage(Memory<byte> bytes, int length, Action release)
    {
        internal readonly Memory<byte> Bytes = bytes;
        internal readonly int Length = length;
        internal int References = 1;
        internal void Release() { if (Interlocked.Decrement(ref References) == 0) release(); }
    }
    private readonly object _gate = new();
    private Storage? _storage;
    private readonly FrameStamp _stamp;
    private readonly ImageLayout _layout;
    private readonly ImageTransform _transform;
    internal ImageLease(Storage storage, FrameStamp stamp, ImageLayout layout, ImageTransform transform)
    { _storage = storage; _stamp = stamp; _layout = layout; _transform = transform; }
    public Memory<byte> WritableBuffer { get { lock (_gate) { var storage = _storage ?? throw new ObjectDisposedException(nameof(ImageLease)); return storage.Bytes.Slice(0, storage.Length); } } }
    public ImageInput Input { get { lock (_gate) return new(_stamp, _layout, WritableBuffer, _transform); } }
    public IImageLease Retain()
    {
        lock (_gate)
        {
            var storage = _storage ?? throw new ObjectDisposedException(nameof(ImageLease));
            Interlocked.Increment(ref storage.References);
            return new ImageLease(storage, _stamp, _layout, _transform);
        }
    }
    public void Dispose() { lock (_gate) { _storage?.Release(); _storage = null; } }
}
