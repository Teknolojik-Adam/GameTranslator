# Third-party components & model attribution

Bu proje yazılmış OCR koordinasyon, satır takip ve görüntü işlem hattı koduna sahip olmakla beraber aşağıdaki üçüncü taraf bileşenleri kullanır.

| Bileşen | Sürüm | Lisans | Kaynak |
|---|---|---|---|
| SkiaSharp | 3.119.1 | MIT | https://github.com/mono/SkiaSharp |
| ONNX Runtime | 1.29.0 | MIT ve üçüncü taraf bildirimleri | https://github.com/microsoft/onnxruntime |
| PP-OCRv5 Mobile detector | RapidAI/RapidOCR ModelScope v3.10.0 | PaddleOCR temelli Apache-2.0 | https://www.modelscope.cn/models/RapidAI/RapidOCR |
| PP-OCRv5 Latin recognizer | RapidAI/RapidOCR ModelScope v3.10.0 | PaddleOCR temelli Apache-2.0 | https://github.com/PaddlePaddle/PaddleOCR |
| PP-OCRv5 Latin charset | RapidAI/RapidOCR ModelScope v3.10.0 | Yukarıdaki model paketinin parçası | https://www.modelscope.cn/models/RapidAI/RapidOCR |
| PP-OCRv6 small recognizer (local cached file) | PaddleOCR-derived, RapidOCR redistribution | Apache-2.0 | https://rapidai.github.io/RapidOCRDocs/latest/en/model_licenses/ |

Model dosyaları birebir indirildi; kodun yazarı olmak model ağırlıklarının telif hakkını değiştirmez. Paket içinde model lisans metni, kaynak atfı ve gerekliyse upstream NOTICE metinleri korunmalıdır. Ayrı model sürümleri için koşulları tekrar gözden geçirin.

- Detector SHA256: `4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae`
- Recognizer SHA256: `b20bd37c168a570f583afbc8cd7925603890efbcdc000a59e22c269d160b5f5a`
- Charset SHA256: `3c0a8a79b612653c25f765271714f71281e4e955962c153e272b7b8c1d2b13ff`
- Hybrid recognizer SHA256: `6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884`
- Hybrid charset extracted from recognizer metadata SHA256: `f7aa897ca828a4c7c9e2739c30f9161a33306d532f020bcdb91dcfb664a5507e`
- Hybrid detector remains the PP-OCRv5 detector, unmodified.
- The PP-OCRv6 small recognition model was **already present** in the installed RapidOCR distribution. Its local bytes were copied without downloading another engine/model; upstream ownership remains with PaddleOCR rights holders.
- The standalone extracted charset.txt is a text representation of embedded ONNX model character metadata.

Lisans dosyaları `licenses/` klasöründedir:
- `Apache-2.0.txt`
- `SkiaSharp-MIT.txt`
- `ONNXRuntime-MIT.txt`
- `ONNXRuntime-ThirdPartyNotices.txt`

RapidOCR model lisans ve atıf rehberi: https://rapidai.github.io/RapidOCRDocs/latest/en/model_licenses/

Bu belgeler teknik atıf kaydıdır; dağıtım öncesi bileşenlerin güncel bildirimleri ayrıca doğrulanmalıdır.
