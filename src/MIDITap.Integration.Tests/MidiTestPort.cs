// MidiTestPort.cs — 发现用于集成测试的虚拟 MIDI 端口对，并在缺失时让用例自动跳过
//
// 为什么用虚拟端口而不是真机：测试必须能重复、可控、且不依赖实验室里那台琴
// loopMIDI 会为一个名字同时创建输入与输出端口
// 把消息写到输出端、从同名输入端读回，就能走完整条真实的 WinMM/NAudio 路径
//
// 端口名可通过环境变量覆盖，便于在不同机器/CI 上换用别的虚拟端口
//
// Discovers the virtual MIDI port pair used by the integration tests
// Skips rather than fails when it is absent
//
// A virtual loopback port rather than real hardware
// Tests must be repeatable, controllable, and not depend on whatever piano happens to be attached
// loopMIDI creates an input and an output under one name
// Writing to the output and reading back from the input of the same name exercises the real WinMM/NAudio path
//
// The port name is overridable so another virtual port can be used elsewhere

using System.Runtime.InteropServices;
using NAudio.Midi;

namespace MIDITap.Integration.Tests;

public static class MidiTestPort
{
    /// <summary>
    /// 默认端口名（loopMIDI 中为 MIDITap 建立的测试端口）
    ///
    /// Default port name (the test port created for MIDITap in loopMIDI)
    /// </summary>
    public const string DefaultName = "MIDITap-TestConfig";

    /// <summary>环境变量：覆盖测试端口名 / Environment variable that overrides the test port name</summary>
    public const string NameEnvironmentVariable = "MIDITAP_TEST_MIDI_PORT";

    public static string Name { get; } =
        Environment.GetEnvironmentVariable(NameEnvironmentVariable) is { Length: > 0 } custom
            ? custom
            : DefaultName;

    /// <summary>输入端口索引；-1 表示没有 / Input port index, or -1</summary>
    public static int InputIndex { get; } = FindInput();

    /// <summary>输出端口索引；-1 表示没有 / Output port index, or -1</summary>
    public static int OutputIndex { get; } = FindOutput();

    /// <summary>
    /// 端口对是否齐备（输入与输出同名端口都存在）
    ///
    /// Whether the port pair is complete (both the input and the output port with that name exist)
    /// </summary>
    public static bool IsAvailable => InputIndex >= 0 && OutputIndex >= 0;

    /// <summary>
    /// 跳过原因（端口不齐时给用户看的信息）
    ///
    /// Skip reason (the message shown to the user when the port pair is incomplete)
    /// </summary>
    public static string SkipReason { get; } = BuildSkipReason();

    private static int FindInput()
    {
        var count = MidiIn.NumberOfDevices;
        for (var i = 0; i < count; i++)
        {
            if (Matches(MidiIn.DeviceInfo(i).ProductName))
            {
                return i;
            }
        }
        return -1;
    }

    // NAudio 只封装了 MIDI **输入**的设备枚举（MidiIn.NumberOfDevices/DeviceInfo）
    // 输出侧必须自己调 WinMM
    // 注意 MIDIOUTCAPS 比 MIDIINCAPS 多四个字段
    // 用错结构体会让设备名读成空字符串
    //
    // NAudio only wraps INPUT enumeration
    // The output side needs WinMM directly
    // MIDIOUTCAPS has four more fields than MIDIINCAPS
    // Using the wrong struct makes the device name read back empty
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiOutCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }

    [DllImport("winmm.dll", EntryPoint = "midiOutGetNumDevs")]
    private static extern int MidiOutGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "midiOutGetDevCaps", CharSet = CharSet.Unicode)]
    private static extern int MidiOutGetDevCaps(UIntPtr deviceId, ref MidiOutCaps caps, int size);

    private static int FindOutput()
    {
        var count = MidiOutGetNumDevs();
        for (var i = 0; i < count; i++)
        {
            var caps = default(MidiOutCaps);
            var rc = MidiOutGetDevCaps((UIntPtr)i, ref caps, Marshal.SizeOf<MidiOutCaps>());
            if (rc == 0 && Matches(caps.szPname))
            {
                return i;
            }
        }
        return -1;
    }

    private static bool Matches(string? productName)
        => !string.IsNullOrEmpty(productName)
           && string.Equals(productName.Trim(), Name, StringComparison.OrdinalIgnoreCase);

    private static string BuildSkipReason()
        => IsAvailable
            ? string.Empty
            : $"MIDI loopback port '{Name}' not found (input={InputIndex}, output={OutputIndex}). " +
              "Install loopMIDI and create a port with that name, or set " +
              $"{NameEnvironmentVariable} to an existing virtual port. " +
              "在 CI 上这是预期行为：windows-latest 没有 MIDI 设备。";
}
