using System.Text.Json;
using GameTranslatorOCR.Core.Engines;
using GameTranslatorOCR.Core.Models;

Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length != 1) { Console.Error.WriteLine("Missing model path"); return 2; }
try
{
    using var engine = new OnnxGameOcrEngine(args[0]);
    string? line;
    while ((line = await Console.In.ReadLineAsync()) != null)
    {
        try
        {
            var request = JsonSerializer.Deserialize<OcrRequest>(line) ?? throw new InvalidDataException("Missing request");
            var data = Convert.FromBase64String(request.Image ?? "");
            var language = request.Language == "en" ? "en" : "tr";
            var options = new OcrOptions(Language: language, Quality: OcrQualityProfile.Balanced,
                RecognitionPaddingPixels: 6, DetectionMaxSideOverride: 864,
                RestrictTurkishEnglishCharset: true);
            var result = await engine.RecognizeAsync(data, options);
            Console.WriteLine(JsonSerializer.Serialize(new OcrResponse(
                string.Join("\n", result.Lines.Select(l => l.Text)), null)));
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new OcrResponse("", ex.Message)));
        }
    }
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
return 0;
public sealed class OcrRequest { public string? Image { get; set; } public string? Language { get; set; } }
public sealed record OcrResponse(string Text, string? Error);
