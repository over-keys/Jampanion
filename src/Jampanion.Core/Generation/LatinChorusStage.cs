namespace Jampanion.Core.Generation;

internal enum LatinChorusStage
{
    Opening,
    Groove,
    Developing,
    Peak,
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
            2 => LatinChorusStage.Groove,
            3 => LatinChorusStage.Developing,
            _ => LatinChorusStage.Peak
        };
    }
}
