using Jampanion.Core.Music;

namespace Jampanion.Core.Playback;

public sealed class FeelTransitionState
{
    public FeelTransitionState(RhythmFeel initialFeel)
    {
        CurrentFeel = initialFeel;
    }

    public RhythmFeel CurrentFeel { get; private set; }
    public RhythmFeel? PendingFeel { get; private set; }

    public bool Request(RhythmFeel feel)
    {
        var previous = PendingFeel;
        PendingFeel = feel == CurrentFeel ? null : feel;
        return previous != PendingFeel;
    }

    public bool Cancel()
    {
        if (PendingFeel is null)
        {
            return false;
        }

        PendingFeel = null;
        return true;
    }

    public void ApplyPlannedBoundary(RhythmFeel plannedFeel)
    {
        CurrentFeel = plannedFeel;

        // A transition can be deliberately deferred across several four-bar
        // generation blocks. Consume the request only when its requested feel
        // is actually applied at a legal musical boundary.
        if (PendingFeel == plannedFeel)
        {
            PendingFeel = null;
        }
    }

    public void Reset(RhythmFeel feel)
    {
        CurrentFeel = feel;
        PendingFeel = null;
    }
}
