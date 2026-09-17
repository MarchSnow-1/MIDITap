// NativeMemory.cs — 读取系统可用物理内存
//
// 为什么不用 GC.GetGCMemoryInfo().TotalAvailableMemoryBytes：
// 它给出的是进程**可用**的地址空间上限（受作业对象与容器配额影响），不是系统还剩多少物理内存
// 排查"内存不足导致按键偶发丢失"要看的是后者，因此直接问 GlobalMemoryStatusEx
//
// 为什么不放进 Core：它经由 P/Invoke 读 Windows 计数器
// Core 不得引用 WindowsAppSDK 与 WinUI，且要求能在没有桌面的环境下测试
// 这类平台调用放在 App 层，Core 保持可测的纯逻辑
//
// NativeMemory.cs — reads the system's available physical memory
//
// Why not GC.GetGCMemoryInfo().TotalAvailableMemoryBytes:
// that reports the address-space limit the process may use (affected by job objects and container quotas), not how much physical memory the system has left
// Triaging "keys occasionally lost because memory ran out" needs the latter, so GlobalMemoryStatusEx is asked directly
//
// Why not in Core: it reads a Windows counter through P/Invoke
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
