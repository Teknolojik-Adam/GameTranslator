using P5S_ceviri;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;

namespace P5S_ceviri
{
    public class MemoryService : IMemoryService
    {
        #region Windows API Imports (P/Invoke)
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer,
            int dwSize,
            out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint dwDesiredAccess,
            bool bInheritHandle,
            int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
        #endregion

        protected readonly ILogger _logger;
        protected readonly AppSettings _appSettings;
        private IntPtr _processHandle = IntPtr.Zero;
        private ILogger logger;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        public MemoryService(ILogger logger, AppSettings appSettings)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        }

        public MemoryService(ILogger logger)
        {
            this.logger = logger;
        }

        public string TryReadStringDeep(IntPtr address)
        {
            var queue = new Queue<(IntPtr address, int depth)>();
            queue.Enqueue((address, 0));
            var visited = new HashSet<long>();

            while (queue.Any())
            {
                var (currentAddress, currentDepth) = queue.Dequeue();

                if (currentAddress == IntPtr.Zero || currentDepth > _appSettings.PointerSearchMaxDepth || !visited.Add(currentAddress.ToInt64()))
                {
                    continue;
                }

                var buffer = ReadBytes(currentAddress, _appSettings.StringReadLength);
                if (buffer.Length == 0)
                {
                    continue;
                }

                var (found, text) = ParseBufferAsString(buffer);
                if (found)
                {
                    return text;
                }

                if (buffer.Length >= IntPtr.Size)
                {
                    try
                    {
                        long pointerValue = IntPtr.Size == 8 ? BitConverter.ToInt64(buffer, 0) : BitConverter.ToInt32(buffer, 0);
                        if (pointerValue > 0x10000 && pointerValue < 0x7FFFFFFFFFFF)
                        {
                            queue.Enqueue((new IntPtr(pointerValue), currentDepth + 1));
                        }
                    }
                    catch
                    {
                        // This is intentional. If the buffer is not a valid pointer,
                        // BitConverter will throw an exception. We catch it and
                        // simply continue the loop to the next item in the queue.
                    }
                }
            }
            return null;
        }

        private (bool, string) ParseBufferAsString(byte[] buffer)
        {
            int nullIndex = Array.IndexOf(buffer, (byte)0);
            if (nullIndex >= 0)
            {
                var segment = new ArraySegment<byte>(buffer, 0, nullIndex);
                buffer = segment.ToArray();
            }

            foreach (var encodingName in new[] { "Unicode", "UTF-8", "ASCII" })
            {
                try
                {
                    Encoding encoding = Encoding.GetEncoding(encodingName);
                    string result = encoding.GetString(buffer).Trim('\0');
                    if (IsValidGameText(result))
                    {
                        return (true, result);
                    }
                }
                catch { continue; }
            }
            return (false, null);
        }

        public bool AttachToProcess(int processId)
        {
            Dispose();
            _processHandle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, processId);
            if (_processHandle != IntPtr.Zero)
            {
                _logger?.LogInformation($"Process'e başarıyla bağlanıldı (ID: {processId}).");
                return true;
            }
            else
            {
                int errorCode = Marshal.GetLastWin32Error();
                _logger?.LogError($"Process'e bağlanılamadı (ID: {processId}). Hata Kodu: {errorCode}");
                return false;
            }
        }

        public byte[] ReadBytes(IntPtr address, int length)
        {
            if (_processHandle == IntPtr.Zero || address == IntPtr.Zero || length <= 0)
            {
                return new byte[0];
            }
            byte[] buffer = new byte[length];
            if (ReadProcessMemory(_processHandle, address, buffer, length, out int bytesRead) && bytesRead == length)
            {
                return buffer;
            }
            else
            {
                int errorCode = Marshal.GetLastWin32Error();
                _logger?.LogWarning($"Bellek okuma başarısız: Adres 0x{address.ToInt64():X}, Uzunluk {length}, Okunan Byte {bytesRead}, Hata Kodu: {errorCode}");
                return new byte[0];
            }
        }

        private bool IsValidGameText(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Length < 2 || s.Length > 1000)
                return false;
            if (s.Contains('\uFFFD'))
                return false;
            int printableOrWhitespaceCount = s.Count(c => !char.IsControl(c) || char.IsWhiteSpace(c));
            double printableRatio = (double)printableOrWhitespaceCount / s.Length;
            return printableRatio >= 0.8 && s.Any(char.IsLetterOrDigit);
        }

        public IntPtr ResolveAddressFromPath(Process process, PathInfo path)
        {
            if (path == null || process == null) return IntPtr.Zero;
            try
            {
                ProcessModule mainModule = process.MainModule;
                if (mainModule == null || !mainModule.ModuleName.Equals(path.BaseAddressModule, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning($"Modül eşleşmedi: Beklenen '{path.BaseAddressModule}', Bulunan '{mainModule?.ModuleName}'");
                    return IntPtr.Zero;
                }
                IntPtr currentAddress = IntPtr.Add(mainModule.BaseAddress, (int)path.BaseAddressOffset);
                _logger?.LogInformation($"Başlangıç adresi: 0x{currentAddress.ToInt64():X} ({mainModule.ModuleName} + 0x{path.BaseAddressOffset:X})");
                foreach (var offset in path.PointerOffsets)
                {
                    var pointerBytes = ReadBytes(currentAddress, IntPtr.Size);
                    if (pointerBytes.Length == 0)
                    {
                        _logger?.LogWarning($"Pointer okuma başarısız: 0x{currentAddress.ToInt64():X}");
                        return IntPtr.Zero;
                    }
                    long pointerValue = IntPtr.Size == 8 ? BitConverter.ToInt64(pointerBytes, 0) : BitConverter.ToInt32(pointerBytes, 0);
                    currentAddress = new IntPtr(pointerValue);
                    if (currentAddress == IntPtr.Zero)
                    {
                        _logger?.LogWarning("Pointer değeri sıfır.");
                        return IntPtr.Zero;
                    }
                    currentAddress = IntPtr.Add(currentAddress, offset);
                    _logger?.LogInformation($" -> Offset 0x{offset:X} uygulandı. Yeni adres: 0x{currentAddress.ToInt64():X}");
                }
                _logger?.LogInformation($"Pointer yolu başarıyla çözümlendi: 0x{currentAddress.ToInt64():X}");
                return currentAddress;
            }
            catch (Exception ex)
            {
                _logger?.LogError("Adres yolu çözümlenirken hata oluştu.", ex);
                return IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (_processHandle != IntPtr.Zero)
            {
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
                _logger?.LogInformation("Process bağlantısı kapatıldı.");
            }
        }
    }
}