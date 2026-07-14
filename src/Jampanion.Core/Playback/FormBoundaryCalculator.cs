using Jampanion.Core.Music;

namespace Jampanion.Core.Playback;

public static class FormBoundaryCalculator
{
    public static bool IsSafeTransitionBar(int zeroBasedBarIndex, int chorusBars = SessionConstants.ChorusBars)
    {
        return zeroBasedBarIndex is >= 0 && zeroBasedBarIndex < chorusBars &&
               zeroBasedBarIndex % SessionConstants.BarsPerSegment == 0;
    }

    public static (int Chorus, int Bar) GetNextBoundary(int chorus, int segmentIndex, int segmentCount = SessionConstants.SegmentsPerChorus)
    {
        if (chorus < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chorus));
        }

        if (segmentIndex is < 0 || segmentIndex >= segmentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        }

        if (segmentIndex == segmentCount - 1)
        {
            return (chorus + 1, 1);
        }

        return (chorus, (segmentIndex + 1) * SessionConstants.BarsPerSegment + 1);
    }

    public static bool IsTwoToFourTransitionBar(
        int chorus,
        int zeroBasedBarIndex,
        int chorusBars = SessionConstants.ChorusBars)
    {
        if (chorus < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chorus));
        }

        if (chorusBars < 2 || chorusBars % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chorusBars), "The chorus must contain an even number of bars.");
        }

        if (zeroBasedBarIndex is < 0 || zeroBasedBarIndex >= chorusBars)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroBasedBarIndex));
        }

        var halfChorusBar = chorusBars / 2;
        return zeroBasedBarIndex == halfChorusBar ||
               (chorus > 1 && zeroBasedBarIndex == 0);
    }

    public static RhythmFeel ResolvePlannedFeel(
        RhythmFeel currentFeel,
        RhythmFeel? pendingFeel,
        int targetChorus,
        int targetZeroBasedBarIndex,
        int chorusBars = SessionConstants.ChorusBars)
    {
        if (pendingFeel is null)
        {
            return currentFeel;
        }

        if (currentFeel == RhythmFeel.TwoBeat &&
            pendingFeel == RhythmFeel.FourBeat &&
            !IsTwoToFourTransitionBar(targetChorus, targetZeroBasedBarIndex, chorusBars))
        {
            return currentFeel;
        }

        return pendingFeel.Value;
    }

    public static (int Chorus, int Bar) GetNextTwoToFourBoundary(
        int chorus,
        int oneBasedBar,
        int chorusBars = SessionConstants.ChorusBars)
    {
        if (chorus < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chorus));
        }

        if (chorusBars < 2 || chorusBars % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chorusBars), "The chorus must contain an even number of bars.");
        }

        if (oneBasedBar is < 1 || oneBasedBar > chorusBars)
        {
            throw new ArgumentOutOfRangeException(nameof(oneBasedBar));
        }

        var halfChorusBars = chorusBars / 2;
        return oneBasedBar <= halfChorusBars
            ? (chorus, halfChorusBars + 1)
            : (chorus + 1, 1);
    }
}
