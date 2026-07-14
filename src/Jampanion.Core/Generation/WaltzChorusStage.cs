namespace Jampanion.Core.Generation;

internal enum WaltzChorusStage
{
    Opening,
    Standard,
    Developing,
    Lifted,
    HeadOut
}

internal static class WaltzChorusArc
{
    public static WaltzChorusStage Resolve(int chorus, bool isEndingForm)
    {
        if (isEndingForm)
        {
            return WaltzChorusStage.HeadOut;
        }

        return chorus switch
        {
            1 => WaltzChorusStage.Opening,
            2 => WaltzChorusStage.Standard,
            3 => WaltzChorusStage.Developing,
            _ => WaltzChorusStage.Lifted
        };
    }
}
