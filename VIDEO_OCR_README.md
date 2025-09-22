# Video OCR - Real-time Text Recognition System

Bu dokümantasyon, GameTranslator uygulamasına eklenen gelişmiş video OCR (Optical Character Recognition) sistemini açıklar.

## 🎯 Özellikler

### 1. Gerçek Zamanlı Video OCR
- **Video Akışı Yakalama**: Webcam veya video cihazlarından gerçek zamanlı görüntü yakalama
- **Çoklu Çözünürlük Desteği**: 640x480, 800x600, 1024x768, 1280x720 çözünürlükleri
- **Ayarlanabilir Frame Rate**: 1-60 FPS arası ayarlanabilir frame hızı
- **Otomatik Cihaz Algılama**: Mevcut kamera cihazlarını otomatik algılama

### 2. Çoklu OCR Motoru Karşılaştırması
- **Tesseract OCR**: Açık kaynak OCR motoru
- **Windows OCR**: Windows'un yerleşik OCR motoru
- **Paralel İşleme**: Tüm motorları aynı anda çalıştırarak en iyi sonucu seçme
- **Performans Analizi**: Her motorun işlem süresi ve doğruluk oranı karşılaştırması

### 3. OCR Doğruluk Skorlaması
- **Karakter Seviyesi Doğruluk**: Levenshtein mesafesi ile karakter bazlı doğruluk
- **Kelime Seviyesi Doğruluk**: Kelime bazlı doğruluk hesaplama
- **Satır Seviyesi Doğruluk**: Satır bazlı doğruluk analizi
- **Güven Skoru**: Metin ve görüntü özelliklerine dayalı güven skoru
- **Ground Truth Karşılaştırması**: Beklenen metin ile karşılaştırma

## 🏗️ Sistem Mimarisi

### Servis Katmanı
```
IVideoCaptureService
├── VideoCaptureService (OpenCV tabanlı)
│   ├── Gerçek zamanlı video yakalama
│   ├── Frame rate kontrolü
│   └── Cihaz yönetimi

IOcrComparisonService
├── OcrComparisonService
│   ├── Çoklu motor karşılaştırması
│   ├── En iyi motor seçimi
│   └── Performans analizi

IOcrAccuracyService
├── OcrAccuracyService
│   ├── Doğruluk hesaplama
│   ├── Levenshtein mesafesi
│   └── Güven skoru hesaplama

IRealtimeVideoOcrService
├── RealtimeVideoOcrService
│   ├── Ana koordinatör servis
│   ├── Event yönetimi
│   └── Sonuç birleştirme
```

### UI Katmanı
```
VideoOcrWindow
├── Video görüntüleme
├── OCR sonuçları
├── Kontrol paneli
└── İstatistikler

AccuracyReportWindow
├── Detaylı raporlar
├── Grafik görünümler
└── Export özellikleri
```

## 🚀 Kullanım

### 1. Video OCR Başlatma
1. Ana uygulamada "Video OCR Aç" butonuna tıklayın
2. Kamera cihazını seçin
3. Frame rate ve çözünürlük ayarlarını yapın
4. "Start Video OCR" butonuna tıklayın

### 2. OCR Ayarları
- **Engine Comparison**: Çoklu motor karşılaştırmasını etkinleştirir
- **Accuracy Scoring**: Doğruluk skorlamasını etkinleştirir
- **Region Detection**: Metin bölgesi algılamasını etkinleştirir
- **Confidence Threshold**: Minimum güven eşiği ayarı

### 3. Ground Truth Ayarlama
- Beklenen metni "Ground Truth" alanına girin
- Bu metin doğruluk hesaplamalarında referans olarak kullanılır

### 4. Rapor Oluşturma
- "Generate Report" butonuna tıklayarak detaylı doğruluk raporu oluşturun
- Raporu TXT veya CSV formatında export edebilirsiniz

## 📊 Doğruluk Metrikleri

### Karakter Seviyesi Doğruluk
```csharp
// Levenshtein mesafesi ile karakter bazlı doğruluk
var distance = LevenshteinDistance(recognizedChars, groundTruthChars);
var accuracy = 1.0 - (double)distance / groundTruthChars.Length;
```

### Kelime Seviyesi Doğruluk
```csharp
// Kelime bazlı doğruluk hesaplama
var recognizedWords = recognized.Split(' ');
var groundTruthWords = groundTruth.Split(' ');
var wordAccuracy = CalculateWordAccuracy(recognizedWords, groundTruthWords);
```

### Güven Skoru
```csharp
// Metin ve görüntü özelliklerine dayalı güven skoru
var textConfidence = CalculateTextBasedConfidence(recognizedText);
var imageConfidence = CalculateImageBasedConfidence(sourceImage);
var overallConfidence = (textConfidence + imageConfidence) / 2.0;
```

## ⚙️ Konfigürasyon

### AppSettings Yeni Özellikleri
```csharp
// Video OCR Ayarları
public bool EnableVideoOcr { get; set; } = false;
public int VideoOcrFrameRate { get; set; } = 30;
public int VideoOcrWidth { get; set; } = 640;
public int VideoOcrHeight { get; set; } = 480;
public bool EnableOcrComparison { get; set; } = true;
public bool EnableOcrAccuracyScoring { get; set; } = false;
public int VideoOcrDeviceIndex { get; set; } = 0;
public bool EnableOcrRegionDetection { get; set; } = true;
public double OcrConfidenceThreshold { get; set; } = 0.7;
public int OcrResultHistorySize { get; set; } = 100;
```

## 🔧 Teknik Detaylar

### Video Yakalama
- **OpenCV VideoCapture** kullanılarak gerçek zamanlı video yakalama
- **Asenkron işleme** ile UI donmaması
- **Otomatik cihaz algılama** ve hata yönetimi

### OCR Motorları
- **Tesseract**: Yapılandırılabilir parametreler ile optimize edilmiş
- **Windows OCR**: Windows API'si ile yerleşik OCR
- **Paralel işleme**: Task.WhenAll ile eşzamanlı çalıştırma

### Doğruluk Hesaplama
- **Levenshtein Distance**: Karakter, kelime ve satır seviyesi karşılaştırma
- **Normalizasyon**: Metin ön işleme ve karşılaştırma için
- **Çoklu metrik**: Farklı seviyelerde doğruluk hesaplama

## 📈 Performans Optimizasyonları

### Bellek Yönetimi
- **IDisposable** pattern ile kaynak temizliği
- **ConcurrentQueue** ile thread-safe sonuç yönetimi
- **Otomatik temizlik** ile bellek sızıntısı önleme

### İşlem Optimizasyonları
- **Asenkron işleme** ile UI responsiveness
- **Paralel OCR** ile hız artırma
- **Akıllı frame atlama** ile CPU kullanımı optimizasyonu

## 🐛 Hata Yönetimi

### Video Hataları
- Kamera erişim hataları
- Cihaz bulunamama durumları
- Frame yakalama hataları

### OCR Hataları
- Motor başlatma hataları
- İşlem zaman aşımı
- Bellek yetersizliği

### UI Hataları
- Thread güvenliği
- Event handler hataları
- Dispose pattern uygulaması

## 🔮 Gelecek Geliştirmeler

### Planlanan Özellikler
- **GPU Accelerated OCR**: CUDA/OpenCL desteği
- **Deep Learning Models**: Özel eğitilmiş modeller
- **Multi-language Support**: Çoklu dil desteği
- **Cloud OCR Integration**: Bulut tabanlı OCR servisleri
- **Real-time Translation**: OCR sonuçlarının anlık çevirisi

### Performans İyileştirmeleri
- **Frame Skipping**: Akıllı frame atlama
- **Region of Interest**: İlgi alanı odaklı işleme
- **Adaptive Quality**: Dinamik kalite ayarlama
- **Caching**: Sonuç önbellekleme

## 📝 Lisans ve Katkıda Bulunma

Bu video OCR sistemi, mevcut GameTranslator projesinin bir parçasıdır ve aynı lisans koşulları altında dağıtılmaktadır.

### Katkıda Bulunma
1. Fork yapın
2. Feature branch oluşturun
3. Değişikliklerinizi commit edin
4. Pull request gönderin

### Test Etme
- Farklı kamera cihazları ile test edin
- Çeşitli metin türleri ile doğruluk testleri yapın
- Performans testleri gerçekleştirin
- Hata senaryolarını test edin

## 📞 Destek

Herhangi bir sorun veya öneri için:
- GitHub Issues kullanın
- Detaylı hata loglarını paylaşın
- Sistem özelliklerinizi belirtin
- Beklenen ve gerçek davranışları açıklayın
