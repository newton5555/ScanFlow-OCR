using System.IO;
using BitMiracle.LibTiff.Classic;
using StbImageSharp;
using StbImageWriteSharp;

namespace ScanFlowOcr.Imaging;

public readonly record struct StillImageBgra(byte[] Pixels, int Width, int Height);

public static class StillImages
{
    public static (int Width, int Height) ProbeDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        return ProbeDimensions(stream);
    }

    public static (int Width, int Height) ProbeDimensions(Stream stream)
    {
        Stream seekable = EnsureSeekable(stream);
        try
        {
            if (IsTiffHeader(seekable))
            {
                return ProbeTiffDimensions(seekable);
            }

            long pos = seekable.Position;
            ImageInfo? info = ImageInfo.FromStream(seekable);
            seekable.Position = pos;

            if (info == null || info.Value.Width < 1 || info.Value.Height < 1)
                throw new InvalidDataException("无法识别图片格式。");

            return (info.Value.Width, info.Value.Height);
        }
        finally
        {
            if (!ReferenceEquals(seekable, stream)) seekable.Dispose();
        }
    }

    public static StillImageBgra DecodeBgra(string path, bool compositeOnWhite = true)
    {
        using var stream = File.OpenRead(path);
        return DecodeBgra(stream, compositeOnWhite);
    }

    public static StillImageBgra DecodeBgra(Stream stream, bool compositeOnWhite = true)
    {
        Stream seekable = EnsureSeekable(stream);
        try
        {
            if (IsTiffHeader(seekable))
            {
                return DecodeTiff(seekable, compositeOnWhite);
            }

            long pos = seekable.Position;
            ImageResult result = ImageResult.FromStream(seekable, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            seekable.Position = pos;

            if (result == null || result.Width < 1 || result.Height < 1 || result.Data == null)
                throw new InvalidDataException("无法解码图片。");

            byte[] rgba = result.Data;
            byte[] bgra = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                bgra[i] = rgba[i + 2];     // B
                bgra[i + 1] = rgba[i + 1]; // G
                bgra[i + 2] = rgba[i];     // R
                bgra[i + 3] = rgba[i + 3]; // A
            }

            if (compositeOnWhite)
            {
                CompositeOverWhite(bgra);
            }

            return new StillImageBgra(bgra, result.Width, result.Height);
        }
        finally
        {
            if (!ReferenceEquals(seekable, stream)) seekable.Dispose();
        }
    }

    public static byte[] CopyToBgr(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width < 1 || height < 1 || bgra.Length < checked(width * height * 4))
            throw new ArgumentException("BGRA 缓冲无效。");
        byte[] bgr = new byte[checked(width * height * 3)];
        for (int i = 0; i < width * height; i++)
        {
            bgr[i * 3] = bgra[i * 4];
            bgr[i * 3 + 1] = bgra[i * 4 + 1];
            bgr[i * 3 + 2] = bgra[i * 4 + 2];
        }
        return bgr;
    }

    public static byte[] EncodeJpegFromBgr(ReadOnlySpan<byte> bgr, int width, int height, int quality = 95)
    {
        if (width < 1 || height < 1 || bgr.Length < checked(width * height * 3))
            throw new ArgumentException("BGR 缓冲无效。");
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));

        byte[] rgb = new byte[checked(width * height * 3)];
        for (int i = 0; i < width * height; i++)
        {
            rgb[i * 3] = bgr[i * 3 + 2];     // R
            rgb[i * 3 + 1] = bgr[i * 3 + 1]; // G
            rgb[i * 3 + 2] = bgr[i * 3];     // B
        }

        using var ms = new MemoryStream();
        var writer = new ImageWriter();
        writer.WriteJpg(rgb, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlue, ms, quality);
        return ms.ToArray();
    }

    public static byte[] ScaleBgra(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int destWidth, int destHeight)
    {
        if (sourceWidth < 1 || sourceHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (destWidth < 1 || destHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(destWidth));
        if (source.Length < checked(sourceWidth * sourceHeight * 4))
            throw new ArgumentException("BGRA 缓冲无效。", nameof(source));

        if (sourceWidth == destWidth && sourceHeight == destHeight)
        {
            return source.Slice(0, checked(destWidth * destHeight * 4)).ToArray();
        }

        byte[] dest = new byte[checked(destWidth * destHeight * 4)];
        float scaleX = (float)sourceWidth / destWidth;
        float scaleY = (float)sourceHeight / destHeight;

        for (int y = 0; y < destHeight; y++)
        {
            float srcY = (y + 0.5f) * scaleY - 0.5f;
            if (srcY < 0) srcY = 0;
            int y0 = (int)srcY;
            int y1 = Math.Min(y0 + 1, sourceHeight - 1);
            float v = srcY - y0;
            float invV = 1.0f - v;

            int row0Offset = y0 * sourceWidth * 4;
            int row1Offset = y1 * sourceWidth * 4;
            int destRowOffset = y * destWidth * 4;

            for (int x = 0; x < destWidth; x++)
            {
                float srcX = (x + 0.5f) * scaleX - 0.5f;
                if (srcX < 0) srcX = 0;
                int x0 = (int)srcX;
                int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                float u = srcX - x0;
                float invU = 1.0f - u;

                float w00 = invU * invV;
                float w10 = u * invV;
                float w01 = invU * v;
                float w11 = u * v;

                int p00 = row0Offset + x0 * 4;
                int p10 = row0Offset + x1 * 4;
                int p01 = row1Offset + x0 * 4;
                int p11 = row1Offset + x1 * 4;

                int dIdx = destRowOffset + x * 4;
                dest[dIdx] = (byte)(w00 * source[p00] + w10 * source[p10] + w01 * source[p01] + w11 * source[p11] + 0.5f);
                dest[dIdx + 1] = (byte)(w00 * source[p00 + 1] + w10 * source[p10 + 1] + w01 * source[p01 + 1] + w11 * source[p11 + 1] + 0.5f);
                dest[dIdx + 2] = (byte)(w00 * source[p00 + 2] + w10 * source[p10 + 2] + w01 * source[p01 + 2] + w11 * source[p11 + 2] + 0.5f);
                dest[dIdx + 3] = (byte)(w00 * source[p00 + 3] + w10 * source[p10 + 3] + w01 * source[p01 + 3] + w11 * source[p11 + 3] + 0.5f);
            }
        }
        return dest;
    }

    public static void CompositeOverWhite(byte[] buffer)
    {
        for (int i = 0; i < buffer.Length; i += 4)
        {
            byte a = buffer[i + 3];
            if (a == 255) continue;
            if (a == 0)
            {
                buffer[i] = 255;
                buffer[i + 1] = 255;
                buffer[i + 2] = 255;
                buffer[i + 3] = 255;
                continue;
            }
            buffer[i] = (byte)((buffer[i] * a + 255 * (255 - a) + 127) / 255);
            buffer[i + 1] = (byte)((buffer[i + 1] * a + 255 * (255 - a) + 127) / 255);
            buffer[i + 2] = (byte)((buffer[i + 2] * a + 255 * (255 - a) + 127) / 255);
            buffer[i + 3] = 255;
        }
    }

    private static Stream EnsureSeekable(Stream stream)
    {
        if (stream.CanSeek) return stream;
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    private static bool IsTiffHeader(Stream stream)
    {
        if (!stream.CanSeek) return false;
        long pos = stream.Position;
        Span<byte> header = stackalloc byte[4];
        int read = stream.Read(header);
        stream.Position = pos;
        if (read < 4) return false;

        return (header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00) ||
               (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A);
    }

    private static (int Width, int Height) ProbeTiffDimensions(Stream stream)
    {
        long pos = stream.Position;
        try
        {
            using var tif = Tiff.ClientOpen("custom", "r", stream, NonClosingTiffStream.Instance);
            if (tif == null) throw new InvalidDataException("无法解析 TIFF 文件。");
            FieldValue[]? wField = tif.GetField(TiffTag.IMAGEWIDTH);
            FieldValue[]? hField = tif.GetField(TiffTag.IMAGELENGTH);
            if (wField == null || hField == null || wField.Length == 0 || hField.Length == 0)
                throw new InvalidDataException("TIFF 缺少尺寸元数据。");
            int width = wField[0].ToInt();
            int height = hField[0].ToInt();
            if (width <= 0 || height <= 0)
                throw new InvalidDataException($"TIFF 分辨率无效 ({width}×{height})。");
            return (width, height);
        }
        finally
        {
            stream.Position = pos;
        }
    }

    private static StillImageBgra DecodeTiff(Stream stream, bool compositeOnWhite)
    {
        long pos = stream.Position;
        try
        {
            using var tif = Tiff.ClientOpen("custom", "r", stream, NonClosingTiffStream.Instance);
            if (tif == null) throw new InvalidDataException("无法解析 TIFF 文件。");
            FieldValue[]? wField = tif.GetField(TiffTag.IMAGEWIDTH);
            FieldValue[]? hField = tif.GetField(TiffTag.IMAGELENGTH);
            if (wField == null || hField == null || wField.Length == 0 || hField.Length == 0)
                throw new InvalidDataException("TIFF 缺少尺寸元数据。");
            int width = wField[0].ToInt();
            int height = hField[0].ToInt();
            if (width <= 0 || height <= 0)
                throw new InvalidDataException($"TIFF 分辨率无效 ({width}×{height})。");

            int[] raster = new int[checked(width * height)];
            if (!tif.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT))
            {
                throw new InvalidDataException("无法解码 TIFF 像素数据。");
            }

            byte[] bgra = new byte[checked(width * height * 4)];
            for (int i = 0; i < raster.Length; i++)
            {
                uint pixel = (uint)raster[i];
                // ReadRGBAImageOriented 输出像素按 ABGR/RGBA 字节排布：
                // TIFFGetR(pixel) = pixel & 0xFF
                // TIFFGetG(pixel) = (pixel >> 8) & 0xFF
                // TIFFGetB(pixel) = (pixel >> 16) & 0xFF
                // TIFFGetA(pixel) = (pixel >> 24) & 0xFF
                bgra[i * 4] = (byte)((pixel >> 16) & 0xFF);     // B
                bgra[i * 4 + 1] = (byte)((pixel >> 8) & 0xFF);  // G
                bgra[i * 4 + 2] = (byte)(pixel & 0xFF);         // R
                bgra[i * 4 + 3] = (byte)((pixel >> 24) & 0xFF); // A
            }

            if (compositeOnWhite)
            {
                CompositeOverWhite(bgra);
            }

            return new StillImageBgra(bgra, width, height);
        }
        finally
        {
            stream.Position = pos;
        }
    }

    private sealed class NonClosingTiffStream : TiffStream
    {
        public static readonly NonClosingTiffStream Instance = new();

        public override int Read(object clientData, byte[] buffer, int offset, int count) =>
            ((Stream)clientData).Read(buffer, offset, count);

        public override void Write(object clientData, byte[] buffer, int offset, int count) =>
            ((Stream)clientData).Write(buffer, offset, count);

        public override long Seek(object clientData, long offset, SeekOrigin origin) =>
            ((Stream)clientData).Seek(offset, origin);

        public override void Close(object clientData)
        {
            // 不关闭外部传入的 Stream，确保其生命周期完全由调用方控制
        }

        public override long Size(object clientData) =>
            ((Stream)clientData).Length;
    }
}
