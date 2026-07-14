namespace Jampanion.Core.Music;

public sealed class TuneBar
{
    public TuneBar(int index, string section, ChordSpec chord)
        : this(index, section, SessionConstants.BeatsPerBar, [new ChordChange(0, chord)])
    {
    }

    public TuneBar(int index, string section, IReadOnlyList<ChordChange> chordChanges)
        : this(index, section, SessionConstants.BeatsPerBar, chordChanges)
    {
    }

    public TuneBar(int index, string section, int beatsPerBar, IReadOnlyList<ChordChange> chordChanges)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (beatsPerBar is < SessionConstants.MinimumSupportedBeatsPerBar or > SessionConstants.MaximumSupportedBeatsPerBar)
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerBar));
        }

        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(chordChanges);

        if (chordChanges.Count == 0)
        {
            throw new ArgumentException("A bar must contain at least one chord.", nameof(chordChanges));
        }

        var ordered = chordChanges.OrderBy(change => change.StartBeat).ToArray();
        if (ordered[0].StartBeat != 0)
        {
            throw new ArgumentException("The first chord in a bar must begin on beat 1.", nameof(chordChanges));
        }

        if (ordered.Any(change => change.StartBeat >= beatsPerBar))
        {
            throw new ArgumentException("A chord change begins outside the bar's time signature.", nameof(chordChanges));
        }

        if (ordered.Select(change => change.StartBeat).Distinct().Count() != ordered.Length)
        {
            throw new ArgumentException("Chord changes in a bar must begin on different beats.", nameof(chordChanges));
        }

        Index = index;
        Section = section.Trim();
        BeatsPerBar = beatsPerBar;
        ChordChanges = ordered;
    }

    public int Index { get; }
    public string Section { get; }
    public int BeatsPerBar { get; }
    public long BarTicks => SessionConstants.GetBarTicks(BeatsPerBar);
    public IReadOnlyList<ChordChange> ChordChanges { get; }

    // Backward-compatible access for paths that only need the opening harmony.
    public ChordSpec Chord => ChordChanges[0].Chord;

    public string DisplaySymbol => string.Join(" / ", ChordChanges.Select(change => change.Chord.Symbol));

    public ChordSpec GetChordAtBeat(int beat)
    {
        if (beat is < 0 || beat >= BeatsPerBar)
        {
            throw new ArgumentOutOfRangeException(nameof(beat));
        }

        return ChordChanges.Last(change => change.StartBeat <= beat).Chord;
    }

    public ChordSpec GetChordAtTick(long tickWithinBar)
    {
        if (tickWithinBar is < 0 || tickWithinBar >= BarTicks)
        {
            throw new ArgumentOutOfRangeException(nameof(tickWithinBar));
        }

        var beat = (int)(tickWithinBar / SessionConstants.Ppq);
        return GetChordAtBeat(beat);
    }
}
