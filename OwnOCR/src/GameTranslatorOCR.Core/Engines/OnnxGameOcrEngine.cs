using System.Diagnostics;
using System.Runtime.InteropServices;
using GameTranslatorOCR.Core.Abstractions;
using GameTranslatorOCR.Core.Detection;
using GameTranslatorOCR.Core.Imaging;
using GameTranslatorOCR.Core.ModelPacks;
using GameTranslatorOCR.Core.Models;
using GameTranslatorOCR.Core.Recognition;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace GameTranslatorOCR.Core.Engines;

/// <summary>
/// PP-OCRv5 Mobile CPU ONNX OCR motoru. Kodlanmış görüntüleri ve doğrudan BGRA32
/// ekran yakalama arabelleklerini işleyebilir; Python/Tesseract gerektirmez.
/// ONNX oturumları motor ömrü boyunca paylaşılır.
/// </summary>
public sealed class OnnxGameOcrEngine : IRawOcrEngine
{
    private readonly InferenceSession _detector;
    private readonly InferenceSession _recognizer;
    private readonly CtcDecoder _decoder;
    private readonly string _detectorInputName;
    private readonly string _recognizerInputName;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly HashSet<string> _supportedLanguages;

    public string ModelPackId { get; }

    /// <summary>Son OCR çağrısının aşama süreleri, ms. Profil amaçlıdır.</summary>
    public OcrStageTimings? LastTimings { get; private set; }

    public OnnxGameOcrEngine(string modelDirectory)
    {
        var validation = ModelPackValidator.Validate(modelDirectory);
        if (!validation.IsValid)
            throw new InvalidDataException("Model paketi geçersiz: " +
                string.Join("; ", validation.Errors));
        var checksums = ModelPackIntegrity.Verify(modelDirectory);
        if (checksums.Count != 0)
            throw new InvalidDataException("Model dosya bütünlüğü doğrulanamadı: " +
                string.Join("; ", checksums));

        var manifest = validation.Manifest!;
        ModelPackId = manifest.PackId;
        _supportedLanguages = new HashSet<string>(manifest.Languages,
            StringComparer.OrdinalIgnoreCase);
        _decoder = new CtcDecoder(Path.Combine(modelDirectory, manifest.DictionaryFile));

        using var opts = new SessionOptions();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        string? envThreads=Environment.GetEnvironmentVariable("GTOCR_INTRAOP_THREADS");
        opts.IntraOpNumThreads=int.TryParse(envThreads,out int requestedThreads) &&
            requestedThreads is >= 1 and <= 24
            ? requestedThreads
            : Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        opts.InterOpNumThreads = 1;
        _detector = new InferenceSession(Path.Combine(modelDirectory, manifest.DetectorFile), opts);
        try
        {
            _recognizer = new InferenceSession(
                Path.Combine(modelDirectory, manifest.RecognizerFile), opts);
        }
        catch
        {
            _detector.Dispose();
            throw;
        }

        _detectorInputName = _detector.InputMetadata.Keys.Single();
        _recognizerInputName = _recognizer.InputMetadata.Keys.Single();
    }

    public async Task<OcrResult> RecognizeAsync(
        ReadOnlyMemory<byte> encodedImage,
        OcrOptions options,
        CancellationToken cancellationToken = default)
    {
        VerifyOptions(options);
        if (encodedImage.IsEmpty)
            throw new ArgumentException("Görüntü boş.", nameof(encodedImage));

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                cancellationToken.ThrowIfCancellationRequested();
                using var image = SKBitmap.Decode(encodedImage.ToArray())
                    ?? throw new InvalidDataException("Görüntü formatı çözümlenemedi.");
                return RecognizeBitmapCore(image, options, cancellationToken, stopwatch);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<OcrResult> RecognizeBgraAsync(
        ReadOnlyMemory<byte> pixels,
        int width, int height, int stride,
        OcrOptions options,
        CancellationToken cancellationToken = default)
    {
        VerifyOptions(options);
        ValidateBgra(pixels.Length, width, height, stride);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                cancellationToken.ThrowIfCancellationRequested();
                using var bitmap = CreateBgraBitmap(pixels, width, height, stride);
                return RecognizeBitmapCore(bitmap, options, cancellationToken, stopwatch);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private void VerifyOptions(OcrOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_supportedLanguages.Contains(options.Language) &&
            !string.Equals(options.Language, ModelPackId, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"'{options.Language}' dili '{ModelPackId}' model paketiyle desteklenmiyor.");
    }

    private static void ValidateBgra(int length, int width, int height, int stride)
    {
        if (width < 1 || height < 1 || width > 8192 || height > 8192 ||
            (long)width * height > 20_000_000)
            throw new ArgumentOutOfRangeException(nameof(width),
                "Görüntü boyutu geçersiz veya 20 megapikseli aşıyor.");
        if (stride < (long)width * 4 || length < (long)(height - 1) * stride + (long)width * 4)
            throw new ArgumentException("Ham BGRA veri uzunluğu/stride uyuşmuyor.");
    }

    private static SKBitmap CreateBgraBitmap(
        ReadOnlyMemory<byte> pixels, int width, int height, int stride)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        try
        {
            // Avoid copying the entire caller-owned BGRA buffer into a
            // temporary array. Copy scanlines directly into Skia's bitmap
            // storage, preserving row stride and every original pixel.
            Span<byte> dest = bitmap.GetPixelSpan();
            if (dest.Length < checked(bitmap.RowBytes * height))
                throw new OutOfMemoryException("SKBitmap ayrılmış bellek yok.");
            ReadOnlySpan<byte> source = pixels.Span;
            int copyWidth = checked(width * 4);
            for (int y = 0; y < height; y++)
                source.Slice(y * stride, copyWidth)
                    .CopyTo(dest.Slice(y * bitmap.RowBytes, copyWidth));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private OcrResult RecognizeBitmapCore(SKBitmap image,OcrOptions options,
        CancellationToken cancellationToken,Stopwatch stopwatch)
    {
        if((long)image.Width*image.Height>20_000_000)
            throw new InvalidDataException("Görüntü çok büyük (20 MP sınırı).");

        double decodeMs=stopwatch.Elapsed.TotalMilliseconds;
        int limit=options.Quality switch
        {
            OcrQualityProfile.Speed=>736,
            OcrQualityProfile.Balanced=>960,
            OcrQualityProfile.Quality=>1280,
            _=>960
        };
        if(options.DetectionMaxSideOverride is >= 320 and <= 1920)
            limit=options.DetectionMaxSideOverride;
        int minimum=options.AdaptiveDetectionUpscale?Math.Min(limit,736):0;
        var (tensor,_,_)=ImageTensorBuilder.BuildDetection(image,limit,minimum);
        double prepareMs=stopwatch.Elapsed.TotalMilliseconds;
        using var detectorOutput=_detector.Run(
            [NamedOnnxValue.CreateFromTensor(_detectorInputName,tensor)]);
        double detectorMs=stopwatch.Elapsed.TotalMilliseconds;
        var probabilities=detectorOutput.First().AsTensor<float>();
        var boxes=DbNetPostProcessor.Extract(probabilities,image.Width,image.Height);

        double postprocessMs=stopwatch.Elapsed.TotalMilliseconds;
        var crops=new List<SKBitmap>(boxes.Count);
        var quads=new List<OcrQuad?>(boxes.Count);
        try
        {
            foreach(var box in boxes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OcrQuad? quad=options.EnablePerspectiveCorrection
                    ?TextAngleEstimator.Estimate(probabilities,box,image.Width,image.Height)
                    :null;
                SKBitmap crop;
                if(quad.HasValue)
                {
                    crop=PerspectiveCropper.Rectify(image,quad.Value);
                }
                else
                {
                    int padding=Math.Clamp(options.RecognitionPaddingPixels,0,24);
                    if(options.AdaptiveRecognitionPadding)
                        padding=Math.Max(padding,Math.Clamp(box.Height/7,2,24));
                    int x1=Math.Max(0,box.X-padding);
                    int y1=Math.Max(0,box.Y-padding);
                    int x2=Math.Min(image.Width,box.X+box.Width+padding);
                    int y2=Math.Min(image.Height,box.Y+box.Height+padding);
                    crop=ImageTensorBuilder.Crop(image,x1,y1,x2-x1,y2-y1);
                }
                crops.Add(crop);
                quads.Add(quad);
            }

            double croppingMs=stopwatch.Elapsed.TotalMilliseconds;
            var lines=new OcrLine?[boxes.Count];
            int batchSize=Math.Clamp(options.RecognitionBatchSize,1,12);
            int[] sorted=Enumerable.Range(0,crops.Count).OrderBy(
                i=>(double)crops[i].Width/crops[i].Height).ToArray();

            // Batch only similarly shaped lines. Large width padding can alter
            // CTC characters (e.g. Turkish dotless 'ı'), so separate distant ratios.
            for(int offset=0;offset<sorted.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double shortestRatio=(double)crops[sorted[offset]].Width/
                    crops[sorted[offset]].Height;
                int next=offset+1;
                while(next<sorted.Length && next-offset<batchSize)
                {
                    double ratio=(double)crops[sorted[next]].Width/
                        crops[sorted[next]].Height;
                    if(ratio>shortestRatio*1.25)break;
                    next++;
                }
                int[] indices=sorted.Skip(offset).Take(next-offset).ToArray();
                offset=next;
                SKBitmap[] batchCrops=indices.Select(i=>crops[i]).ToArray();
                var recBatch=ImageTensorBuilder.BuildRecognitionBatch(batchCrops);
                using var recognitionOutput=_recognizer.Run(
                    [NamedOnnxValue.CreateFromTensor(_recognizerInputName,recBatch.Tensor)]);
                var output=recognitionOutput.First().AsTensor<float>();

                for(int row=0;row<indices.Length;row++)
                {
                    int i=indices[row];
                    var decoded=_decoder.DecodeDetailed(output,row,
                        options.RestrictTurkishEnglishCharset);
                    var recognized=decoded.Recognition;
                    if(string.IsNullOrWhiteSpace(recognized.Text))continue;
                    if(options.EnableArtifactFilter &&
                       OcrArtifactFilter.ShouldReject(recognized,boxes[i]))
                        continue;

                    var line=new OcrLine(recognized.Text,boxes[i],recognized.Confidence)
                    {
                        Quadrilateral=quads[i]
                    };
                    if(options.ReturnWordBoxes)
                    {
                        line=line with
                        {
                            Words=ApproximateWords(decoded,boxes[i],
                                recBatch.UsedWidths[row],recBatch.BatchWidth)
                        };
                    }
                    lines[i]=line;
                }
            }
            stopwatch.Stop();
            LastTimings=new OcrStageTimings(
                DecodeMs:decodeMs,
                PreprocessMs:prepareMs-decodeMs,
                DetectionMs:detectorMs-prepareMs,
                PostprocessMs:postprocessMs-detectorMs,
                CropMs:croppingMs-postprocessMs,
                RecognitionMs:stopwatch.Elapsed.TotalMilliseconds-croppingMs,
                TotalMs:stopwatch.Elapsed.TotalMilliseconds);
            return new OcrResult(lines.Where(x=>x is not null).Select(x=>x!).ToArray(),
                options.Language,stopwatch.Elapsed);
        }
        finally
        {
            foreach(var crop in crops)crop.Dispose();
        }
    }

    /// <summary>
    /// CTC timestep alignment is only an approximation (not a character detector).
    /// Word boxes remain axis aligned and need geometric mapping for rotated text.
    /// </summary>
    private static IReadOnlyList<OcrWord> ApproximateWords(DecodedRecognition decoded,
        OcrBox bounds,int usedWidth,int tensorWidth)
    {
        if(usedWidth<=0||decoded.TotalSteps<=0||decoded.Words.Count==0)
            return Array.Empty<OcrWord>();
        var words=new List<OcrWord>();
        double scale=(double)tensorWidth/usedWidth;
        foreach(var span in decoded.Words)
        {
            double left=Math.Clamp((double)span.StartStep/decoded.TotalSteps*scale,0,1);
            double right=Math.Clamp((double)span.EndStep/decoded.TotalSteps*scale,0,1);
            int x1=bounds.X+(int)Math.Floor(left*bounds.Width);
            int x2=bounds.X+(int)Math.Ceiling(right*bounds.Width);
            if(x2<=x1)x2=x1+1;
            words.Add(new OcrWord(span.Text,
                new OcrBox(x1,bounds.Y,x2-x1,bounds.Height),span.Confidence));
        }
        return words;
    }

    public void Dispose()
    {
        _detector.Dispose();
        _recognizer.Dispose();
        _mutex.Dispose();
    }
}
