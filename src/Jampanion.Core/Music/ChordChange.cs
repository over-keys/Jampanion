namespace Jampanion.Core.Music;

public sealed record ChordChange
{
    public ChordChange(int startBeat, ChordSpec chord)
    {
        if (startBeat is < 0 or >= SessionConstants.MaximumSupportedBeatsPerBar)
        {
            throw new ArgumentOutOfRangeException(nameof(startBeat));
        }

        ArgumentNullException.ThrowIfNull(chord);
        StartBeat = startBeat;
        Chord = chord;
    }

    public int StartBeat { get; }
    public ChordSpec Chord { get; }
}
