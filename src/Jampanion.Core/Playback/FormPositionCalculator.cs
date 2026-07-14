using Jampanion.Core.Music;

namespace Jampanion.Core.Playback;

public static class FormPositionCalculator
{
    public static FormPosition FromAbsoluteSessionTick(
        long absoluteTick,
        int chorusBars = SessionConstants.ChorusBars,
        int beatsPerBar = SessionConstants.BeatsPerBar)
    {
        if (absoluteTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteTick));
        }

        if (chorusBars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chorusBars));
        }

        var barTicks = SessionConstants.GetBarTicks(beatsPerBar);
        var chorusTicks = (long)chorusBars * barTicks;
        var chorusIndex = absoluteTick / chorusTicks;
        var tickWithinChorus = absoluteTick % chorusTicks;
        var barIndex = (int)(tickWithinChorus / barTicks);
        var tickWithinBar = tickWithinChorus % barTicks;
        var beatIndex = (int)(tickWithinBar / SessionConstants.Ppq);

        return new FormPosition(
            Chorus: checked((int)chorusIndex + 1),
            Bar: barIndex + 1,
            Beat: beatIndex + 1,
            BarIndex: barIndex,
            TickWithinChorus: tickWithinChorus);
    }
}
