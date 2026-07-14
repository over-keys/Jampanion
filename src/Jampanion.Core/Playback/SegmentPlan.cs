using Jampanion.Core.Music;

namespace Jampanion.Core.Playback;

public sealed class SegmentPlan
{
    public SegmentPlan(
        int segmentIndex,
        RhythmFeel feel,
        IReadOnlyList<ScheduledNote> notes,
        long? lengthTicks = null)
    {
        if (segmentIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        }

        ArgumentNullException.ThrowIfNull(notes);

        var resolvedLengthTicks = lengthTicks ?? (long)SessionConstants.BarsPerSegment * SessionConstants.BarTicks;
        if (resolvedLengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks));
        }

        if (notes.Any(note => note.StartTick < 0 || note.EndTick > resolvedLengthTicks))
        {
            throw new ArgumentException("Segment notes must remain inside the declared segment length.", nameof(notes));
        }

        SegmentIndex = segmentIndex;
        Feel = feel;
        Notes = notes;
        LengthTicks = resolvedLengthTicks;
    }

    public int SegmentIndex { get; }
    public int StartBarIndex => SegmentIndex * SessionConstants.BarsPerSegment;
    public RhythmFeel Feel { get; }
    public IReadOnlyList<ScheduledNote> Notes { get; }
    public long LengthTicks { get; }
}
