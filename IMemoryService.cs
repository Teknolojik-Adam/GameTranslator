using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GameTranslatorUltimate
{
    public interface IMemoryService : IDisposable
    {
        bool AttachToProcess(
            int processId);

        byte[] ReadBytes(
            IntPtr address,
            int length);

        string TryReadStringDeep(
            IntPtr address);

        IntPtr ResolveAddressFromPath(
            Process process,
            PathInfo path);

        IntPtr ResolveAddressFromPathCached(
            Process process,
            PathInfo path);

        byte[] ReadBytesCached(
            IntPtr address,
            int length);

        List<KeyValuePair<PathInfo, IntPtr>> GetMostFrequentAddresses(
            int topN = 10);

        void ClearAddressCache();

        void ClearMemoryCache();

        void ClearAllCaches();

        (
            int AddressCacheCount,
            int MemoryCacheCount
        ) GetCacheStatistics();
    }
}