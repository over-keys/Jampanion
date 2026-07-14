using Jampanion.Core.Analysis;
using Jampanion.Core.Music;

namespace Jampanion.Core.Generation;

internal static class SwingTiming
{
    // The ride cymbal remains the ensemble reference. Bass sits slightly ahead of it
    // with a stable, phrase-level offset rather than independent random jitter.
    // The form's structural high stage receives a little more lead, but the range
    // stays small enough to sound like forward motion rather than a flam.
    public const long TwoBeatBassLeadTicks = 8;
    public const long FourBeatBassLeadTicks = 10;
    public const long MaximumBassLeadTicks = 15;
    public const long RideDelayTicks = 4;
    public const long HiHatDelayTicks = 3;
    public const long KickDelayTicks = 1;

    public static long BassStart(
        long gridTick,
        RhythmFeel feel,
        PerformanceGuidance guidance,
        PhraseFunction function)
    {
        if (gridTick == 0)
        {
            return 0;
        }

        var lead = feel == RhythmFeel.TwoBeat
            ? TwoBeatBassLeadTicks
            : FourBeatBassLeadTicks;

        if (guidance.HighStage && feel == RhythmFeel.FourBeat)
        {
            lead += 5;
        }

        if (function is PhraseFunction.Build or PhraseFunction.Setup)
        {
            lead += 1;
        }

        return Math.Max(0, gridTick - Math.Min(MaximumBassLeadTicks, lead));
    }

    // Ending figures use the normal four-beat lead. They are ensemble hits rather
    // than a sustained groove, so no energy-dependent extra push is applied.
    public static long BassStart(long gridTick)
        => gridTick == 0 ? 0 : Math.Max(0, gridTick - FourBeatBassLeadTicks);

    public static long DrumStart(long gridTick, long delayTicks)
        => Math.Max(0, gridTick + delayTicks);

    public static long PianoDelay(int seed, bool highStage = false)
        => highStage
            ? 8 + (long)Math.Round(DeterministicNoise.Unit(seed, 1601) * 5)
            : 12 + (long)Math.Round(DeterministicNoise.Unit(seed, 1601) * 6);

    public static long ClampDuration(long start, long requestedDuration, long segmentLength)
        => Math.Max(1, Math.Min(requestedDuration, segmentLength - start));

    public static long SegmentLengthTicks => (long)SessionConstants.BarsPerSegment * SessionConstants.BarTicks;
}
