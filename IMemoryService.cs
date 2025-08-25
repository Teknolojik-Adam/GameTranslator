using System;
using System.Diagnostics;

namespace P5S_ceviri
{
    public interface IMemoryService : IDisposable
    {
        bool AttachToProcess(int processId);
        byte[] ReadBytes(IntPtr address, int length);
        string TryReadStringDeep(IntPtr address);
        IntPtr ResolveAddressFromPath(Process process, PathInfo path);
    }
}