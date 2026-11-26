using System;
using System.Runtime.InteropServices;

namespace ProxyPicker.Platform.Windows
{
    /// <summary>Win32 Job Object（KILL_ON_JOB_CLOSE）封装：确保主进程意外崩溃时自动终止旁路探测子内核。</summary>
    public sealed class KillOnCloseJob : IDisposable
    {
        const int JobObjectExtendedLimitInformation = 9;
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        IntPtr _handle;

        public bool IsValid => _handle != IntPtr.Zero;

        public KillOnCloseJob()
        {
            IntPtr h = CreateJobObjectW(IntPtr.Zero, null);
            if (h == IntPtr.Zero) return;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int sz = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(sz);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (SetInformationJobObject(h, JobObjectExtendedLimitInformation, ptr, (uint)sz))
                    _handle = h;
                else
                    CloseHandle(h);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        public bool Assign(IntPtr processHandle)
        {
            return _handle != IntPtr.Zero && processHandle != IntPtr.Zero && AssignProcessToJobObject(_handle, processHandle);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                try { CloseHandle(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
        }
    }
}
