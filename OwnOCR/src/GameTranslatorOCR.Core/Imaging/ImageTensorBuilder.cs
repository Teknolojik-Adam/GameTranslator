using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using System.Runtime.InteropServices;

namespace GameTranslatorOCR.Core.Imaging;

/// <summary>
/// PP-OCR NCHW RGB [-1,1] preprocessing, optional minimum-resolution scaling
/// and dynamic-width text batching. No Python/OpenCV runtime dependency.
/// </summary>
public static class ImageTensorBuilder
{
    public const int RecognizerHeight=48;

    public static (DenseTensor<float> Tensor,int Width,int Height) BuildDetection(
        SKBitmap original,int maxSide=960,int minSide=0)
    {
        ArgumentNullException.ThrowIfNull(original);
        if(maxSide<32||minSide<0)throw new ArgumentOutOfRangeException(nameof(maxSide));
        double maxScale=(double)maxSide/Math.Max(original.Width,original.Height);
        double desiredScale=minSide>0?
            Math.Max(1d,(double)minSide/Math.Min(original.Width,original.Height)):1d;
        double scale=Math.Min(maxScale,desiredScale);
        int w=Math.Max(32,(int)Math.Round(original.Width*scale/32d)*32);
        int h=Math.Max(32,(int)Math.Round(original.Height*scale/32d)*32);
        using var resized=Resize(original,w,h);
        var tensor=new DenseTensor<float>([1,3,h,w]);
        Fill(resized,tensor,w,0);
        return (tensor,w,h);
    }

    public static DenseTensor<float> BuildRecognition(SKBitmap crop)=>
        BuildRecognitionBatch([crop]).Tensor;

    /// <summary>
    /// Groups all crops at common width but preserves individual pixel widths
    /// for optional approximate CTC word alignment.
    /// </summary>
    public static RecognitionBatch BuildRecognitionBatch(IReadOnlyList<SKBitmap> crops)
    {
        ArgumentNullException.ThrowIfNull(crops);
        if(crops.Count<1||crops.Count>32)
            throw new ArgumentOutOfRangeException(nameof(crops),"1-32 crops expected.");
        var widths=new int[crops.Count];
        var originalWidths=new int[crops.Count];
        int canvasWidth=64;
        for(int i=0;i<crops.Count;i++)
        {
            SKBitmap crop=crops[i]??throw new ArgumentNullException(nameof(crops));
            if(crop.Width<1||crop.Height<1)
                throw new InvalidDataException("OCR crop boş.");
            double ratio=(double)crop.Width/crop.Height;
            int resizedWidth=Math.Clamp((int)Math.Round(RecognizerHeight*ratio),1,1024);
            widths[i]=resizedWidth;
            originalWidths[i]=crop.Width;
            int needed=Math.Clamp((int)Math.Ceiling(RecognizerHeight*ratio/16d)*16,64,1024);
            canvasWidth=Math.Max(canvasWidth,needed);
        }
        var tensor=new DenseTensor<float>([crops.Count,3,RecognizerHeight,canvasWidth]);
        for(int i=0;i<crops.Count;i++)
        {
            using var image=Resize(crops[i],widths[i],RecognizerHeight);
            Fill(image,tensor,canvasWidth,i);
        }
        return new RecognitionBatch(tensor,widths,canvasWidth);
    }

    public static SKBitmap Resize(SKBitmap source,int width,int height)
    {
        if(width<=0||height<=0)throw new ArgumentOutOfRangeException(nameof(width));
        var output=new SKBitmap(new SKImageInfo(width,height,
            SKColorType.Rgba8888,SKAlphaType.Premul));
        using var canvas=new SKCanvas(output);
        canvas.Clear(SKColors.White);
        using var paint=new SKPaint{IsAntialias=true};
        canvas.DrawBitmap(source,new SKRect(0,0,width,height),paint);
        canvas.Flush();
        return output;
    }

    public static SKBitmap Crop(SKBitmap source,int x,int y,int width,int height)
    {
        if(width<=0||height<=0)throw new ArgumentOutOfRangeException(nameof(width));
        var result=new SKBitmap(new SKImageInfo(width,height,
            SKColorType.Rgba8888,SKAlphaType.Premul));
        using var canvas=new SKCanvas(result);
        canvas.Clear(SKColors.White);
        using var paint=new SKPaint{IsAntialias=true};
        canvas.DrawBitmap(source,new SKRect(x,y,x+width,y+height),
            new SKRect(0,0,width,height),paint);
        canvas.Flush();
        return result;
    }

    private static void Fill(SKBitmap bitmap,DenseTensor<float> tensor,
        int strideWidth,int batchIndex)
    {
        if(bitmap.ColorType!=SKColorType.Rgba8888)
            throw new InvalidDataException("RGBA8888 piksel bekleniyor.");
        int height=bitmap.Height,planeSize=height*strideWidth;
        int batchOffset=batchIndex*planeSize*3;
        var buffer=tensor.Buffer.Span;
        // SkiaSharp exposes the existing bitmap storage as a Span. Do not
        // allocate a full-size byte[] and Marshal.Copy on every detector or
        // recognition crop. Pixel values and channel order remain unchanged.
        int byteCount=checked(bitmap.RowBytes*height);
        ReadOnlySpan<byte> raw=bitmap.GetPixelSpan();
        if(raw.Length<byteCount)
            throw new InvalidDataException("Skia bitmap piksel tamponu beklenenden kısa.");
        for(int y=0;y<height;y++)
        {
            int offset=y*bitmap.RowBytes;
            for(int x=0;x<bitmap.Width;x++)
            {
                int pixel=offset+x*4;
                int idx=batchOffset+y*strideWidth+x;
                buffer[idx]=raw[pixel]/127.5f-1f;
                buffer[idx+planeSize]=raw[pixel+1]/127.5f-1f;
                buffer[idx+2*planeSize]=raw[pixel+2]/127.5f-1f;
            }
        }
    }
}

public sealed record RecognitionBatch(DenseTensor<float> Tensor,
    IReadOnlyList<int> UsedWidths,int BatchWidth);
