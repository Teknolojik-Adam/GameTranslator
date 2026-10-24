using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tesseract;

namespace GameTranslatorUltimate
{
    // GameTranslator (.NET 4.8) -> our ONNX OCR (.NET 8) long-running bridge.
    public sealed class GameTranslatorOcrEngine : IOcrEngine, IDisposable
    {
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private Process _process;
        private bool _disposed;
        public OcrEngineType EngineType => OcrEngineType.GameTranslatorOcr;
        public GameTranslatorOcrEngine(ILogger logger) { _logger = logger; }

        private void StartIfNeeded()
        {
            if (_process != null && !_process.HasExited) return;
            StopBridge();
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OwnOCR");
            string exe = Path.Combine(root, "win-x64", "GameTranslatorOCR.Bridge.exe");
            string model = Path.Combine(root, "models", "latin-v6-hybrid");
            if (!File.Exists(exe)) throw new FileNotFoundException("Bizim OCR calistirilabilir dosyasi yok.", exe);
            if (!Directory.Exists(model)) throw new DirectoryNotFoundException("Bizim OCR modeli yok: " + model);
            var process = new Process();
            process.StartInfo = new ProcessStartInfo(exe, "\"" + model + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(exe),
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) _logger.LogWarning("Bizim OCR: " + e.Data);
            };
            process.Start();
            process.BeginErrorReadLine();
            _process = process;
        }

        public async Task<string> RecognizeTextAsync(Bitmap image, string language, PageSegMode psm = PageSegMode.Auto)
        {
            if (image == null) return "";
            if (_disposed) throw new ObjectDisposedException(nameof(GameTranslatorOcrEngine));
            string png;
            using (var ms = new MemoryStream())
            {
                image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                png = Convert.ToBase64String(ms.ToArray());
            }
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                StartIfNeeded();
                string lang = language == "eng" || language == "en" ? "en" : "tr";
                var request = JsonConvert.SerializeObject(new { Image = png, Language = lang });
                await _process.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                var task = _process.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(task, Task.Delay(30000)).ConfigureAwait(false) != task)
                    throw new TimeoutException("Bizim OCR 30 saniye icinde yanit vermedi.");
                var responseLine = await task.ConfigureAwait(false);
                if (responseLine == null) throw new IOException("OCR koprusu kapandi.");
                var result = JObject.Parse(responseLine);
                string error = (string)result["Error"];
                if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                return (string)result["Text"] ?? "";
            }
            catch (Exception ex)
            {
                _logger.LogError("Bizim OCR hatasi", ex);
                StopBridge();
                return "";
            }
            finally { _lock.Release(); }
        }

        private void StopBridge()
        {
            var p = _process;
            _process = null;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
            try { p.Dispose(); } catch { }
        }

        public void Dispose()
        {
            _disposed = true;
            _lock.Wait();
            try { StopBridge(); } finally { _lock.Release(); _lock.Dispose(); }
        }
    }
}
