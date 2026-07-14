namespace Jampanion.Core.Generation;

internal enum BossaChorusStage
{
    Opening,
    FirstSolo,
    Standard,
    Lifted,
    HeadOut
}

internal static class BossaChorusArc
{
    public static BossaChorusStage Resolve(int chorus, bool isEndingForm)
    {
        if (chorus < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chorus));
        }

        if (isEndingForm)
        {
            return BossaChorusStage.HeadOut;
        }

        // Keep the first solo distinct from the head: it opens the bass and
        // cross-stick pulse without yet adding the cabasa or high-stage accents.
        // Chorus 3 establishes the regular solo texture; chorus 4 onward lifts.
        return chorus switch
        {
            1 => BossaChorusStage.Opening,
            2 => BossaChorusStage.FirstSolo,
            3 => BossaChorusStage.Standard,
            _ => BossaChorusStage.Lifted
        };
    }
}
