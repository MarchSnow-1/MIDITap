// MidiTheoryAttribute.cs — 需要 MIDI 端口的 [Theory]，端口缺失时跳过
//
// A [Theory] that needs the MIDI port; skipped when it is absent

using Xunit;

namespace MIDITap.Integration.Tests;

public sealed class MidiTheoryAttribute : TheoryAttribute
{
    public MidiTheoryAttribute()
    {
        if (!MidiTestPort.IsAvailable)
        {
            Skip = MidiTestPort.SkipReason;
        }
    }
}
