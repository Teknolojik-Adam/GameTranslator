using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Timers; 

namespace P5S_ceviri
{
    public class ProcessService : IProcessService, IDisposable
    {
        private readonly ILogger _logger;
        private List<Process> _processes = new List<Process>();
        private Timer _refreshTimer;
        private readonly object _lock = new object();

      
        public class ProcessInfo
        {
            public int Id { get; set; }
            public string ProcessName { get; set; }
            public DateTime StartTime { get; set; }
            public string MainModuleFileName { get; set; }
            public float CpuUsage { get; set; }
            public long MemoryUsageBytes { get; set; }
        }

        public ProcessService(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            RefreshProcesses(); // Başlangıçta bir kez listeyi doldur.
            StartAutoRefresh();
        }

        public IEnumerable<Process> GetProcesses()
        {
            lock (_lock)
            {
                
                return new List<Process>(_processes);
            }
        }

        public void RefreshProcesses()
        {
            _logger.LogInformation("Proses listesi yenileniyor...");
            try
            {
                var accessibleProcesses = Process.GetProcesses()
                                                 .Where(IsProcessAccessible)
                                                 .ToList();
                lock (_lock)
                {
                    _processes = accessibleProcesses;
                }
                _logger.LogInformation($"Toplam {_processes.Count} adet erişilebilir proses bulundu.");
            }
            catch (Exception ex)
            {
                _logger.LogError("Proses listesi yenilenirken bir hata oluştu.", ex);
                lock (_lock)
                {
                    _processes.Clear();
                }
            }
        }

        private bool IsProcessAccessible(Process p)
        {
            try
            {
                // Sistem ve korumalı işlemlere erişmeye çalışırken oluşacak hataları engellemek için.
                return !p.HasExited && p.MainModule != null;
            }
            catch
            {
                return false;
            }
        }

        public IEnumerable<ProcessInfo> GetProcessInfos()
        {
            var currentProcesses = GetProcesses(); // Kilitli ve güvenli kopyayı al.

            return currentProcesses.Select(p =>
            {
                try
                {
                    return new ProcessInfo
                    {
                        Id = p.Id,
                        ProcessName = p.ProcessName,
                        StartTime = p.StartTime,
                        MainModuleFileName = p.MainModule?.FileName,
                        CpuUsage = GetCpuUsage(p),
                        MemoryUsageBytes = p.WorkingSet64
                    };
                }
                catch (Exception)
                {
                    // Eğer işlem bu arada kapandıysa null döner
                    return null;
                }
            }).Where(pi => pi != null).ToList(); // Null olanları listeden çıkarmak için.
        }

        private float GetCpuUsage(Process process)
        {
            try
            {
                // Bu hesaplama, işlemin başlangıcından bu yana olan ORTALAMA CPU kullanımı analiz etmek için
                var totalProcessorTime = process.TotalProcessorTime;
                var runTime = DateTime.Now - process.StartTime;
                if (runTime.TotalMilliseconds > 0)
                {
                    return (float)(totalProcessorTime.TotalMilliseconds / (Environment.ProcessorCount * runTime.TotalMilliseconds) * 100);
                }
            }
            catch {}
            return 0f;
        }

        public IEnumerable<ProcessInfo> FilterProcesses(Func<ProcessInfo, bool> predicate)
        {
            return GetProcessInfos().Where(predicate);
        }

        private void StartAutoRefresh()
        {
            _refreshTimer = new Timer(30000); // 30 saniyede bir
            _refreshTimer.Elapsed += (sender, e) => RefreshProcesses();
            _refreshTimer.AutoReset = true;
            _refreshTimer.Enabled = true;
        }

        public void Dispose()
        {
            _refreshTimer?.Stop();
            _refreshTimer?.Dispose();
        }
    }
}