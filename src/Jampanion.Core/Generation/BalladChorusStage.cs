using Jampanion.Core.Music;

namespace Jampanion.Core.Generation;

internal enum BalladChorusStage
{
    Theme,
    QuietSolo,
    MovingTwoFeel,
    FourFeel,
    HeadOut
}

internal static class BalladChorusArc
{
    public static BalladChorusStage Resolve(
        int chorus,
        int absoluteBarIndex,
        int halfChorusBars,
        bool isEndingForm)
    {
        if (chorus < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chorus));
        }

        if (absoluteBarIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteBarIndex));
        }

        if (halfChorusBars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(halfChorusBars));
        }

        if (isEndingForm)
        {
            return BalladChorusStage.HeadOut;
        }

        if (chorus == 1)
        {
            return BalladChorusStage.Theme;
        }

        var secondHalf = absoluteBarIndex >= halfChorusBars;
        return chorus switch
        {
            2 => secondHalf ? BalladChorusStage.MovingTwoFeel : BalladChorusStage.QuietSolo,
            _ => BalladChorusStage.FourFeel
        };
    }
}
