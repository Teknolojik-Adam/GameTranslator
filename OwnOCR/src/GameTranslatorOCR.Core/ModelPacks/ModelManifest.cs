namespace GameTranslatorOCR.Core.ModelPacks;

/// <summary>Model paketinin disk üzerindeki bileşenlerini tarif eder.</summary>
public sealed class ModelManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string PackId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ModelFamily { get; set; } = "";
    public string Decoder { get; set; } = "";
    public string DetectorFile { get; set; } = "";
    public string RecognizerFile { get; set; } = "";
    public string DictionaryFile { get; set; } = "";
    public string[] Languages { get; set; } = [];
}
