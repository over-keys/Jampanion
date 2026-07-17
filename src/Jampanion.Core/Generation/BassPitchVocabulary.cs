using Jampanion.Core.Music;

namespace Jampanion.Core.Generation;

/// <summary>
/// Keeps bass selection anchored to functional chord tones. Piano colour tones
/// such as 6ths and 9ths remain available to the harmony without becoming
/// automatic bass targets.
/// </summary>
internal static class BassPitchVocabulary
{
    public static IReadOnlyList<int> StructuralChordPitchClasses(ChordSpec chord)
    {
        if (chord.IsOnChord)
        {
            return chord.OnChordBassPitchClasses.Select(Mod12).Distinct().ToArray();
        }

        var result = new List<int>();
        Add(result, chord.BassFoundationPitchClass);
        Add(result, ThirdOrSuspensionPitchClass(chord));
        Add(result, FifthPitchClass(chord));
        Add(result, SeventhPitchClass(chord));
        return result;
    }

    public static int? ThirdPitchClass(ChordSpec chord) =>
        FindChordToneByIntervals(chord, 3, 4);

    public static int? FifthPitchClass(ChordSpec chord) =>
        FindChordToneByIntervals(chord, 6, 7, 8) ?? Mod12(chord.BassFifth);

    public static int? SeventhPitchClass(ChordSpec chord)
    {
        var seventh = FindChordToneByIntervals(chord, 10, 11);
        if (seventh is not null)
        {
            return seventh;
        }

        // Interval 9 is a diminished seventh only in diminished harmony. In a
        // 6, 6/9 or 13 chord it is a colour tone and should not steer the bass.
        var symbol = chord.Symbol.ToLowerInvariant();
        return symbol.Contains("dim", StringComparison.Ordinal)
            ? FindChordToneByIntervals(chord, 9)
            : null;
    }

    public static IReadOnlyList<int> RootApproachPitchClasses(ChordSpec chord)
    {
        var root = Mod12(chord.BassFoundationPitchClass);
        return [Mod12(root - 1), Mod12(root + 1)];
    }

    private static int? ThirdOrSuspensionPitchClass(ChordSpec chord)
    {
        var third = ThirdPitchClass(chord);
        if (third is not null)
        {
            return third;
        }

        var symbol = chord.Symbol.ToLowerInvariant();
        if (symbol.Contains("sus4", StringComparison.Ordinal) ||
            symbol.Contains("sus", StringComparison.Ordinal) &&
            !symbol.Contains("sus2", StringComparison.Ordinal))
        {
            return FindChordToneByIntervals(chord, 5);
        }

        return symbol.Contains("sus2", StringComparison.Ordinal)
            ? FindChordToneByIntervals(chord, 2)
            : null;
    }

    private static int? FindChordToneByIntervals(ChordSpec chord, params int[] intervals)
    {
        var root = chord.RootPitchClass;
        var available = chord.BassPitchClasses.Select(Mod12).ToHashSet();
        foreach (var interval in intervals)
        {
            var pitchClass = Mod12(root + interval);
            if (available.Contains(pitchClass))
            {
                return pitchClass;
            }
        }

        return null;
    }

    private static void Add(ICollection<int> result, int? pitchClass)
    {
        if (pitchClass is int value && !result.Contains(Mod12(value)))
        {
            result.Add(Mod12(value));
        }
    }

    private static int Mod12(int value) => (value % 12 + 12) % 12;
}
