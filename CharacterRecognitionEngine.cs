// MultipleFiles/CharacterRecognitionEngine.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace P5S_ceviri
{
    public class CharacterRecognitionEngine
    {
        private readonly ILogger _logger;
        private Dictionary<char, Bitmap> _templates = new Dictionary<char, Bitmap>();
        private const int TEMPLATE_SIZE = 20; // Şablonların ve karakterlerin yeniden boyutlandırılacağı boyut
        private const byte BINARIZATION_THRESHOLD = 128; // Şablonları ikiliye çevirirken kullanılacak eşik

        public CharacterRecognitionEngine(ILogger logger)
        {
            _logger = logger;
            LoadTemplates();
        }

        /// <summary>
        /// Şablonları yükler ve ön işler.
        /// Şablonlar, uygulamanın çalıştığı dizindeki "tessdata/templates" klasöründe aranır.
        /// </summary>
        private void LoadTemplates()
        {
            string templateFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata", "templates");

            if (!Directory.Exists(templateFolderPath))
            {
                _logger.LogWarning($"Şablon klasörü bulunamadı: {templateFolderPath}. OCR tanıma çalışmayabilir.");
                return;
            }

            _templates.Clear(); // Önceki şablonları temizle

            foreach (string filePath in Directory.GetFiles(templateFolderPath, "*.png"))
            {
                try
                {
                    char character = Path.GetFileNameWithoutExtension(filePath).ToUpper()[0]; // Dosya adının ilk karakteri (büyük harf)
                    using (Bitmap originalTemplate = new Bitmap(filePath))
                    {
                        using (Bitmap grayscaleTemplate = ImageProcessingUtils.ToGrayscale(originalTemplate))
                        using (Bitmap binaryTemplate = ImageProcessingUtils.ToBinary(grayscaleTemplate, BINARIZATION_THRESHOLD))
                        {
                            Bitmap resizedTemplate = new Bitmap(TEMPLATE_SIZE, TEMPLATE_SIZE, PixelFormat.Format1bppIndexed);
                            using (Graphics g = Graphics.FromImage(resizedTemplate))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                g.DrawImage(binaryTemplate, 0, 0, TEMPLATE_SIZE, TEMPLATE_SIZE);
                            }
                            _templates[character] = resizedTemplate; // Klonlayarak sakla
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Şablon yüklenirken hata oluştu: {filePath}", ex);
                }
            }
            _logger.LogInformation($"{_templates.Count} adet OCR şablonu yüklendi.");
        }

        /// <summary>
        /// Bir karakter görüntüsünü önceden yüklenmiş şablonlarla eşleştirerek tanır.
        /// </summary>
        /// <param name="characterImage">İkili (1bppIndexed) karakter görüntüsü.</param>
        /// <returns>Tanınan karakter veya boşluk (' ') eğer tanınamazsa.</returns>
        public char RecognizeCharacter(Bitmap characterImage)
        {
            if (characterImage == null || characterImage.PixelFormat != PixelFormat.Format1bppIndexed)
            {
                _logger.LogWarning("RecognizeCharacter: Karakter görüntüsü ikili (1bppIndexed) olmalıdır.");
                return ' ';
            }
            if (_templates.Count == 0)
            {
                _logger.LogWarning("RecognizeCharacter: Hiç şablon yüklenmedi. Tanıma yapılamıyor.");
                return ' ';
            }

            Bitmap resizedCharacter = new Bitmap(TEMPLATE_SIZE, TEMPLATE_SIZE, PixelFormat.Format1bppIndexed);
            using (Graphics g = Graphics.FromImage(resizedCharacter))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(characterImage, 0, 0, TEMPLATE_SIZE, TEMPLATE_SIZE);
            }

            BitmapData charData = null;
            byte[] charPixels = null;
            try
            {
                charData = resizedCharacter.LockBits(
                    new Rectangle(0, 0, TEMPLATE_SIZE, TEMPLATE_SIZE),
                    ImageLockMode.ReadOnly,
                    resizedCharacter.PixelFormat);
                charPixels = new byte[charData.Stride * TEMPLATE_SIZE];
                Marshal.Copy(charData.Scan0, charPixels, 0, charPixels.Length);
            }
            finally
            {
                if (charData != null) resizedCharacter.UnlockBits(charData);
            }


            char bestMatchChar = ' ';
            double minDifference = double.MaxValue;

            foreach (var entry in _templates)
            {
                Bitmap template = entry.Value;
                BitmapData templateData = null;
                byte[] templatePixels = null;

                try
                {
                    templateData = template.LockBits(
                        new Rectangle(0, 0, TEMPLATE_SIZE, TEMPLATE_SIZE),
                        ImageLockMode.ReadOnly,
                        template.PixelFormat);
                    templatePixels = new byte[templateData.Stride * TEMPLATE_SIZE];
                    Marshal.Copy(templateData.Scan0, templatePixels, 0, templatePixels.Length);
                }
                finally
                {
                    if (templateData != null) template.UnlockBits(templateData);
                }

                double currentDifference = 0;
                for (int i = 0; i < charPixels.Length; i++)
                {
                    byte charByte = charPixels[i];
                    byte templateByte = templatePixels[i];

                    for (int bit = 0; bit < 8; bit++)
                    {
                        bool charPixel = ((charByte >> (7 - bit)) & 1) == 1;
                        bool templatePixel = ((templateByte >> (7 - bit)) & 1) == 1;

                        if (charPixel != templatePixel)
                        {
                            currentDifference += 1;
                        }
                    }
                }

                if (currentDifference < minDifference)
                {
                    minDifference = currentDifference;
                    bestMatchChar = entry.Key;
                }
            }

            // Tanıma eşiği: Toplam piksel sayısının %15'inden fazlası farklıysa tanınamadı say
            double maxAllowedDifference = (TEMPLATE_SIZE * TEMPLATE_SIZE) * 0.15;
            if (minDifference > maxAllowedDifference)
            {
                return ' '; // Tanınamadı
            }

            return bestMatchChar;
        }

        // Kaynakları serbest bırak
        public void Dispose()
        {
            foreach (var template in _templates.Values)
            {
                template.Dispose();
            }
            _templates.Clear();
        }
    }
}
