using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class ScheduledNoteOverlapGuard
{
    // A retriggered MIDI note must receive Note Off before the next Note On on
    // the same channel and pitch. This is especially important for external GM
    // devices, which otherwise sustain or steal the wrong voice.
    public static List<ScheduledNote> TrimSamePitchOverlaps(IEnumerable<ScheduledNote> source)
    {
        var result = source.ToList();
        foreach (var group in result
                     .Select((note, index) => (note, index))
                     .GroupBy(item => (item.note.Channel, item.note.NoteNumber)))
        {
            var indices = group.OrderBy(item => item.note.StartTick).Select(item => item.index).ToArray();
            for (var position = 1; position < indices.Length; position++)
            {
                var previousIndex = indices[position - 1];
                var currentIndex = indices[position];
                var previous = result[previousIndex];
                var current = result[currentIndex];
                if (current.StartTick <= previous.StartTick)
                {
                    result[currentIndex] = current with { DurationTicks = 0 };
                    continue;
                }

                if (current.StartTick < previous.EndTick)
                {
                    result[previousIndex] = previous with
                    {
                        DurationTicks = Math.Max(1, current.StartTick - previous.StartTick - 1)
                    };
                }
            }
        }

        return result.Where(note => note.DurationTicks > 0).ToList();
    }
}
