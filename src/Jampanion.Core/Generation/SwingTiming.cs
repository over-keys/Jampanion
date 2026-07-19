using Jampanion.Core.Analysis;
using Jampanion.Core.Music;

namespace Jampanion.Core.Generation;

internal static class SwingTiming
{
    public static long BassStart(
        long gridTick,
        RhythmFeel feel,
        PerformanceGuidance guidance,
        PhraseFunction function,
        TimeFeelProfile profile)
    {
        if (gridTick == 0)
        {
            return 0;
        }

        var start = profile.Place(gridTick, TimeFeelRole.Bass);
        var extraLeadMilliseconds = feel == RhythmFeel.FourBeat ? 1.5 : 0;

        if (guidance.HighStage && feel == RhythmFeel.FourBeat)
        {
            extraLeadMilliseconds += 2.0;
        }

        if (function is PhraseFunction.Build or PhraseFunction.Setup)
        {
            extraLeadMilliseconds += 0.6;
        }

        return Math.Max(0, start - profile.MillisecondsToTicks(extraLeadMilliseconds));
    }

    public static long BassStart(long gridTick, TimeFeelProfile profile)
        => gridTick == 0 ? 0 : profile.Place(gridTick, TimeFeelRole.Bass);

    public static long DrumStart(long gridTick, TimeFeelRole role, TimeFeelProfile profile)
        => profile.Place(gridTick, role);

    public static long PianoStart(long gridTick, int seed, bool highStage, TimeFeelProfile profile)
    {
        var baseStart = profile.Place(gridTick, TimeFeelRole.Piano);
        var spreadMilliseconds = highStage ? 1.8 : 2.8;
        return Math.Max(0, baseStart + profile.MillisecondsToTicks(
            (DeterministicNoise.Unit(seed, 1601) - 0.5) * spreadMilliseconds));
    }

    public static long ClampDuration(long start, long requestedDuration, long segmentLength)
        => Math.Max(1, Math.Min(requestedDuration, segmentLength - start));

    public static long SegmentLengthTicks => (long)SessionConstants.BarsPerSegment * SessionConstants.BarTicks;
}
