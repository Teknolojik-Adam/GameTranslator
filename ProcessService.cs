using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace GameTranslatorUltimate
{
    public class ProcessService : IProcessService, IDisposable
    {
        private sealed class CpuSample
        {
            public TimeSpan ProcessorTime { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class ProcessInfo
        {
            public int Id { get; set; }
            public string ProcessName { get; set; }
            public DateTime StartTime { get; set; }
            public string MainModuleFileName { get; set; }
            public float CpuUsage { get; set; }
            public long MemoryUsageBytes { get; set; }
        }

        private const int RefreshIntervalMilliseconds = 30000;

        private readonly ILogger _logger;
        private readonly object _dataLock = new object();

        private readonly Dictionary<int, CpuSample> _cpuSamples =
            new Dictionary<int, CpuSample>();

        private List<ProcessInfo> _processInfos =
            new List<ProcessInfo>();

        private Timer _refreshTimer;

        private int _refreshing;
        private int _disposed;

        public ProcessService(ILogger logger)
        {
            _logger =
                logger ?? throw new ArgumentNullException(nameof(logger));

            RefreshProcesses();
            StartAutoRefresh();
        }

        public IEnumerable<Process> GetProcesses()
        {
            ThrowIfDisposed();

            int[] processIds;

            lock (_dataLock)
            {
                processIds =
                    _processInfos
                        .Select(x => x.Id)
                        .ToArray();
            }

            var result =
                new List<Process>();

            foreach (int processId in processIds)
            {
                try
                {
                    Process process =
                        Process.GetProcessById(processId);

                    if (!process.HasExited)
                    {
                        result.Add(process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch
                {
                }
            }

            return result;
        }

        public IEnumerable<ProcessInfo> GetProcessInfos()
        {
            ThrowIfDisposed();

            lock (_dataLock)
            {
                return _processInfos
                    .Select(CloneProcessInfo)
                    .ToList();
            }
        }

        public IEnumerable<ProcessInfo> FilterProcesses(
            Func<ProcessInfo, bool> predicate)
        {
            ThrowIfDisposed();

            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));

            return GetProcessInfos()
                .Where(predicate)
                .ToList();
        }

        public void RefreshProcesses()
        {
            ThrowIfDisposed();

            if (Interlocked.CompareExchange(
                    ref _refreshing,
                    1,
                    0) != 0)
            {
                return;
            }

            try
            {
                Process[] processes = null;

                try
                {
                    processes =
                        Process.GetProcesses();

                    DateTime now =
                        DateTime.UtcNow;

                    var newProcessInfos =
                        new List<ProcessInfo>();

                    var activeProcessIds =
                        new HashSet<int>();

                    foreach (Process process in processes)
                    {
                        try
                        {
                            ProcessInfo info =
                                CreateProcessInfo(
                                    process,
                                    now);

                            if (info == null)
                                continue;

                            newProcessInfos.Add(info);
                            activeProcessIds.Add(info.Id);
                        }
                        catch
                        {
                        }
                        finally
                        {
                            try
                            {
                                process.Dispose();
                            }
                            catch
                            {
                            }
                        }
                    }

                    lock (_dataLock)
                    {
                        _processInfos =
                            newProcessInfos
                                .OrderBy(x => x.ProcessName)
                                .ThenBy(x => x.Id)
                                .ToList();

                        RemoveDeadCpuSamples(
                            activeProcessIds);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "Proses listesi yenilenirken hata oluştu.",
                        ex);
                }
                finally
                {
                    if (processes != null)
                    {
                        foreach (Process process in processes)
                        {
                            try
                            {
                                process?.Dispose();
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(
                    ref _refreshing,
                    0);
            }
        }

        private ProcessInfo CreateProcessInfo(
            Process process,
            DateTime timestamp)
        {
            if (process == null)
                return null;

            int processId;
            string processName;
            DateTime startTime;
            string moduleFileName;
            long memoryUsage;
            TimeSpan processorTime;

            try
            {
                if (process.HasExited)
                    return null;

                processId =
                    process.Id;

                processName =
                    process.ProcessName;

                startTime =
                    process.StartTime;

                memoryUsage =
                    process.WorkingSet64;

                processorTime =
                    process.TotalProcessorTime;
            }
            catch
            {
                return null;
            }

            try
            {
                moduleFileName =
                    process.MainModule != null
                        ? process.MainModule.FileName
                        : null;
            }
            catch
            {
                return null;
            }

            float cpuUsage =
                CalculateCpuUsage(
                    processId,
                    processorTime,
                    timestamp);

            return new ProcessInfo
            {
                Id = processId,
                ProcessName = processName,
                StartTime = startTime,
                MainModuleFileName = moduleFileName,
                CpuUsage = cpuUsage,
                MemoryUsageBytes = memoryUsage
            };
        }

        private float CalculateCpuUsage(
            int processId,
            TimeSpan currentProcessorTime,
            DateTime currentTimestamp)
        {
            lock (_dataLock)
            {
                CpuSample previousSample;

                float cpuUsage = 0f;

                if (_cpuSamples.TryGetValue(
                    processId,
                    out previousSample))
                {
                    double elapsedMilliseconds =
                        (currentTimestamp -
                         previousSample.Timestamp)
                        .TotalMilliseconds;

                    double processorMilliseconds =
                        (currentProcessorTime -
                         previousSample.ProcessorTime)
                        .TotalMilliseconds;

                    if (elapsedMilliseconds > 0 &&
                        processorMilliseconds >= 0)
                    {
                        double usage =
                            processorMilliseconds /
                            (elapsedMilliseconds *
                             Environment.ProcessorCount) *
                            100.0;

                        if (usage < 0)
                            usage = 0;

                        if (usage > 100)
                            usage = 100;

                        cpuUsage =
                            (float)usage;
                    }
                }

                _cpuSamples[processId] =
                    new CpuSample
                    {
                        ProcessorTime =
                            currentProcessorTime,

                        Timestamp =
                            currentTimestamp
                    };

                return cpuUsage;
            }
        }

        private void RemoveDeadCpuSamples(
            HashSet<int> activeProcessIds)
        {
            int[] deadProcessIds =
                _cpuSamples.Keys
                    .Where(id =>
                        !activeProcessIds.Contains(id))
                    .ToArray();

            foreach (int processId in deadProcessIds)
            {
                _cpuSamples.Remove(processId);
            }
        }

        private static ProcessInfo CloneProcessInfo(
            ProcessInfo source)
        {
            return new ProcessInfo
            {
                Id = source.Id,
                ProcessName = source.ProcessName,
                StartTime = source.StartTime,
                MainModuleFileName = source.MainModuleFileName,
                CpuUsage = source.CpuUsage,
                MemoryUsageBytes = source.MemoryUsageBytes
            };
        }

        private void StartAutoRefresh()
        {
            _refreshTimer =
                new Timer(
                    AutoRefreshCallback,
                    null,
                    RefreshIntervalMilliseconds,
                    RefreshIntervalMilliseconds);
        }

        private void AutoRefreshCallback(object state)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            try
            {
                RefreshProcesses();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Otomatik proses yenileme sırasında hata oluştu.",
                    ex);
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(ProcessService));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }

            Timer timer =
                Interlocked.Exchange(
                    ref _refreshTimer,
                    null);

            if (timer != null)
            {
                try
                {
                    timer.Change(
                        Timeout.Infinite,
                        Timeout.Infinite);
                }
                catch
                {
                }

                timer.Dispose();
            }

            lock (_dataLock)
            {
                _processInfos.Clear();
                _cpuSamples.Clear();
            }
        }
    }
}