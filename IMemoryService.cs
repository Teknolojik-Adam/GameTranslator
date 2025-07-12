using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace P5S_ceviri
{
    public interface IMemoryService : IDisposable
    {
        bool AttachToProcess(int processId);
        byte[] ReadBytes(IntPtr address, int length);
        string TryReadStringDeep(IntPtr address, int maxDepth = 4, int length = 256);
        IntPtr ResolveAddressFromPath(Process process, PathInfo path);
        List<IntPtr> FindStringAddresses(Process process, string searchText);
    }
}