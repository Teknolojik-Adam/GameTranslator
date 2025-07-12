using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace P5S_ceviri
{
    public class PointerPath
    {
        public string ModuleName { get; set; }
        public long BaseOffset { get; set; }
        public List<int> Offsets { get; set; } = new List<int>();

        public override string ToString()
        {
            return $"\"{ModuleName}\"+0x{BaseOffset:X} -> {string.Join(" -> ", Offsets.Select(o => "0x" + o.ToString("X")))}";
        }
    }

    public class PointerScanner
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        private readonly Process _gameProcess;
        private readonly ProcessModule _mainModule;

        public PointerScanner(Process process)
        {
            _gameProcess = process ?? throw new ArgumentNullException(nameof(process));
            _mainModule = process.MainModule;
        }

        public async Task<List<PointerPath>> FindPointers(IntPtr targetAddress, int maxDepth = 4)
        {
            byte[] memoryDump = new byte[_mainModule.ModuleMemorySize];
            if (!ReadProcessMemory(_gameProcess.Handle, _mainModule.BaseAddress, memoryDump, memoryDump.Length, out _))
            {
                throw new Exception("Oyun belleği okunamadı!");
            }

            var paths = await Task.Run(() => ScanLevel(memoryDump, new List<long> { targetAddress.ToInt64() }, maxDepth));
            return paths;
        }

        private List<PointerPath> ScanLevel(byte[] memoryDump, List<long> targets, int depth)
        {
            if (depth <= 0 || !targets.Any())
            {
                return new List<PointerPath>();
            }

            var foundPointers = new Dictionary<long, long>();
            for (int i = 0; i < memoryDump.Length - (IntPtr.Size - 1); i += 4)
            {
                long potentialPointer = BitConverter.ToInt64(memoryDump, i);
                if (targets.Contains(potentialPointer))
                {
                    foundPointers[i] = potentialPointer;
                }
            }

            var newPaths = foundPointers.Keys.Select(offset => new PointerPath
            {
                ModuleName = _mainModule.ModuleName,
                BaseOffset = offset,
                Offsets = new List<int> { (int)(foundPointers[offset] - (offset + _mainModule.BaseAddress.ToInt64())) }
            }).ToList();

            var deeperPaths = ScanLevel(memoryDump, foundPointers.Keys.Select(k => k + _mainModule.BaseAddress.ToInt64()).ToList(), depth - 1);

            // her seviyeden bulunan yolları birleştiriyoruz.
            return newPaths.Concat(deeperPaths).ToList();
        }
    }
}