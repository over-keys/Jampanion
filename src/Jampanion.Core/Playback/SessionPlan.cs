using Jampanion.Core.Music;

namespace Jampanion.Core.Playback;

public sealed class SessionPlan
{
    public SessionPlan(
        TuneForm form,
        IReadOnlyList<ScheduledNote> countInNotes,
        IReadOnlyList<ScheduledNote> chorusNotes)
    {
        Form = form;
        CountInNotes = countInNotes;
        ChorusNotes = chorusNotes;
    }

    public TuneForm Form { get; }
    public IReadOnlyList<ScheduledNote> CountInNotes { get; }
    public IReadOnlyList<ScheduledNote> ChorusNotes { get; }
    public long CountInLengthTicks => SessionConstants.CountInBars * Form.BarTicks;
    public long ChorusLengthTicks => (long)Form.Bars.Count * Form.BarTicks;
}
