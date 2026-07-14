using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

public static class HeadOutResumeTransition
{
    public static IReadOnlyList<ScheduledNote> Apply(
        IReadOnlyList<ScheduledNote> notes,
        long resumeTick,
        long barTicks)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (resumeTick < 0) throw new ArgumentOutOfRangeException(nameof(resumeTick));
        if (barTicks <= 0) throw new ArgumentOutOfRangeException(nameof(barTicks));

        var transitionEnd = resumeTick + barTicks;
        return notes.Select(note =>
        {
            if (note.StartTick < resumeTick || note.StartTick >= transitionEnd)
            {
                return note;
            }

            var progress = (note.StartTick - resumeTick) / (double)barTicks;
            var maximumReduction = note.Channel switch
            {
                SessionConstants.PianoChannel => 5,
                SessionConstants.DrumsChannel => 4,
                SessionConstants.BassChannel => 2,
                _ => 0
            };
            var reduction = (int)Math.Round(maximumReduction * (1.0 - progress));
            return note with { Velocity = (byte)Math.Max(1, note.Velocity - reduction) };
        }).ToArray();
    }
}
