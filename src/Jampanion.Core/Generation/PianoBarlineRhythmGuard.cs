using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class PianoBarlineRhythmGuard
{
    private const long GridTolerance = 24;
    private const long OneAndOffset = SessionConstants.Ppq * 2 / 3;
    private const long FourAndOffset = SessionConstants.BarTicks - SessionConstants.Ppq / 3;

    // When a phrase enters on 4&, beat 1 may not mechanically re-strike the
    // chord. The 4& may ring across the barline or end on its own; an attack on
    // 1& remains legal and creates the idiomatic short 4& | 1& alternative.
    public static bool SuppressDownbeatAfterFourAnd(bool previousBarEndedOnFourAnd, long offset) =>
        previousBarEndedOnFourAnd && Math.Abs(offset) <= GridTolerance;

    public static bool IsFourAnd(long offset) =>
        Math.Abs(offset - FourAndOffset) <= GridTolerance;

    public static bool IsOneAnd(long offset) =>
        Math.Abs(offset - OneAndOffset) <= GridTolerance;

    // Final event-level guard. This catches any downbeat that may be introduced
    // by chord-change expansion or survive across a cell boundary. Rolled
    // voicings can spread an attack by roughly 30 ticks, so the event window is
    // deliberately wider than the planning-grid tolerance.
    public static IReadOnlyList<ScheduledNote> RemoveForbiddenDownbeats(
        IEnumerable<ScheduledNote> source,
        long barTicks = SessionConstants.BarTicks,
        TimeFeelProfile? timeFeel = null)
    {
        const long eventTolerance = 48;
        var performedFourAndOffset = timeFeel?.MapGrid(FourAndOffset) ?? FourAndOffset;
        var notes = source.ToArray();
        var barsEndingOnFourAnd = notes
            .Where(note => note.Channel == SessionConstants.PianoChannel)
            .Where(note => Math.Abs(PositiveModulo(note.StartTick, barTicks) - performedFourAndOffset) <= eventTolerance)
            .Select(note => note.StartTick / barTicks)
            .ToHashSet();
        if (barsEndingOnFourAnd.Count == 0)
        {
            return notes;
        }

        return notes
            .Where(note => note.Channel != SessionConstants.PianoChannel ||
                !barsEndingOnFourAnd.Contains(note.StartTick / barTicks - 1) ||
                PositiveModulo(note.StartTick, barTicks) > eventTolerance)
            .ToArray();
    }

    private static long PositiveModulo(long value, long divisor) =>
        (value % divisor + divisor) % divisor;
}
