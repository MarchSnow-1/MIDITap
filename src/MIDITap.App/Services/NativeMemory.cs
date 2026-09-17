// NativeMemory.cs — 读取内存数字：系统可用物理内存与进程可用上限
//
// 两个数字含义不同，因此各占一项
// 系统可用物理内存来自 GlobalMemoryStatusEx，回答"机器还剩多少"
// 进程可用上限来自 GC.GetGCMemoryInfo().TotalAvailableMemoryBytes，回答"这个进程最多能用多少"
// 后者在容器或作业对象下是配额而不是物理内存，因此不能拿它代替前者
// 排查"内存不足导致按键偶发丢失"要看系统那一项，而判断进程是否被配额卡住要看这一项
//
// 为什么不放进 Core：系统内存计数器经由 P/Invoke 读取
// Core 不得引用 WindowsAppSDK 与 WinUI，且要求能在没有桌面的环境下测试
// 这类平台调用放在 App 层，Core 保持可测的纯逻辑
//
// NativeMemory.cs — reads memory figures: the system's free physical memory and the process's usable limit
//
// The two mean different things, so each gets an entry of its own
// The system's free physical memory comes from GlobalMemoryStatusEx and answers "how much is left on the machine"
// The process's usable limit comes from GC.GetGCMemoryInfo().TotalAvailableMemoryBytes and answers "how much may this process use at most"
// The latter is a quota under a container or a job object rather than physical memory, so it cannot stand in for the former
// Triaging "keys occasionally lost because memory ran out" needs the system figure, while judging whether the process is capped by a quota needs this one
//
// Why not in Core: the system counter is read through P/Invoke
// Core must not reference WindowsAppSDK or WinUI, and it has to stay testable without a desktop
// Platform calls of this kind live in the App layer, leaving Core as pure logic

using System.Runtime.InteropServices;

namespace MIDITap.App.Services;

public static class NativeMemory
{
    /// <summary>
    /// 系统当前可用的物理内存字节数
    /// 取不到时返回 false，调用方据此写 unknown，而不是拿 0 冒充一个真实读数
    ///
    /// The number of bytes of physical memory currently available to the system
    /// Returns false when the reading is unavailable, so the caller records unknown rather than passing 0 off as a real figure
    /// </summary>
    public static bool TryAvailableBytes(out long bytes)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullAvailPhys > long.MaxValue)
        {
            bytes = 0;
            return false;
        }
        bytes = (long)status.ullAvailPhys;
        return true;
    }

    /// <summary>
    /// 本进程可用的内存上限字节数
    /// 取的是 GC 对"这个进程还能用多少"的判断：默认是物理内存，在容器或作业对象下则是配额
    /// 取不到时返回 false，调用方据此写 unknown
    ///
    /// The memory limit in bytes available to this process
    /// It is the GC's view of how much the process may still use: physical memory by default, a quota under a container or a job object
    /// Returns false when the reading is unavailable, so the caller records unknown
    /// </summary>
    public static bool TryProcessLimitBytes(out long bytes)
    {
        // 托管 API，不需要 P/Invoke；非正值不作为读数采用，见方法说明
        //
        // A managed API, so no P/Invoke is involved; a non-positive value is not taken as a reading, see the summary
        var limit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (limit <= 0)
        {
            bytes = 0;
            return false;
        }
        bytes = limit;
        return true;
    }

    // 固定布局与字段顺序由 Win32 定义，字段名保持原名以便与文档逐项对照
    //
    // The layout and field order are defined by Win32, and the names are kept as published so they can be matched against the documentation item by item
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
