using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Imaging;

public static class RawImages
{
    public static ImageLease ToGray(ImageInput input,ImageAllocator allocator)
    {
        ValidateBgra(input);
        int w=input.Layout.Width,h=input.Layout.Height;
        var plane=input.Layout.Planes[0];
        var result=allocator.Allocate(input.Stamp,JpegDecoder.GrayLayout(w,h),checked(w*h),input.ImageToSource);
        var source=input.Buffer.Span; var dest=result.WritableBuffer.Span;
        for(int y=0;y<h;y++)
        {
            var row=source.Slice(plane.Offset+y*plane.StrideBytes,w*4);
            for(int x=0;x<w;x++) dest[y*w+x]=(byte)((29*row[x*4]+150*row[x*4+1]+77*row[x*4+2]+128)>>8);
        }
        return result;
    }
    public static ImageLease ToBgr(ImageInput input,ImageAllocator allocator)
    {
        ValidateBgra(input);
        int w=input.Layout.Width,h=input.Layout.Height;
        var plane=input.Layout.Planes[0];
        var result=allocator.Allocate(input.Stamp,JpegDecoder.BgrLayout(w,h),checked(w*h*3),input.ImageToSource);
        var source=input.Buffer.Span; var dest=result.WritableBuffer.Span;
        for(int y=0;y<h;y++)
        {
            var row=source.Slice(plane.Offset+y*plane.StrideBytes,w*4);
            var destRow=dest.Slice(y*w*3,w*3);
            for(int x=0;x<w;x++)
            {
                destRow[x*3]=row[x*4];
                destRow[x*3+1]=row[x*4+1];
                destRow[x*3+2]=row[x*4+2];
            }
        }
        return result;
    }
    public static void ValidateBgr(ImageInput input)
    {
        var layout=input.Layout;
        if(layout.Encoding!=FrameEncoding.Raw || layout.PixelFormat!=PixelFormat.Bgr24 || layout.Width<1 || layout.Height<1 || layout.Planes.Length!=1) throw new ArgumentException("需要 BGR24 图像。",nameof(input));
        var p=layout.Planes[0]; long row=(long)layout.Width*3;
        long end=(long)p.Offset+(long)(layout.Height-1)*p.StrideBytes;
        if(p.Height<layout.Height || p.RowBytes<row || Math.Abs((long)p.StrideBytes)<row || Math.Min(p.Offset,end)<0 || Math.Max(p.Offset,end)+row>input.Buffer.Length) throw new ArgumentException("BGR24 图像布局无效。",nameof(input));
    }
    public static void ValidateBgra(ImageInput input)
    {
        var layout=input.Layout;
        if(layout.Encoding!=FrameEncoding.Raw || layout.PixelFormat!=PixelFormat.Bgra32 || layout.Width<1 || layout.Height<1 || layout.Planes.Length!=1) throw new ArgumentException("需要 BGRA32 图像。",nameof(input));
        var p=layout.Planes[0]; long row=(long)layout.Width*4;
        long end=(long)p.Offset+(long)(layout.Height-1)*p.StrideBytes;
        if(p.Height<layout.Height || p.RowBytes<row || Math.Abs((long)p.StrideBytes)<row || Math.Min(p.Offset,end)<0 || Math.Max(p.Offset,end)+row>input.Buffer.Length) throw new ArgumentException("BGRA32 图像布局无效。",nameof(input));
    }
}
