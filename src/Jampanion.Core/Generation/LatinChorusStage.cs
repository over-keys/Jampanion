namespace Jampanion.Core.Generation;

internal enum LatinChorusStage
{
    Opening,
    Ponchando,
    Montuno,
    Mambo,
    HeadOut
}

internal static class LatinChorusArc
{
    public static LatinChorusStage Resolve(int chorus, bool isEndingForm)
    {
        if (chorus < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chorus));
        }

        if (isEndingForm)
        {
            return LatinChorusStage.HeadOut;
        }

        return chorus switch
        {
            1 => LatinChorusStage.Opening,
            2 => LatinChorusStage.Ponchando,
            3 => LatinChorusStage.Montuno,
            _ => LatinChorusStage.Mambo
        };
    }
}
