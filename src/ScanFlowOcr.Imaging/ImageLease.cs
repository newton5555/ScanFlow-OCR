using System.Buffers;
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
        byte[] bytes;
        try
        {
            bytes = ArrayPool<byte>.Shared.Rent(length);
        }
        catch
        {
            Interlocked.Add(ref _live, -length);
            throw;
        }
        try { return new ImageLease(new ImageLease.Storage(bytes, length, () => { ArrayPool<byte>.Shared.Return(bytes); Interlocked.Add(ref _live, -length); }), stamp, layout, transform); }
        catch { ArrayPool<byte>.Shared.Return(bytes); Interlocked.Add(ref _live, -length); throw; }
    }
}

public sealed class ImageLease : IImageLease
{
    internal sealed class Storage(byte[] bytes, int length, Action release)
    {
        internal readonly byte[] Bytes = bytes;
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
    public Memory<byte> WritableBuffer { get { lock (_gate) { var storage = _storage ?? throw new ObjectDisposedException(nameof(ImageLease)); return storage.Bytes.AsMemory(0, storage.Length); } } }
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
