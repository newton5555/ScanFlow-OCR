using System.Runtime.InteropServices;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Imaging;

// One decoder per worker. The native handle never crosses a concurrent call.
public sealed unsafe class JpegDecoder : IDisposable
{
    private readonly nint _library;
    private nint _handle;
    private readonly delegate* unmanaged[Cdecl]<nint> _init;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _destroy;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, nuint, int*, int*, int*, int*, int> _header;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, nuint, byte*, int, int, int, int, int, int> _decode;
    public JpegDecoder(string libraryPath)
    {
        _library = NativeLibrary.Load(libraryPath);
        try
        {
            _init = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(_library, "tjInitDecompress");
            _destroy = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_library, "tjDestroy");
            _header = (delegate* unmanaged[Cdecl]<nint, byte*, nuint, int*, int*, int*, int*, int>)NativeLibrary.GetExport(_library, "tjDecompressHeader3");
            _decode = (delegate* unmanaged[Cdecl]<nint, byte*, nuint, byte*, int, int, int, int, int, int>)NativeLibrary.GetExport(_library, "tjDecompress2");
            _handle = _init();
            if (_handle == 0) throw new InvalidOperationException("libjpeg-turbo 初始化失败。");
        }
        catch { NativeLibrary.Free(_library); throw; }
    }
    public ImageLease DecodeGray(ImageInput jpeg, ImageAllocator allocator)
        => Decode(jpeg, allocator, PixelFormat.Gray8);
    public ImageLease DecodeBgra(ImageInput jpeg, ImageAllocator allocator)
        => Decode(jpeg, allocator, PixelFormat.Bgra32);
    public ImageLease DecodePreview(ImageInput jpeg, ImageAllocator allocator, int maxWidth, int maxHeight)
        => Decode(jpeg, allocator, PixelFormat.Bgra32, maxWidth, maxHeight);
    public ImageLease DecodeBgr(ImageInput jpeg, ImageAllocator allocator)
        => Decode(jpeg, allocator, PixelFormat.Bgr24);
    public int DecodeBgrInto(ImageInput jpeg, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (jpeg.Layout.Encoding != FrameEncoding.Jpeg)
            throw new ArgumentException("需要 JPEG 输入。", nameof(jpeg));
        fixed (byte* input = jpeg.Buffer.Span)
        {
            int w = 0, h = 0, sub = 0, color = 0;
            if (_header(_handle, input, (nuint)jpeg.Buffer.Length, &w, &h, &sub, &color) != 0 || w <= 0 || h <= 0)
                throw new InvalidDataException("JPEG 头部无效。");
            if (w != jpeg.Layout.Width || h != jpeg.Layout.Height)
                throw new InvalidDataException("JPEG 尺寸与相机协商模式不一致。");
            int packed = checked(w * 3);
            int total = checked(packed * h);
            if (destination.Length < total)
                throw new ArgumentException($"BGR 目标缓冲不足（需要 {total} 字节）。", nameof(destination));
            fixed (byte* output = destination)
                if (_decode(_handle, input, (nuint)jpeg.Buffer.Length, output, w, packed, h, TurboJpegPixelFormat(PixelFormat.Bgr24), 0) != 0)
                    throw new InvalidDataException("JPEG 解压失败。");
            return total;
        }
    }
    private ImageLease Decode(ImageInput jpeg, ImageAllocator allocator, PixelFormat format, int maxWidth = 0, int maxHeight = 0)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        int tjFormat = TurboJpegPixelFormat(format);
        int bytesPerPixel = BytesPerPixel(format);
        fixed (byte* input = jpeg.Buffer.Span)
        {
            int w = 0, h = 0, sub = 0, color = 0;
            if (_header(_handle, input, (nuint)jpeg.Buffer.Length, &w, &h, &sub, &color) != 0 || w <= 0 || h <= 0)
                throw new InvalidDataException("JPEG 头部无效。");
            if (w != jpeg.Layout.Width || h != jpeg.Layout.Height) throw new InvalidDataException("JPEG 尺寸与相机协商模式不一致。");
            int originalWidth = w, originalHeight = h;
            // Native JPEG IDCT scaling avoids allocating full-resolution preview pixels.
            int divisor = 1;
            if (maxWidth > 0 && maxHeight > 0)
                while (divisor < 8 &&
                       (w + divisor * 2 - 1) / (divisor * 2) >= maxWidth &&
                       (h + divisor * 2 - 1) / (divisor * 2) >= maxHeight)
                    divisor *= 2;
            w = (w + divisor - 1) / divisor;
            h = (h + divisor - 1) / divisor;
            int stride = checked(w * bytesPerPixel);
            var layout = format switch
            {
                PixelFormat.Gray8 => GrayLayout(w, h),
                PixelFormat.Bgr24 => BgrLayout(w, h),
                PixelFormat.Bgra32 => BgraLayout(w, h),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
            var transform = jpeg.ImageToSource;
            double sx = (double)originalWidth / w, sy = (double)originalHeight / h;
            var result = allocator.Allocate(jpeg.Stamp, layout, checked(stride * h),
                transform with
                {
                    M11 = transform.M11 * sx, M21 = transform.M21 * sx, M31 = transform.M31 * sx,
                    M12 = transform.M12 * sy, M22 = transform.M22 * sy, M32 = transform.M32 * sy
                });
            try
            {
                fixed (byte* output = result.WritableBuffer.Span)
                    if (_decode(_handle, input, (nuint)jpeg.Buffer.Length, output, w, stride, h, tjFormat, 0) != 0)
                        throw new InvalidDataException("JPEG 解压失败。");
                return result;
            }
            catch { result.Dispose(); throw; }
        }
    }
    public static ImageLayout GrayLayout(int width, int height) => PackedLayout(PixelFormat.Gray8, width, height, 1);
    public static ImageLayout BgrLayout(int width, int height) => PackedLayout(PixelFormat.Bgr24, width, height, 3);
    public static ImageLayout BgraLayout(int width, int height) => PackedLayout(PixelFormat.Bgra32, width, height, 4);
    private static ImageLayout PackedLayout(PixelFormat format, int width, int height, int bytesPerPixel)
    {
        int stride = checked(width * bytesPerPixel);
        return new(FrameEncoding.Raw, format, width, height, [new(0, stride, stride, height)], ColorRange.Full, ColorMatrix.Unspecified);
    }
    private static int TurboJpegPixelFormat(PixelFormat format) => format switch
    {
        PixelFormat.Bgr24 => 1,
        PixelFormat.Gray8 => 6,
        PixelFormat.Bgra32 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "JPEG 解压只支持 Gray8、Bgr24、Bgra32。")
    };
    private static int BytesPerPixel(PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Bgr24 => 3,
        PixelFormat.Bgra32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
    public void Dispose() { if (_handle == 0) return; _destroy(_handle); _handle = 0; NativeLibrary.Free(_library); }
}
