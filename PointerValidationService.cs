using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class PointerValidationResult
    {
        public PointerPath Path { get; set; }
        public bool IsValid { get; set; }
        public string CurrentValue { get; set; }
        public string ExpectedValue { get; set; }
        public string ErrorMessage { get; set; }
        public int Score { get; set; } 
        public TimeSpan ResponseTime { get; set; }
    }

    public class PointerValidationService
    {
        private readonly IMemoryService _memoryService;
        private readonly ILogger _logger;

        public PointerValidationService(IMemoryService memoryService, ILogger logger)
        {
            _memoryService = memoryService;
            _logger = logger;
        }


        public async Task<List<PointerValidationResult>> ValidatePointersAsync(Process process, 
            List<PointerPath> paths, string expectedText = null)
        {
            var results = new List<PointerValidationResult>();

            if (!_memoryService.AttachToProcess(process.Id))
            {
                _logger.LogError("Process'e bağlanılamadı - pointer validation başarısız");
                return results;
            }

            foreach (var path in paths)
            {
                var result = await ValidateSinglePointerAsync(process, path, expectedText);
                results.Add(result);
            }

            // Sonuçları score'a göre sırala (en iyi önce)
            return results.OrderByDescending(r => r.Score).ToList();
        }

        private async Task<PointerValidationResult> ValidateSinglePointerAsync(Process process, 
            PointerPath path, string expectedText)
        {
            var result = new PointerValidationResult
            {
                Path = path,
                IsValid = false,
                Score = 0
            };

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // PathInfo'ya çevir
                var pathInfo = ConvertToPathInfo(path);
                if (pathInfo == null)
                {
                    result.ErrorMessage = "Geçersiz pointer formatı";
                    return result;
                }

                // Adresi resolve et
                var resolvedAddress = _memoryService.ResolveAddressFromPath(process, pathInfo);
                if (resolvedAddress == IntPtr.Zero)
                {
                    result.ErrorMessage = "Adres resolve edilemedi";
                    return result;
                }

                // Metni oku
                var currentValue = _memoryService.TryReadStringDeep(resolvedAddress);
                result.CurrentValue = currentValue;

                stopwatch.Stop();
                result.ResponseTime = stopwatch.Elapsed;

                // Doğrulama
                if (string.IsNullOrEmpty(currentValue))
                {
                    result.ErrorMessage = "Boş veri okundu";
                    result.Score = 0;
                }
                else if (!string.IsNullOrEmpty(expectedText))
                {
                    
                    result.ExpectedValue = expectedText;
                    if (currentValue.Contains(expectedText) || expectedText.Contains(currentValue))
                    {
                        result.IsValid = true;
                        result.Score = CalculateSimilarityScore(currentValue, expectedText);
                    }
                    else
                    {
                        result.ErrorMessage = "Beklenen metin bulunamadı";
                        result.Score = 10;
                    }
                }
                else
                {
                    // Genel kalite kontrolü
                    result.IsValid = IsValidGameText(currentValue);
                    result.Score = CalculateQualityScore(currentValue, path);
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Hata: {ex.Message}";
                result.Score = 0;
                stopwatch.Stop();
                result.ResponseTime = stopwatch.Elapsed;
            }

            return result;
        }

        public async Task<PointerStabilityResult> TestPointerStabilityAsync(Process process, 
            PointerPath path, int testDurationSeconds = 30, int sampleIntervalMs = 1000)
        {
            var result = new PointerStabilityResult
            {
                Path = path,
                TestDuration = TimeSpan.FromSeconds(testDurationSeconds),
                Samples = new List<StabilitySample>()
            };

            var pathInfo = ConvertToPathInfo(path);
            if (pathInfo == null)
            {
                result.ErrorMessage = "Geçersiz pointer formatı";
                return result;
            }

            var startTime = DateTime.Now;
            var endTime = startTime.AddSeconds(testDurationSeconds);

            while (DateTime.Now < endTime)
            {
                try
                {
                    var address = _memoryService.ResolveAddressFromPath(process, pathInfo);
                    var value = address != IntPtr.Zero ? _memoryService.TryReadStringDeep(address) : null;
                    
                    result.Samples.Add(new StabilitySample
                    {
                        Timestamp = DateTime.Now,
                        Address = address,
                        Value = value,
                        IsSuccessful = !string.IsNullOrEmpty(value)
                    });

                    await Task.Delay(sampleIntervalMs);
                }
                catch (Exception ex)
                {
                    result.Samples.Add(new StabilitySample
                    {
                        Timestamp = DateTime.Now,
                        Address = IntPtr.Zero,
                        Value = null,
                        IsSuccessful = false,
                        ErrorMessage = ex.Message
                    });
                }
            }

            // Stabilite skorunu hesapla
            result.CalculateStabilityMetrics();
            return result;
        }

        private PathInfo ConvertToPathInfo(PointerPath path)
        {
            try
            {
                if (path.ModuleName == "[EXTERNAL]")
                {
                    
                    return null; 
                }

                return new PathInfo
                {
                    BaseAddressModule = path.ModuleName,
                    BaseAddressOffset = path.BaseOffset,
                    PointerOffsets = path.Offsets
                };
            }
            catch
            {
                return null;
            }
        }

        private int CalculateSimilarityScore(string current, string expected)
        {
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(expected))
                return 0;

            if (current == expected)
                return 100;

            if (current.Contains(expected) || expected.Contains(current))
                return 80;

            
            var distance = LevenshteinDistance(current, expected);
            var maxLength = Math.Max(current.Length, expected.Length);
            var similarity = (1.0 - (double)distance / maxLength) * 100;
            
            return Math.Max(0, (int)similarity);
        }

        private int CalculateQualityScore(string text, PointerPath path)
        {
            int score = 0;

            // Metin kalitesi
            if (IsValidGameText(text))
                score += 30;

            // Uzunluk kontrolü
            if (text.Length >= 3 && text.Length <= 200)
                score += 20;

            // Pointer depth penalty (daha az depth = daha iyi)
            score += Math.Max(0, 30 - (path.Offsets.Count * 5));

            // Module içi bonus
            if (path.ModuleName != "[EXTERNAL]")
                score += 20;

            return Math.Min(100, score);
        }

        private bool IsValidGameText(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Length < 2 || s.Length > 1000) 
                return false;
            
            if (s.Contains('\uFFFD')) 
                return false;
                
            // Yazdırılabilir karakter oranı
            int printableCount = s.Count(c => !char.IsControl(c) || char.IsWhiteSpace(c));
            return (double)printableCount / s.Length >= 0.8 && s.Any(char.IsLetterOrDigit);
        }

        private int LevenshteinDistance(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1)) return s2?.Length ?? 0;
            if (string.IsNullOrEmpty(s2)) return s1.Length;

            int[,] matrix = new int[s1.Length + 1, s2.Length + 1];

            for (int i = 0; i <= s1.Length; i++)
                matrix[i, 0] = i;
            for (int j = 0; j <= s2.Length; j++)
                matrix[0, j] = j;

            for (int i = 1; i <= s1.Length; i++)
            {
                for (int j = 1; j <= s2.Length; j++)
                {
                    int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                }
            }

            return matrix[s1.Length, s2.Length];
        }
    }

    public class PointerStabilityResult
    {
        public PointerPath Path { get; set; }
        public TimeSpan TestDuration { get; set; }
        public List<StabilitySample> Samples { get; set; } = new List<StabilitySample>();
        public double SuccessRate { get; set; }
        public double AddressConsistency { get; set; }
        public double ValueConsistency { get; set; }
        public int StabilityScore { get; set; }
        public string ErrorMessage { get; set; }

        public void CalculateStabilityMetrics()
        {
            if (!Samples.Any()) return;

           
            SuccessRate = (double)Samples.Count(s => s.IsSuccessful) / Samples.Count * 100;

           
            var addresses = Samples.Where(s => s.Address != IntPtr.Zero).Select(s => s.Address).Distinct();
            AddressConsistency = addresses.Count() <= 1 ? 100 : 0;

           
            var values = Samples.Where(s => !string.IsNullOrEmpty(s.Value)).Select(s => s.Value).Distinct();
            ValueConsistency = values.Count() <= 3 ? 100 : Math.Max(0, 100 - (values.Count() * 10));

            StabilityScore = (int)((SuccessRate * 0.4) + (AddressConsistency * 0.3) + (ValueConsistency * 0.3));
        }
    }

    public class StabilitySample
    {
        public DateTime Timestamp { get; set; }
        public IntPtr Address { get; set; }
        public string Value { get; set; }
        public bool IsSuccessful { get; set; }
        public string ErrorMessage { get; set; }
    }
} 