using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameTranslatorUltimate
{
    public sealed class EnhancedMemoryService : MemoryService
    {
        public event Action<string> StatusChanged;
        public event Action<int> ProgressChanged;

        public EnhancedMemoryService(
            ILogger logger)
            : base(
                logger ?? throw new ArgumentNullException(nameof(logger)),
                new AppSettings(logger))
        {
            _logger.LogInformation(
                "EnhancedMemoryService başlatıldı.");
        }

        public EnhancedMemoryService(
            ILogger logger,
            AppSettings appSettings)
            : base(
                logger ?? throw new ArgumentNullException(nameof(logger)),
                appSettings ?? throw new ArgumentNullException(nameof(appSettings)))
        {
            _logger.LogInformation(
                "EnhancedMemoryService başlatıldı (AppSettings ile).");
        }

        protected void ReportStatus(
            string status)
        {
            Action<string> handler =
                StatusChanged;

            if (handler == null)
                return;

            try
            {
                handler(
                    status ?? string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "StatusChanged event'i çalıştırılırken hata oluştu.",
                    ex);
            }
        }

        protected void ReportProgress(
            int progress)
        {
            progress =
                Math.Max(
                    0,
                    Math.Min(
                        100,
                        progress));

            Action<int> handler =
                ProgressChanged;

            if (handler == null)
                return;

            try
            {
                handler(
                    progress);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "ProgressChanged event'i çalıştırılırken hata oluştu.",
                    ex);
            }
        }

        public Task<List<IntPtr>> FindPatternAddressesAsync(
            Process process,
            string pattern,
            CancellationToken ct,
            IProgress<int> progress = null)
        {
            return FindPatternAddressesAsync(
                process,
                pattern,
                ct,
                progress,
                4 * 1024 * 1024,
                1024 * 1024,
                true);
        }

        public Task<List<IntPtr>> FindPatternAddressesAsync(
            Process process,
            string pattern,
            CancellationToken ct,
            IProgress<int> progress,
            int chunkSize)
        {
            return FindPatternAddressesAsync(
                process,
                pattern,
                ct,
                progress,
                chunkSize,
                1024 * 1024,
                true);
        }

        public Task<List<IntPtr>> FindPatternAddressesAsync(
            Process process,
            string pattern,
            CancellationToken ct,
            IProgress<int> progress,
            int chunkSize,
            int bufferSize)
        {
            return FindPatternAddressesAsync(
                process,
                pattern,
                ct,
                progress,
                chunkSize,
                bufferSize,
                true);
        }

        public async Task<List<IntPtr>> FindPatternAddressesAsync(
            Process process,
            string pattern,
            CancellationToken ct,
            IProgress<int> progress,
            int chunkSize,
            int bufferSize,
            bool useOverlappingBuffers)
        {
            if (process == null)
            {
                ReportStatus(
                    "Geçersiz process.");

                _logger.LogWarning(
                    "Pattern taraması için process null.");

                return new List<IntPtr>();
            }

            if (string.IsNullOrWhiteSpace(
                pattern))
            {
                ReportStatus(
                    "Geçersiz pattern.");

                _logger.LogWarning(
                    "Pattern boş olamaz.");

                return new List<IntPtr>();
            }

            if (chunkSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(chunkSize));
            }

            if (bufferSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bufferSize));
            }

            try
            {
                ct.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    ReportStatus(
                        "Process kapanmış.");

                    return new List<IntPtr>();
                }

                ReportStatus(
                    "Pattern ayrıştırılıyor...");

                PatternData parsedPattern =
                    ParsePattern(
                        pattern);

                if (parsedPattern == null ||
                    parsedPattern.Bytes.Length == 0)
                {
                    ReportStatus(
                        "Geçersiz pattern.");

                    _logger.LogError(
                        $"Geçersiz pattern formatı: {pattern}");

                    return new List<IntPtr>();
                }

                int patternLength =
                    parsedPattern.Bytes.Length;

                if (bufferSize < patternLength)
                {
                    bufferSize =
                        patternLength;
                }

                int overlap =
                    useOverlappingBuffers
                        ? Math.Max(
                            0,
                            patternLength - 1)
                        : 0;

                int step =
                    bufferSize - overlap;

                if (step <= 0)
                {
                    step =
                        1;
                }

                ProcessModule module =
                    process.MainModule;

                if (module == null)
                {
                    ReportStatus(
                        "Ana modül bulunamadı.");

                    _logger.LogError(
                        "Ana modül bulunamadı.");

                    return new List<IntPtr>();
                }

                long moduleStart =
                    module.BaseAddress.ToInt64();

                long moduleSize =
                    module.ModuleMemorySize;

                if (moduleSize <= 0)
                {
                    ReportStatus(
                        "Ana modül boyutu geçersiz.");

                    return new List<IntPtr>();
                }

                long moduleEnd =
                    checked(
                        moduleStart +
                        moduleSize);

                ReportStatus(
                    "Bellek bölgeleri taranıyor...");

                List<MemoryChunk> chunks =
                    BuildChunks(
                        moduleStart,
                        moduleEnd,
                        chunkSize,
                        patternLength);

                var results =
                    new HashSet<long>();

                object resultsLock =
                    new object();

                long completedChunkBytes =
                    0;

                int lastProgress =
                    -1;

                await Task.Run(
                    () =>
                    {
                        Parallel.ForEach(
                            chunks,
                            new ParallelOptions
                            {
                                CancellationToken =
                                    ct,

                                MaxDegreeOfParallelism =
                                    Math.Max(
                                        1,
                                        Environment.ProcessorCount)
                            },
                            chunk =>
                            {
                                ct.ThrowIfCancellationRequested();

                                ScanChunk(
                                    process,
                                    chunk,
                                    parsedPattern,
                                    bufferSize,
                                    step,
                                    results,
                                    resultsLock,
                                    ct);

                                long completed =
                                    Interlocked.Add(
                                        ref completedChunkBytes,
                                        chunk.ProgressSize);

                                int currentProgress =
                                    moduleSize > 0
                                        ? (int)Math.Min(
                                            100,
                                            completed * 100L /
                                            moduleSize)
                                        : 100;

                                int previous =
                                    Interlocked.Exchange(
                                        ref lastProgress,
                                        currentProgress);

                                if (previous != currentProgress)
                                {
                                    ReportProgress(
                                        currentProgress);

                                    if (progress != null)
                                    {
                                        progress.Report(
                                            currentProgress);
                                    }
                                }
                            });
                    },
                    ct);

                ct.ThrowIfCancellationRequested();

                List<IntPtr> finalResults;

                lock (resultsLock)
                {
                    finalResults =
                        results
                            .OrderBy(
                                address => address)
                            .Select(
                                address => new IntPtr(address))
                            .ToList();
                }

                ReportProgress(
                    100);

                if (progress != null)
                {
                    progress.Report(
                        100);
                }

                ReportStatus(
                    $"Tarama tamamlandı. {finalResults.Count} adet sonuç bulundu.");

                _logger.LogInformation(
                    $"Pattern taraması tamamlandı. {finalResults.Count} adet sonuç bulundu.");

                return finalResults;
            }
            catch (OperationCanceledException)
            {
                ReportStatus(
                    "Pattern taraması kullanıcı tarafından durduruldu.");

                _logger.LogInformation(
                    "Pattern taraması kullanıcı tarafından durduruldu.");

                return new List<IntPtr>();
            }
            catch (Exception ex)
            {
                ReportStatus(
                    $"Tarama sırasında hata: {ex.Message}");

                _logger.LogError(
                    "Pattern taraması sırasında hata oluştu.",
                    ex);

                return new List<IntPtr>();
            }
        }

        private void ScanChunk(
            Process process,
            MemoryChunk chunk,
            PatternData pattern,
            int bufferSize,
            int step,
            HashSet<long> results,
            object resultsLock,
            CancellationToken ct)
        {
            long chunkEnd =
                chunk.Start +
                chunk.ScanSize;

            byte[] buffer =
                new byte[bufferSize];

            for (long address = chunk.Start;
                 address < chunkEnd;
                 address += step)
            {
                ct.ThrowIfCancellationRequested();

                long remaining =
                    chunkEnd -
                    address;

                if (remaining <= 0)
                    break;

                int readSize =
                    (int)Math.Min(
                        buffer.Length,
                        remaining);

                int bytesRead;

                bool success =
                    ReadProcessMemory(
                        process.Handle,
                        new IntPtr(address),
                        buffer,
                        readSize,
                        out bytesRead);

                if (!success ||
                    bytesRead <= 0)
                {
                    continue;
                }

                int maxOffset =
                    bytesRead -
                    pattern.Bytes.Length;

                if (maxOffset < 0)
                    continue;

                for (int offset = 0;
                     offset <= maxOffset;
                     offset++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!MatchesWithMask(
                        buffer,
                        offset,
                        bytesRead,
                        pattern.Bytes,
                        pattern.Mask))
                    {
                        continue;
                    }

                    long resultAddress =
                        address +
                        offset;

                    if (resultAddress < chunk.ResultStart ||
                        resultAddress >= chunk.ResultEnd)
                    {
                        continue;
                    }

                    lock (resultsLock)
                    {
                        results.Add(
                            resultAddress);
                    }
                }
            }
        }

        private static List<MemoryChunk> BuildChunks(
            long moduleStart,
            long moduleEnd,
            int chunkSize,
            int patternLength)
        {
            var chunks =
                new List<MemoryChunk>();

            long current =
                moduleStart;

            while (current < moduleEnd)
            {
                long logicalEnd =
                    Math.Min(
                        moduleEnd,
                        current +
                        chunkSize);

                long scanEnd =
                    logicalEnd;

                if (logicalEnd < moduleEnd &&
                    patternLength > 1)
                {
                    scanEnd =
                        Math.Min(
                            moduleEnd,
                            logicalEnd +
                            patternLength -
                            1);
                }

                chunks.Add(
                    new MemoryChunk
                    {
                        Start =
                            current,

                        ScanSize =
                            scanEnd -
                            current,

                        ProgressSize =
                            logicalEnd -
                            current,

                        ResultStart =
                            current,

                        ResultEnd =
                            logicalEnd
                    });

                current =
                    logicalEnd;
            }

            return chunks;
        }

        private PatternData ParsePattern(
            string pattern)
        {
            string[] parts =
                pattern.Split(
                    new[]
                    {
                        ' ',
                        '\t',
                        '\r',
                        '\n'
                    },
                    StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
                return null;

            byte[] bytes =
                new byte[parts.Length];

            bool[] mask =
                new bool[parts.Length];

            for (int i = 0;
                 i < parts.Length;
                 i++)
            {
                string part =
                    parts[i];

                if (part == "?" ||
                    part == "??")
                {
                    bytes[i] =
                        0;

                    mask[i] =
                        false;

                    continue;
                }

                byte value;

                if (!byte.TryParse(
                    part,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out value))
                {
                    _logger.LogError(
                        $"Geçersiz pattern parçası: {part}");

                    return null;
                }

                bytes[i] =
                    value;

                mask[i] =
                    true;
            }

            return new PatternData(
                bytes,
                mask);
        }

        private static bool MatchesWithMask(
            byte[] buffer,
            int offset,
            int bytesRead,
            byte[] pattern,
            bool[] mask)
        {
            if (buffer == null ||
                pattern == null ||
                mask == null)
            {
                return false;
            }

            if (pattern.Length == 0 ||
                pattern.Length != mask.Length)
            {
                return false;
            }

            if (offset < 0 ||
                offset + pattern.Length >
                bytesRead)
            {
                return false;
            }

            for (int i = 0;
                 i < pattern.Length;
                 i++)
            {
                if (mask[i] &&
                    buffer[offset + i] !=
                    pattern[i])
                {
                    return false;
                }
            }

            return true;
        }

        public new bool AttachToProcess(
            int processId)
        {
            bool success =
                base.AttachToProcess(
                    processId);

            if (!success)
            {
                ReportStatus(
                    $"Process'e bağlanılamadı (ID: {processId}).");

                return false;
            }

            try
            {
                using (Process process =
                       Process.GetProcessById(
                           processId))
                {
                    ReportStatus(
                        $"Process'e başarıyla bağlanıldı (ID: {processId}, Adı: {process.ProcessName}).");
                }
            }
            catch
            {
                ReportStatus(
                    $"Process'e başarıyla bağlanıldı (ID: {processId}).");
            }

            return true;
        }

        public void ReportStatusWithTimestamp(
            string status)
        {
            ReportStatus(
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {status}");
        }

        public void ReportProgressWithTimestamp(
            int progress)
        {
            int normalizedProgress =
                Math.Max(
                    0,
                    Math.Min(
                        100,
                        progress));

            ReportProgress(
                normalizedProgress);

            ReportStatusWithTimestamp(
                $"İlerleme: {normalizedProgress}%");
        }

        public new void Dispose()
        {
            base.Dispose();

            ReportStatus(
                "EnhancedMemoryService kapatıldı.");

            _logger.LogInformation(
                "EnhancedMemoryService kapatıldı.");
        }

        private sealed class PatternData
        {
            public byte[] Bytes { get; private set; }

            public bool[] Mask { get; private set; }

            public PatternData(
                byte[] bytes,
                bool[] mask)
            {
                Bytes =
                    bytes ??
                    new byte[0];

                Mask =
                    mask ??
                    new bool[0];
            }
        }

        private sealed class MemoryChunk
        {
            public long Start { get; set; }

            public long ScanSize { get; set; }

            public long ProgressSize { get; set; }

            public long ResultStart { get; set; }

            public long ResultEnd { get; set; }
        }
    }
}