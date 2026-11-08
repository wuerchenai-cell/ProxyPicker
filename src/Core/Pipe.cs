using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 命名管道连接（Win32 CreateFile + ReadFile + WriteFile）。
    ///
    /// 为什么不直接用 FileStream：.NET Framework 的 FileStream 只接受「文件」类设备，
    /// 打开 \\.\pipe\xxx 会抛 NotSupportedException；net8 上虽然能开，但为了两个框架行为一致、
    /// 且能精确控制超时，这里统一走 Win32 API。
    /// </summary>
    internal sealed class PipeConnection : IDisposable
    {
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint OPEN_EXISTING = 3;
        static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        IntPtr _h = InvalidHandle;

        public static PipeConnection Open(string pipeFullName)
        {
            IntPtr h = CreateFileW(pipeFullName, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero,
                OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == InvalidHandle)
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException("打开管道失败（Win32 错误 " + err + "）：" + pipeFullName);
            }
            return new PipeConnection { _h = h };
        }

        public void WriteAll(byte[] data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int remain = data.Length - offset;
                byte[] chunk;
                if (offset == 0)
                {
                    chunk = data;
                }
                else
                {
                    chunk = new byte[remain];
                    Buffer.BlockCopy(data, offset, chunk, 0, remain);
                }
                uint written;
                if (!WriteFile(_h, chunk, (uint)remain, out written, IntPtr.Zero))
                    throw new IOException("写入管道失败（Win32 错误 " + Marshal.GetLastWin32Error() + "）");
                if (written == 0) throw new IOException("管道写入 0 字节");
                offset += (int)written;
            }
        }

        /// <summary>读到对方关闭为止；超时会关句柄让阻塞的 ReadFile 立即返回。</summary>
        public byte[] ReadToEnd(int timeoutMs)
        {
            var ms = new MemoryStream();
            IntPtr h = _h;
            var task = Task.Run(() =>
            {
                byte[] buf = new byte[65536];
                while (true)
                {
                    uint n;
                    bool ok = ReadFile(h, buf, (uint)buf.Length, out n, IntPtr.Zero);
                    if (!ok || n == 0) break;
                    ms.Write(buf, 0, (int)n);
                }
            });
            if (!task.Wait(timeoutMs))
            {
                Dispose();
                throw new TimeoutException("控制管道响应超时（" + timeoutMs + " ms）");
            }
            return ms.ToArray();
        }

        public void Dispose()
        {
            if (_h != InvalidHandle)
            {
                CloseHandle(_h);
                _h = InvalidHandle;
            }
        }
    }
}
