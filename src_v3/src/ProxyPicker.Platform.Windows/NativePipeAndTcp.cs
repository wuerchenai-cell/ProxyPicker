using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace ProxyPicker.Platform.Windows
{
    /// <summary>监听进程身份三档判定。</summary>
    public enum ListenerKind
    {
        Unknown = 0,
        Kernel = 1,
        Other = 2
    }

    /// <summary>
    /// 命名管道直连与 TCP 监听表进程身份核对（收口 kernel32 / iphlpapi / WMI 命令行实证）。
    /// </summary>
    public static class NativePipeAndTcp
    {
        const int AF_INET = 2;
        const int TCP_TABLE_OWNER_PID_LISTENER = 3;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle Pipe, out uint ServerProcessId);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

        static readonly HashSet<string> KernelExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mihomo.exe", "verge-mihomo.exe", "verge-mihomo-alpha.exe",
            "flclashcore.exe", "clash-meta.exe", "clash.meta.exe",
            "clash-win64.exe", "clash-win32.exe", "clash.exe",
            "nyanpasu-mihomo.exe", "sparkle-mihomo.exe", "mihomo-alpha.exe"
        };

        public static bool IsKernelExecutableName(string exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return false;
            string n = Path.GetFileName(exeName.Trim()).ToLowerInvariant();
            if (KernelExeNames.Contains(n)) return true;
            if (n.StartsWith("mihomo-", StringComparison.Ordinal) && n.EndsWith(".exe", StringComparison.Ordinal) &&
                n.IndexOf("party", StringComparison.Ordinal) < 0)
                return true;
            return false;
        }

        public static List<string> EnumerateControlPipes()
        {
            var result = new List<string>();
            try
            {
                foreach (var full in Directory.GetFiles(@"\\.\pipe\"))
                {
                    string name = full.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
                        ? full.Substring(@"\\.\pipe\".Length)
                        : full;
                    if (IsRestControlPipeName(name))
                        result.Add(name);
                }
            }
            catch { }
            return result;
        }

        public static bool IsRestControlPipeName(string pipeName)
        {
            if (string.IsNullOrWhiteSpace(pipeName)) return false;
            string p = pipeName.Trim().ToLowerInvariant();
            if (p.Contains("service") || p.Contains("helper")) return false;
            return p.StartsWith("verge-mihomo", StringComparison.Ordinal) ||
                   p.StartsWith("nyanpasu-mihomo", StringComparison.Ordinal) ||
                   p.StartsWith("sparkle-mihomo", StringComparison.Ordinal) ||
                   p.StartsWith("mihomo", StringComparison.Ordinal);
        }

        public static bool TrySendPipeHttp(string pipeName, byte[] requestBytes, int timeoutMs,
            out int serverPid, out byte[] responseBytes, out string error)
        {
            serverPid = 0;
            responseBytes = null;
            error = "";
            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(Math.Max(500, Math.Min(timeoutMs, 3000)));
                    uint pid;
                    if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid))
                        serverPid = (int)pid;

                    if (serverPid > 0)
                    {
                        string img;
                        if (NativeProcessProbe.TryQueryImagePathByPid(serverPid, out img) && !IsKernelExecutableName(img))
                        {
                            error = "命名管道服务端不是 mihomo 内核（" + Path.GetFileName(img) + "），已阻止发送控制密钥";
                            return false;
                        }
                    }

                    pipe.Write(requestBytes, 0, requestBytes.Length);
                    pipe.Flush();

                    var ms = new MemoryStream();
                    var buf = new byte[16 * 1024];
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < Math.Max(1500, timeoutMs))
                    {
                        int n = pipe.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        if (ms.Length + n > 16 * 1024 * 1024)
                        {
                            error = "管道响应超过 16 MiB 安全上限";
                            return false;
                        }
                        ms.Write(buf, 0, n);
                    }
                    responseBytes = ms.ToArray();
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool IsLoopbackPortListening(int port, out bool tableOk, out int ownerPid)
        {
            tableOk = false;
            ownerPid = 0;
            if (port <= 0 || port > 65535) return false;
            try
            {
                int size = 0;
                uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (rc != ERROR_INSUFFICIENT_BUFFER || size <= 4) return false;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    rc = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                    if (rc != 0) return false;
                    tableOk = true;
                    int count = Marshal.ReadInt32(buf);
                    int maxRows = (size - 4) / 24;
                    if (count > maxRows) count = maxRows;
                    IntPtr row = IntPtr.Add(buf, 4);
                    for (int i = 0; i < count; i++)
                    {
                        uint localAddr = unchecked((uint)Marshal.ReadInt32(row, 4));
                        int localPortRaw = Marshal.ReadInt32(row, 8);
                        int localPort = ((localPortRaw & 0xFF) << 8) | ((localPortRaw >> 8) & 0xFF);
                        int pid = Marshal.ReadInt32(row, 20);
                        bool isLoopOrAny = (localAddr & 0xFF) == 127 || localAddr == 0;
                        if (localPort == port && isLoopOrAny)
                        {
                            ownerPid = pid;
                            return true;
                        }
                        row = IntPtr.Add(row, 24);
                    }
                    return false;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return false; }
        }

        public static ListenerKind CheckListenerIdentity(int port, out string detail)
        {
            detail = "";
            bool tableOk;
            int pid;
            bool listening = IsLoopbackPortListening(port, out tableOk, out pid);
            if (!tableOk || !listening || pid <= 0)
            {
                detail = tableOk ? "端口无活跃监听" : "无法读取系统 TCP 监听表";
                return ListenerKind.Unknown;
            }
            string img;
            if (!NativeProcessProbe.TryQueryImagePathByPid(pid, out img))
            {
                detail = "PID " + pid + " 路径不可读";
                return ListenerKind.Unknown;
            }
            string fn = Path.GetFileName(img);
            detail = fn + " (PID " + pid + ")";
            return IsKernelExecutableName(fn) ? ListenerKind.Kernel : ListenerKind.Other;
        }

        public static string TryGetProcessCommandLine(int pid)
        {
            if (pid <= 0) return "";
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                {
                    foreach (ManagementBaseObject mo in q.Get())
                        return Convert.ToString(mo["CommandLine"]) ?? "";
                }
            }
            catch { }
            return "";
        }
    }
}
