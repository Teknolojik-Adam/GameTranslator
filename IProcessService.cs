using System.Collections.Generic;
using System.Diagnostics;

namespace P5S_ceviri
{
    public interface IProcessService
    {
        IEnumerable<Process> GetProcesses();
        void RefreshProcesses();
    }
}