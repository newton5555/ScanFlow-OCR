using System.Buffers;
using System.Runtime.InteropServices;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Imaging;

// Bounded image storage. The allocator accounts for both checked-out and idle
// native blocks, so the pool can reuse stable camera/preview sizes without
// allowing its committed footprint to grow past the session limit.
public sealed class ImageAllocator : IDisposable
{
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

        NativeImageMemory bytes;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_idle.TryGetValue(length, out var bucket) || bucket.Count == 0)
            {
                if (_committed > _byteLimit - length)
                    throw new InvalidOperationException("图像缓冲区已达到内存上限。");
                bytes = new NativeImageMemory(length);
                _committed += length;
            }
            else
            {
                bytes = bucket.Pop();
                if (bucket.Count == 0) _idle.Remove(length);
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

    // Stop/reconfigure calls this after all internal queues have been drained.
    // Leases still held by a consumer are released normally and can be trimmed
    // by the next stop or allocator disposal.
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
                if (!_idle.TryGetValue(bytes.Capacity, out var bucket))
                    _idle.Add(bytes.Capacity, bucket = []);
                bucket.Push(bytes);
            }
        }
        if (dispose) ((IDisposable)bytes).Dispose();
    }
}

// Exact-sized native blocks owned by ImageAllocator; blocks are reusable only
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
