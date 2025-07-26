using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace P5S_ceviri
{
    public class ProcessService : IProcessService
    {
        private readonly ILogger _logger;

        private List<Process> _processes = new List<Process>();

        public ProcessService(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        public IEnumerable<Process> GetProcesses()
        {
            return _processes.AsReadOnly();
        }
        // Çalışan işlem listesini yeniler, erişilemeyen veya kapanmış olanları filtreler.
        public void RefreshProcesses()
        {
            _logger.LogInformation("Proses listesi yenileniyor...");
            try
            {
                _processes.Clear();

                _processes = Process.GetProcesses()
                                    .Where(IsProcessAccessible)
                                    .ToList();

                _logger.LogInformation($"Toplam {_processes.Count} adet erişilebilir proses bulundu.");
            }
            catch (Exception ex)
            {
                _logger.LogError("Proses listesi yenilenirken bir hata oluştu.", ex);
                _processes.Clear();
            }
        }

        private bool IsProcessAccessible(Process p)
        {
            try
            {
             
                return !p.HasExited && p.MainModule != null;
            }
            catch
            {
                return false;
            }
        }
    }
}