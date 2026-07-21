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

}
