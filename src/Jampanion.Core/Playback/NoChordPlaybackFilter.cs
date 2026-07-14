using Jampanion.Core.Music;

namespace Jampanion.Core.Playback;

internal static class NoChordPlaybackFilter
{
    public static IReadOnlyList<ScheduledNote> SuppressBassAndPiano(
        IReadOnlyList<ScheduledNote> notes,
        IReadOnlyList<TuneBar> bars)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(bars);

        var noChordRanges = new List<(long Start, long End)>();
        foreach (var (bar, localBarIndex) in bars.Select((bar, index) => (bar, index)))
        {
            var barStart = (long)localBarIndex * bar.BarTicks;
            var changes = bar.ChordChanges.OrderBy(change => change.StartBeat).ToArray();
            for (var index = 0; index < changes.Length; index++)
            {
                if (!changes[index].Chord.IsNoChord)
                {
                    continue;
                }

                var start = barStart + changes[index].StartBeat * SessionConstants.Ppq;
                var end = index + 1 < changes.Length
                    ? barStart + changes[index + 1].StartBeat * SessionConstants.Ppq
                    : barStart + bar.BarTicks;
                noChordRanges.Add((start, end));
            }
        }

        if (noChordRanges.Count == 0)
        {
            return notes;
        }

        return notes
            .Where(note => note.Channel is not (SessionConstants.BassChannel or SessionConstants.PianoChannel) ||
                           !noChordRanges.Any(range => note.StartTick < range.End && note.EndTick > range.Start))
            .ToArray();
    }
}
