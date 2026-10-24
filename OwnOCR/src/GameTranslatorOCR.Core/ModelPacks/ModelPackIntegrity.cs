using System.Security.Cryptography;
using System.Text.Json;

namespace GameTranslatorOCR.Core.ModelPacks;

/// <summary>Model manifestindeki SHA-256 sabitlemelerini doğrular.</summary>
public static class ModelPackIntegrity
{
    public static IReadOnlyList<string> Verify(string directory)
    {
        var errors = new List<string>();
        string manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath)) return ["manifest.json bulunamadı."];

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!manifest.RootElement.TryGetProperty("sha256", out var hashes) ||
            hashes.ValueKind != JsonValueKind.Object)
            return ["Model manifestinde sha256 doğrulama alanı yok."];

        Check("detector", "detectorFile");
        Check("recognizer", "recognizerFile");
        Check("dictionary", "dictionaryFile");
        return errors;

        void Check(string key, string filenameKey)
        {
            if (!manifest.RootElement.TryGetProperty(filenameKey, out var filenameJson) ||
                filenameJson.ValueKind != JsonValueKind.String ||
                !hashes.TryGetProperty(key, out var expectedJson) ||
                expectedJson.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{key}: manifest alanı eksik.");
                return;
            }

            string filename = filenameJson.GetString() ?? "";
            string expected = expectedJson.GetString() ?? "";
            if (filename.Length == 0 || filename != Path.GetFileName(filename) ||
                filename.Contains('\\') || Path.IsPathRooted(filename))
            {
                errors.Add($"{key}: güvenli olmayan dosya yolu.");
                return;
            }
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            {
                errors.Add($"{key}: geçersiz SHA-256 değeri.");
                return;
            }
            string path = Path.Combine(directory, filename);
            if (!File.Exists(path))
            {
                errors.Add($"{key}: model dosyası bulunamadı.");
                return;
            }

            using var stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{key}: SHA-256 uyuşmuyor (model bozulmuş/değişmiş olabilir).");
        }
    }
}
