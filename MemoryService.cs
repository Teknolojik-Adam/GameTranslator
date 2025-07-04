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
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private readonly ILogger _logger;
        private IntPtr _processHandle = IntPtr.Zero;

        public MemoryService(ILogger logger)
        {
            _logger = logger;
        }

        public bool AttachToProcess(int processId)
        {
            Dispose();
            _processHandle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, processId);
            if (_processHandle == IntPtr.Zero)
            {
                _logger.LogError($"Process'e bağlanılamadı (ID: {processId}). Hata Kodu: {Marshal.GetLastWin32Error()}");
                return false;
            }
            return true;
        }

        public byte[] ReadBytes(IntPtr address, int length)
        {
            var buffer = new byte[length];
            if (_processHandle == IntPtr.Zero || address == IntPtr.Zero) return Array.Empty<byte>();

            if (!ReadProcessMemory(_processHandle, address, buffer, length, out _))
            {
                return Array.Empty<byte>();
            }
            return buffer;
        }

        public IntPtr ResolveAddressFromPath(Process process, PathInfo path)
        {
            if (path == null) return IntPtr.Zero;

            try
            {
                var mainModule = process.Modules.Cast<ProcessModule>()
                    .FirstOrDefault(m => m.ModuleName.Equals(path.BaseAddressModule, StringComparison.OrdinalIgnoreCase));

                if (mainModule == null)
                {
                    _logger.LogError($"Ana modül '{path.BaseAddressModule}' bulunamadı.");
                    return IntPtr.Zero;
                }

                IntPtr currentAddress = IntPtr.Add(mainModule.BaseAddress, (int)path.BaseAddressOffset);
                _logger.LogInformation($"Başlangıç adresi ({path.BaseAddressModule} + 0x{path.BaseAddressOffset:X}): 0x{currentAddress.ToInt64():X}");

                foreach (var offset in path.PointerOffsets)
                {
                    var pointerBytes = ReadBytes(currentAddress, IntPtr.Size);
                    if (pointerBytes.Length == 0)
                    {
                        _logger.LogError($"Pointer zinciri okunurken hata: 0x{currentAddress.ToInt64():X} adresi okunamadı.");
                        return IntPtr.Zero;
                    }

                    currentAddress = (IntPtr.Size == 8)
                        ? (IntPtr)BitConverter.ToInt64(pointerBytes, 0)
                        : (IntPtr)BitConverter.ToInt32(pointerBytes, 0);

                    if (currentAddress == IntPtr.Zero)
                    {
                        _logger.LogError("Pointer zinciri kırıldı, null bir adrese ulaşıldı.");
                        return IntPtr.Zero;
                    }

                    _logger.LogInformation($"Pointer okundu: 0x{currentAddress.ToInt64():X}");
                    currentAddress = IntPtr.Add(currentAddress, offset);
                    _logger.LogInformation($"Ofset (0x{offset:X}) eklendi, yeni adres: 0x{currentAddress.ToInt64():X}");
                }

                _logger.LogInformation($"Pointer yolu başarıyla çözüldü. Son metin adresi: 0x{currentAddress.ToInt64():X}");
                return currentAddress;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Adres yolu çözümlenirken beklenmedik bir hata oluştu.", ex);
                return IntPtr.Zero;
            }
        }

        public string TryReadStringDeep(IntPtr address, int maxDepth = 4, int length = 256)
        {
            return ReadStringRecursive(address, maxDepth, length, 0, new HashSet<long>());
        }

        private string ReadStringRecursive(IntPtr address, int maxDepth, int length, int currentDepth, HashSet<long> visited)
        {
            if (currentDepth > maxDepth || address == IntPtr.Zero || !visited.Add(address.ToInt64()))
                return string.Empty;

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
                catch { /* Ignore */ }
            }


            if (directBytes.Length >= IntPtr.Size)
            {
                long pointerValue = (IntPtr.Size == 8) ? BitConverter.ToInt64(directBytes, 0) : BitConverter.ToInt32(directBytes, 0);
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
            if ((double)nonPrintableCount / s.Length > 0.2) return false;
            if (!s.Any(char.IsLetterOrDigit)) return false;
            return true;
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