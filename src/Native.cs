using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CoreLasso
{
    /// <summary>
    /// Thin P/Invoke layer. Everything here is read-only or process-scoped:
    /// no registry writes, no drivers, no services, nothing that survives a reboot.
    /// </summary>
    internal static class Native
    {
        // ---- process access rights (least privilege we can get away with) ----
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint PROCESS_SET_INFORMATION           = 0x0200;
        public const uint PROCESS_SET_LIMITED_INFORMATION   = 0x2000;
        public const uint PROCESS_SUSPEND_RESUME            = 0x0800;

        public const uint CREATE_SUSPENDED = 0x00000004;

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_CPU_SET_INFORMATION
        {
            public uint   Size;
            public uint   Type;                   // CpuSetInformation == 0
            public uint   Id;                     // opaque CPU-set id (NOT the cpu index)
            public ushort Group;
            public byte   LogicalProcessorIndex;
            public byte   CoreIndex;
            public byte   LastLevelCacheIndex;
            public byte   NumaNodeIndex;
            public byte   EfficiencyClass;        // higher == more performant (P-core)
            public byte   AllFlags;               // bit0 Parked, bit1 Allocated, bit2 AllocatedToTarget, bit3 RealTime
            public uint   SchedulingClassReserved;
            public ulong  AllocationTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemCpuSetInformation(
            IntPtr Information, uint BufferLength, out uint ReturnedLength, IntPtr Process, uint Flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessDefaultCpuSets(IntPtr Process, uint[] CpuSetIds, uint CpuSetIdCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessDefaultCpuSets(
            IntPtr Process, uint[] CpuSetIds, uint CpuSetIdCount, out uint RequiredIdCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessAffinityMask(
            IntPtr hProcess, out UIntPtr lpProcessAffinityMask, out UIntPtr lpSystemAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessAffinityMask(IntPtr hProcess, UIntPtr dwProcessAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetPriorityClass(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool QueryFullProcessImageName(
            IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcess(
            string lpApplicationName, string lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
            string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool IsWow64Process(IntPtr hProcess, out bool Wow64Process);
    }
}
