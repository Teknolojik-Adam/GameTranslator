using System.Text.Json;

namespace GameTranslatorOCR.Core.ModelPacks;

public sealed record ModelPackValidation(
    ModelManifest? Manifest,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Model dosyası varlığını ve paket manifestinin güvenli yerel yollarını kontrol eder.
/// ONNX tensor uyumluluğu bir sonraki aşamada doğrulanacaktır.</summary>
public static class ModelPackValidator
{
    public static ModelPackValidation Validate(string directory)
    {
        var errors = new List<string>();
        if (!Directory.Exists(directory))
            return new(null, ["Model paketi dizini bulunamadı."]);

        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
            return new(null, ["manifest.json bulunamadı."]);

        ModelManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModelManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(null, [$"Manifest okunamadı: {ex.Message}"]);
        }

        if (manifest is null)
            return new(null, ["Manifest boş."]);

        if (manifest.SchemaVersion != 1)
            errors.Add("Desteklenmeyen manifest şema sürümü.");
        if (string.IsNullOrWhiteSpace(manifest.PackId))
            errors.Add("packId boş.");
        if (manifest.Languages is null || manifest.Languages.Length == 0)
            errors.Add("En az bir dil belirtilmeli.");
        if (string.IsNullOrWhiteSpace(manifest.Decoder))
            errors.Add("decoder boş.");

        CheckFile("detectorFile", manifest.DetectorFile);
        CheckFile("recognizerFile", manifest.RecognizerFile);
        CheckFile("dictionaryFile", manifest.DictionaryFile);

        return new(manifest, errors);

        void CheckFile(string key, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"{key} boş.");
                return;
            }

            // Paket içinden çıkılmasına veya mutlak yol kullanılmasına izin verme.
            if (name != Path.GetFileName(name) || name is "." or ".." ||
                name.Contains('\\') || Path.IsPathRooted(name))
            {
                errors.Add($"{key} güvenli bir dosya adı değil.");
                return;
            }
            if (!File.Exists(Path.Combine(directory, name)))
                errors.Add($"{key}: {name} dosyası bulunamadı.");
        }
    }
}
