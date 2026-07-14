using Jampanion.Core.Music;

namespace Jampanion.Core.Playback;

public static class ChorusFeelPlan
{
    public static RhythmFeel GetFeel(int chorus, bool headOutActive)
    {
        if (chorus < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chorus));
        }

        if (headOutActive)
        {
            return RhythmFeel.TwoBeat;
        }

        // Chorus 1: opening theme in two-feel.
        // Chorus 2: first solo chorus remains in two-feel.
        // Chorus 3 onward: four-feel.
        return chorus <= 2 ? RhythmFeel.TwoBeat : RhythmFeel.FourBeat;
    }

    public static bool IsHighStage(int chorus, bool headOutActive)
    {
        if (chorus < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chorus));
        }

        // The third solo chorus begins at Chorus 4. Once reached, the high
        // stage remains active until HEAD OUT overrides the form plan.
        return !headOutActive && chorus >= 4;
    }
}
