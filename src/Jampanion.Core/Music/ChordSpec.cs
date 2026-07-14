namespace Jampanion.Core.Music;

public sealed record ChordSpec(
    string Symbol,
    byte BassRoot,
    byte BassFifth,
    IReadOnlyList<byte> PianoVoicing,
    IReadOnlyList<int> BassPitchClasses,
    IReadOnlyList<int> PianoPitchClasses,
    int? HarmonicRootPitchClass = null)
{
    // N.C. keeps a harmless C-shaped vocabulary for generators that need a
    // concrete context; playback removes bass and piano notes in its range.
    public static ChordSpec NoChord { get; } = new(
        "N.C.",
        36,
        43,
        new byte[] { 60, 64, 67 },
        new int[] { 0, 7 },
        new int[] { 0, 4, 7 },
        0)
    {
        IsNoChord = true
    };

    public bool IsNoChord { get; init; }
    public int RootPitchClass => HarmonicRootPitchClass ?? BassRoot % 12;

    // A slash chord's written bass note is the bass root.  The harmonic root
    // remains available to the piano, while the bass stays on the slash-root
    // octave and may use only that root's fifth as a supporting tone.
    public bool IsOnChord => HarmonicRootPitchClass is int harmonic && harmonic != BassRoot % 12;

    public int BassFoundationPitchClass => BassRoot % 12;

    public IReadOnlyList<int> OnChordBassPitchClasses
    {
        get
        {
            var result = new List<int> { BassFoundationPitchClass };
            var fifth = BassFifth % 12;
            if (fifth != BassFoundationPitchClass)
            {
                result.Add(fifth);
            }

            return result;
        }
    }
}
