using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

public sealed class EndingPlan
{
    public EndingPlan(
        ChordSpec chord,
        IReadOnlyList<ScheduledNote> notes,
        long lengthTicks = EndingPlanBuilder.LengthTicks)
    {
        ArgumentNullException.ThrowIfNull(chord);
        ArgumentNullException.ThrowIfNull(notes);
        if (lengthTicks <= 0) throw new ArgumentOutOfRangeException(nameof(lengthTicks));

        Chord = chord;
        Notes = notes;
        LengthTicks = lengthTicks;
    }

    public ChordSpec Chord { get; }
    public IReadOnlyList<ScheduledNote> Notes { get; }
    public long LengthTicks { get; }
}
