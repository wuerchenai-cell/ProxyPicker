using System;
using System.Diagnostics;
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
        const int MaxResponseBytes = 16 * 1024 * 1024;
        static readonly IntPtr InvalidHandle = new IntPtr(-1);

        // 整个「连接 -> 写 -> 读」共用一个总时限，避免服务端不读大配置时长期卡住（评审 R6-6）。
        readonly Stopwatch _clock = Stopwatch.StartNew();
        int _deadlineMs = 15000;
        public void SetDeadline(int ms) { _deadlineMs = Math.Max(1000, ms); }
        long RemainingMs { get { long left = _deadlineMs - _clock.ElapsedMilliseconds; return left > 0 ? left : 0; } }

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

        // Windows Vista 起可用：从已连接的客户端句柄反查「管道服务端」的 PID。
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(IntPtr hNamedPipe, out uint serverProcessId);

        IntPtr _h = InvalidHandle;

        /// <summary>管道另一端的进程 ID；拿不到就抛错，调用方必须停止发送 Secret。</summary>
        public int ServerProcessId
        {
            get
            {
                uint pid;
                if (!GetNamedPipeServerProcessId(_h, out pid))
                    throw new IOException("读取管道服务端 PID 失败（Win32 错误 " +
                        Marshal.GetLastWin32Error() + "）");
                return (int)pid;
            }
        }

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
                if (RemainingMs <= 0)
                    throw new TimeoutException("控制管道写入超过总时限（" + _deadlineMs + " ms）");
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

        /// <summary>读到对方关闭为止；总时限与响应大小都有上限，超时关句柄打断阻塞的 ReadFile。</summary>
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
                    if (ms.Length > MaxResponseBytes)
                        throw new IOException("控制管道响应超过 16 MiB，已中断");
                }
            });
            int wait = (int)Math.Min(Math.Max(1, timeoutMs), Math.Max(1, RemainingMs));
            if (!task.Wait(wait))
            {
                Dispose();
                throw new TimeoutException("控制管道响应超时（" + wait + " ms）");
            }
            task.Wait();   // 把读线程里的大小上限异常抛回调用方
            if (RemainingMs <= 0)
                throw new TimeoutException("控制管道请求超过总时限（" + _deadlineMs + " ms）");
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
