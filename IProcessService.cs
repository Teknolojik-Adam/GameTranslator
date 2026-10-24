using System.Collections.Generic;
using System.Diagnostics;

namespace GameTranslatorUltimate
{
    public interface IProcessService
    {
        IEnumerable<Process> GetProcesses();

        void RefreshProcesses();
    }
}