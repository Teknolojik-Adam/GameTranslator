# 🎮 GameTranslator (OCR & RAM Translation)

<p align="center">
  <img width="800" src="https://github.com/user-attachments/assets/ab335219-6cb8-4216-8ab8-b0da5de723e0" alt="GameTranslator Banner" />
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Language-C%23-blue.svg" alt="C#" />
  <img src="https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-lightgrey.svg" alt="Platform" />
  <img src="https://img.shields.io/badge/OCR-Tesseract%20%7C%20PaddleOCR%20%7C%20CRNN-orange.svg" alt="OCR Engines" />
  <img src="https://img.shields.io/badge/Translation-Real--time-green.svg" alt="Real-time" />
</p>

---

## 🚀 Özel Sürümler / Special Editions

### 🇹🇷 Hızlı Başlangıç Sürümleri
*   **[GameTranslator Tess (Tek Tıkla Çeviri)](https://teknolojikadam.itch.io/gametranslator-tess):** Karmaşık ayarlar istemeyenler için tasarlandı. Sadece bir tuşa basarak ekrandaki metinleri anında çevirin. En basit ve hızlı çözüm!
*   **[GameTranslator Linux Vision](https://teknolojikadam.itch.io/gametranslatorlinux):** Linux kullanıcılarına özel, sistem kaynaklarını optimize eden performans odaklı sürüm.

### 🇺🇸 Quick Start Editions
*   **[GameTranslator Tess (One-Click Translation)](https://teknolojikadam.itch.io/gametranslator-tess):** Designed for users who want a simple experience. Translate screen text instantly with just one click. The most straightforward solution!
*   **[GameTranslator Linux Vision](https://teknolojikadam.itch.io/gametranslatorlinux):** A performance-oriented version specifically optimized for Linux users.

---

## 🇹🇷 Hakkında (Turkish)
**GameTranslator**, oyunlardaki metinleri hem RAM üzerinden doğrudan okuyarak hem de gelişmiş OCR (Optik Karakter Tanıma) tekniklerini kullanarak anlık olarak Türkçeye çeviren hibrit bir yazılımdır. Bilgisayar ekranında görünen her şeyi (Oyun, Anime, Film) saniyeler içinde çevirebilir.

### ✨ Öne Çıkan Özellikler
*   **🧠 Hibrit Çeviri:** Hem RAM (Pointer/Offset) hem de OCR (Ekran Görüntüsü) tabanlı çalışma.
*   **🚀 Gelişmiş OCR Motorları:** Tesseract, Windows OCR, CRNN ve PaddleOCR desteği ile %95+ doğruluk.
*   **🎮 Konsol Desteği:** Remote Play veya IP Webcam kullanarak PS5, Xbox ve Switch oyunlarını çevirme.
*   **⚡ Düşük Gecikme:** RAM üzerinden okuma sayesinde sıfıra yakın gecikme ile metin takibi.
*   
### 🤖 Gelişmiş Yapay Zeka Özellikleri
- **Yerel LLM (Ollama):** Yerel modeller kullanarak internet olmadan çeviri yapar.

- **Anomali Tespiti:** "Çöp" OCR sonuçlarını otomatik olarak filtreler.

- **Bağlamsal Düzeltme:** Oyun jargonunu anlar ve yazım hatalarını gerçek zamanlı olarak düzeltir.

- **Doğruluk Puanlaması:** Birden fazla OCR motorunu karşılaştırır ve en iyi sonucu seçer.
---

## 🇺🇸 About (English)
**GameTranslator** is a hybrid software that provides real-time translation for games by directly reading text from RAM or using advanced OCR (Optical Character Recognition) techniques. It can translate anything visible on your screen (Games, Anime, Movies) into your target language instantly.

### ✨ Key Features
*   **🧠 Hybrid Translation:** Support for both RAM (Pointer/Offset) and OCR (Screen Capture) methods.
*   **🚀 Advanced OCR Engines:** Tesseract, Windows OCR, CRNN, and PaddleOCR support with 95%+ accuracy.
*   **🎮 Console Support:** Translate PS5, Xbox, and Switch games via Remote Play or IP Webcam.
*   **⚡ Low Latency:** Near-zero latency text tracking using direct RAM access.

### 🤖 Advanced AI Features / Gelişmiş Yapay Zeka Özellikleri
- **Local LLM (Ollama):** Translate without internet using local models.
- **Anomaly Detection:** Filters out "garbage" OCR results automatically.
- **Contextual Correction:** Understands game jargon and corrects typos in real-time.
- **Accuracy Scoring:** Compares multiple OCR engines and picks the best result.
---

## 🛠️ Kullanım / How to Use

### 🇹🇷 Türkçe
1.  Programı yönetici olarak başlatın.
2.  Listeden oyun penceresini seçin.
3.  **RAM Çevirisi:** Cheat Engine ile bulduğunuz pointer yolunu girin ve başlatın.
4.  **OCR Çevirisi:** Ekran Çevirisini Başlat butonuna basın. Yazılar otomatik algılanacaktır.

### 🇺🇸 English
1.  Launch the program as administrator.
2.  Select the game window from the list.
3.  **RAM Translation:** Enter the pointer path found via Cheat Engine and start.
4.  **OCR Translation:** Click "Start Screen Translation". Text will be detected automatically.

---

## 📦 Kurulum / Installation

*   **Requirements:** .NET Framework 4.8
*   **Folders:** Ensure `tessdata` and OCR model folders (`crnn`, `paddleocr`) are in the same directory as `program.exe`.

---

## 📸 Screenshots & Flow / Akış Şeması

<p align="center">
  <img width="90%" src="https://github.com/user-attachments/assets/6a55b8e6-836e-4ebf-a699-0a4ae7a32ea2" />
  <img width="90%" src="https://github.com/user-attachments/assets/b9ea624e-4918-4098-9082-6fb3341404fe" />
</p>

---
<p align="center">Developed by <b>Teknolojik-Adam</b></p>
