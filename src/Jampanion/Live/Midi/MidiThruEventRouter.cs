using Jampanion.Core.Music;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace Jampanion.Live.Midi;

internal static class MidiThruEventRouter
{
    public static MidiEvent? RouteToVibraphone(MidiEvent sourceEvent)
    {
        if (sourceEvent is not ChannelEvent || sourceEvent is ProgramChangeEvent)
        {
            return null;
        }

        var routedEvent = sourceEvent.Clone();
        ((ChannelEvent)routedEvent).Channel = (FourBitNumber)SessionConstants.VibraphoneChannel;
        return routedEvent;
    }
}
