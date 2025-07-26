using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace P5S_ceviri
{
    public class MemoryService : IMemoryService
    {

        #region P/Invoke
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
        #endregion

        private readonly ILogger _logger;
        private IntPtr _processHandle = IntPtr.Zero;

        public MemoryService(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool AttachToProcess(int processId)
        {
            Dispose();
            _processHandle = OpenProcess(0x10 | 0x0400, false, processId);
            if (_processHandle != IntPtr.Zero) return true;
            _logger.LogError($"Process'e bağlanılamadı (ID: {processId}). Hata Kodu: {Marshal.GetLastWin32Error()}");
            return false;
        }

        public byte[] ReadBytes(IntPtr address, int length)
        {
            if (_processHandle == IntPtr.Zero || address == IntPtr.Zero) return Array.Empty<byte>();
            var buffer = new byte[length];
            if (ReadProcessMemory(_processHandle, address, buffer, length, out _))
            {
                return buffer;
            }
            return Array.Empty<byte>();
        }

        public IntPtr ResolveAddressFromPath(Process process, PathInfo path)
        {
            if (path == null) return IntPtr.Zero;
            try
            {
                var mainModule = process.MainModule;
                IntPtr currentAddress = IntPtr.Add(mainModule.BaseAddress, (int)path.BaseAddressOffset);
                foreach (var offset in path.PointerOffsets)
                {
                    var pointerBytes = ReadBytes(currentAddress, IntPtr.Size);
                    if (pointerBytes.Length == 0) return IntPtr.Zero;
                    currentAddress = IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(pointerBytes, 0) : (IntPtr)BitConverter.ToInt32(pointerBytes, 0);
                    if (currentAddress == IntPtr.Zero) return IntPtr.Zero;
                    currentAddress = IntPtr.Add(currentAddress, offset);
                }
                _logger.LogInformation($"Pointer yolu çözüldü: 0x{currentAddress.ToInt64():X}");
                return currentAddress;
            }
            catch (Exception ex)
            {
                _logger.LogError("Adres yolu çözümlenirken hata oluştu.", ex);
                return IntPtr.Zero;
            }
        }

        // bellekte  belirli bir metni (Unicode) formatinda arar
        public List<IntPtr> FindStringAddresses(Process process, string searchText)
        {
            if (string.IsNullOrEmpty(searchText) || process == null) return new List<IntPtr>();

            var results = new List<IntPtr>();
            byte[] searchBytes = Encoding.Unicode.GetBytes(searchText);
            var mainModule = process.MainModule;
            byte[] memoryDump = new byte[mainModule.ModuleMemorySize];

            if (!ReadProcessMemory(process.Handle, mainModule.BaseAddress, memoryDump, memoryDump.Length, out _))
            {
                _logger.LogError("Metin arama için bellek okunamadı.");
                return results;
            }

            for (int i = 0; i <= memoryDump.Length - searchBytes.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < searchBytes.Length; j++)
                {
                    if (memoryDump[i + j] != searchBytes[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                {
                    results.Add(IntPtr.Add(mainModule.BaseAddress, i));
                }
            }
            _logger.LogInformation($"'{searchText}' metni için {results.Count} adet adres bulundu.");
            return results;
        }


        public string TryReadStringDeep(IntPtr address, int maxDepth = 4, int length = 256)
        {
            return ReadStringRecursive(address, maxDepth, length, 0, new HashSet<long>());
        }

        private string ReadStringRecursive(IntPtr address, int maxDepth, int length, int currentDepth, HashSet<long> visited)
        {
            if (currentDepth > maxDepth || address == IntPtr.Zero || !visited.Add(address.ToInt64())) return string.Empty;
            byte[] directBytes = ReadBytes(address, length);
            if (directBytes.Length == 0) return string.Empty;
            string[] encodings = { "UTF-8", "Unicode" };
            foreach (var encodingName in encodings)
            {
                try
                {
                    string potentialText = Encoding.GetEncoding(encodingName).GetString(directBytes).Split('\0')[0];
                    if (IsValidGameText(potentialText)) return potentialText;
                }
                catch { }
            }
            if (directBytes.Length >= IntPtr.Size)
            {
                long pointerValue = IntPtr.Size == 8 ? BitConverter.ToInt64(directBytes, 0) : BitConverter.ToInt32(directBytes, 0);
                if (pointerValue > 0x10000 && pointerValue < 0x7FFFFFFFFFFF)
                {
                    return ReadStringRecursive(new IntPtr(pointerValue), maxDepth, length, currentDepth + 1, visited);
                }
            }
            return string.Empty;
        }

        private bool IsValidGameText(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Length < 3 || s.Length > 1000) return false;
            if (s.Contains('\uFFFD')) return false;
            int nonPrintableCount = s.Count(c => char.IsControl(c) && !char.IsWhiteSpace(c));
            return (double)nonPrintableCount / s.Length <= 0.2 && s.Any(char.IsLetterOrDigit);
        }

        public void Dispose()
        {
            if (_processHandle != IntPtr.Zero)
            {
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }
    }
}