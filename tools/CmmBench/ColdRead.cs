using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CmmBench
{
    // Reads a file with the OS file cache BYPASSED, so the bytes come off the
    // physical device every time — the true "cold load from disk" speed, immune
    // to the RAM cache that makes File.ReadAllBytes report 500–1200 MB/s for
    // tracks the game has already played.
    //
    // FILE_FLAG_NO_BUFFERING has two hard requirements that trip up the naive
    // version: the transfer COUNT must be a multiple of the volume sector size,
    // and the destination BUFFER must be page-aligned. A plain `new byte[]` is
    // NOT guaranteed aligned (ReadFile then fails with ERROR_INVALID_PARAMETER
    // and most files silently drop out of the sample), so we read into a
    // VirtualAlloc'd, page-aligned native buffer and copy each block out.
    internal static class ColdRead
    {
        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_READ = 0x1;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
        const uint MEM_COMMIT_RESERVE = 0x3000;
        const uint MEM_RELEASE = 0x8000;
        const uint PAGE_READWRITE = 0x04;
        const int BLOCK = 1 << 20;   // 1 MiB — a multiple of any real sector size

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
            IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, uint toRead, out uint read, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);

        [DllImport("kernel32.dll")]
        static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);

        // Reads the whole file cache-bypassed. Returns the bytes (exact file length)
        // and the wall-clock read time in ms. `bytes` is null if the read failed.
        public static (byte[] bytes, double ms) Read(string path)
        {
            long len = new FileInfo(path).Length;
            var buf = VirtualAlloc(IntPtr.Zero, (UIntPtr)BLOCK, MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (buf == IntPtr.Zero) throw new IOException("VirtualAlloc failed");
            SafeFileHandle h = null;
            try
            {
                h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING,
                                FILE_FLAG_NO_BUFFERING | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
                if (h.IsInvalid) throw new IOException("open failed (err " + Marshal.GetLastWin32Error() + ")");

                var outBytes = new byte[len];
                long total = 0;
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    if (!ReadFile(h, buf, (uint)BLOCK, out uint got, IntPtr.Zero))
                        throw new IOException("ReadFile failed (err " + Marshal.GetLastWin32Error() + ")");
                    if (got == 0) break;
                    int copy = (int)Math.Min(got, len - total);
                    if (copy > 0) Marshal.Copy(buf, outBytes, (int)total, copy);
                    total += got;
                    if (total >= len) break;
                }
                sw.Stop();
                return (outBytes, sw.Elapsed.TotalMilliseconds);
            }
            finally
            {
                h?.Dispose();
                VirtualFree(buf, UIntPtr.Zero, MEM_RELEASE);
            }
        }
    }
}
