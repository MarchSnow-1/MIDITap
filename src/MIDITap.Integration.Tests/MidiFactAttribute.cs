// MidiFactAttribute.cs — 需要真实 MIDI 端口的用例；端口缺失时**跳过**而不是失败
//
// xUnit 的 Skip 可在特性构造函数里赋值
// 因此发现阶段就能判定，无需额外的 Skippable 包依赖
// 这样同一个测试项目既能本地跑真机验证、又能在没有 MIDI 设备的 CI 上通过
//
// A [Fact] that requires a real MIDI port: skipped (not failed) when the port is absent
//
// xUnit allows assigning Skip from the attribute constructor
// So the decision happens at discovery time, and no extra Skippable package is needed
// The same project therefore runs against real hardware locally
// It still passes on CI runners that have no MIDI devices

using Xunit;

namespace MIDITap.Integration.Tests;

/// <summary>
/// 需要 MIDI 回环端口的测试；端口不存在时自动跳过
///
/// Test that requires a MIDI loopback port; auto-skipped when the port is absent
/// </summary>
public sealed class MidiFactAttribute : FactAttribute
{
    public MidiFactAttribute()
    {
        if (!MidiTestPort.IsAvailable)
        {
            Skip = MidiTestPort.SkipReason;
        }
    }
}
