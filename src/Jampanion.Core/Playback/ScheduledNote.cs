namespace Jampanion.Core.Playback;

public readonly record struct ScheduledNote(
    long StartTick,
    long DurationTicks,
    byte NoteNumber,
    byte Velocity,
    byte Channel,
    bool AllowSamePitchTouch = false)
{
    public long EndTick => StartTick + DurationTicks;
}
