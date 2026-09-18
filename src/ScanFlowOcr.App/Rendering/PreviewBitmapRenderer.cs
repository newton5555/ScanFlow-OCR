using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ScanFlowOcr.Contracts;
using Contracts = ScanFlowOcr.Contracts;

namespace ScanFlowOcr.App.Rendering;

/// <summary>BGRA32 / BGR24 raw lease → Avalonia WriteableBitmap (call on UI thread).</summary>
public sealed class PreviewBitmapRenderer : IDisposable
{
    private WriteableBitmap? _bitmap;
    private byte[]? _rowScratch;

    public WriteableBitmap? Bitmap => _bitmap;

    public bool TryRender(IImageLease lease, out WriteableBitmap? bitmap)
    {
        bitmap = null;
        var input = lease.Input;
        var layout = input.Layout;
        if (layout.Encoding != FrameEncoding.Raw || layout.Planes.IsDefaultOrEmpty)
            return false;

        int width = layout.Width;
        int height = layout.Height;
        if (width <= 0 || height <= 0) return false;

        int bpp = layout.PixelFormat switch
        {
            Contracts.PixelFormat.Bgra32 => 4,
            Contracts.PixelFormat.Bgr24 => 3,
            _ => 0
        };
        if (bpp == 0) return false;

        var plane = layout.Planes[0];
        int destStride = checked(width * 4);
        if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormats.Bgra8888,
                AlphaFormat.Opaque);
        }

        if (_rowScratch is null || _rowScratch.Length < destStride)
            _rowScratch = new byte[destStride];

        var src = input.Buffer.Span;
        using (var fb = _bitmap.Lock())
        {
            for (int y = 0; y < height; y++)
            {
                int srcOffset = plane.Offset + y * plane.StrideBytes;
                if (bpp == 4)
                {
                    src.Slice(srcOffset, destStride).CopyTo(_rowScratch.AsSpan(0, destStride));
                }
                else
                {
                    var row = src.Slice(srcOffset, width * 3);
                    for (int x = 0; x < width; x++)
                    {
                        _rowScratch[x * 4] = row[x * 3];
                        _rowScratch[x * 4 + 1] = row[x * 3 + 1];
                        _rowScratch[x * 4 + 2] = row[x * 3 + 2];
                        _rowScratch[x * 4 + 3] = 255;
                    }
                }
                Marshal.Copy(_rowScratch, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), destStride);
            }
        }

        bitmap = _bitmap;
        return true;
    }

    public void Reset()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }

    public void Dispose() => Reset();
}
