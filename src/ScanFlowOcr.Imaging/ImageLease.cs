using System.Buffers;
using System.Runtime.InteropServices;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Imaging;

// Bounded live allocation, including buffers retained by a UI consumer.
public sealed class ImageAllocator(long byteLimit)
{
    private long _live;
    public long LiveBytes => Interlocked.Read(ref _live);
    public ImageLease Allocate(FrameStamp stamp, ImageLayout layout, int length, ImageTransform transform)
    {
        if (length <= 0 || length > byteLimit) throw new InvalidOperationException("图像超过缓冲区限制。");
        if (Interlocked.Add(ref _live, length) > byteLimit)
        {
            Interlocked.Add(ref _live, -length);
            throw new InvalidOperationException("图像缓冲区已达到内存上限。");
        }
        NativeImageMemory bytes;
        try
        {
            bytes = new NativeImageMemory(length);
        }
        catch
        {
            Interlocked.Add(ref _live, -length);
            throw;
        }
        try { return new ImageLease(new ImageLease.Storage(bytes.Memory, length, () => { ((IDisposable)bytes).Dispose(); Interlocked.Add(ref _live, -length); }), stamp, layout, transform); }
        catch { ((IDisposable)bytes).Dispose(); Interlocked.Add(ref _live, -length); throw; }
    }
}

// Exact-sized pixel storage: no global ArrayPool bucket retention after a lease ends.
internal sealed unsafe class NativeImageMemory : MemoryManager<byte>
{
    private byte* _pointer;
    private readonly int _length;
    public NativeImageMemory(int length)
    {
        _pointer = (byte*)NativeMemory.Alloc((nuint)length);
        _length = length;
        GC.AddMemoryPressure(length);
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
        GC.RemoveMemoryPressure(_length);
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
