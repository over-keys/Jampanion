namespace Jampanion.Core.Music;

public static class ChordFactory
{
    public static ChordSpec Major(string root, string? symbol = null) =>
        Create(root, symbol ?? root, [0, 4, 7], [4, 7, 0, 2]);

    public static ChordSpec Minor(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m", [0, 3, 7], [3, 7, 0, 2]);

    public static ChordSpec Major7(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}maj7", [0, 4, 7, 11], [4, 11, 2, 9]);

    public static ChordSpec Major6(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}6", [0, 4, 7, 9], [4, 9, 2, 7]);

    public static ChordSpec Major9(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}maj9", [0, 4, 7, 11], [4, 11, 2, 9]);

    public static ChordSpec Major11(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}maj11", [0, 4, 7, 11], [4, 11, 5, 2]);

    public static ChordSpec Major13(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}maj13", [0, 4, 7, 9, 11], [4, 11, 9, 2]);

    public static ChordSpec MajorSixNine(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}6/9", [0, 4, 7, 9], [4, 9, 2, 7]);

    public static ChordSpec Add9(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}add9", [0, 4, 7], [4, 7, 2, 0]);

    public static ChordSpec Minor7(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m7", [0, 3, 7, 10], [3, 10, 2, 7]);

    public static ChordSpec Minor9(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m9", [0, 3, 7, 10], [3, 10, 2, 7]);

    public static ChordSpec Minor11(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m11", [0, 3, 7, 10], [3, 10, 5, 2]);

    public static ChordSpec Minor13(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m13", [0, 3, 7, 9, 10], [3, 10, 9, 2]);

    public static ChordSpec Dominant7(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}7", [0, 4, 7, 10], [4, 10, 2, 9]);

    public static ChordSpec Dominant(
        string root,
        string quality,
        string? symbol = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quality);
        var lower = quality.ToLowerInvariant();
        var bassIntervals = new List<int> { 0, 4, 7, 10 };
        var pianoIntervals = new List<int> { 4, 10 };
        var hasAlteration = lower.Contains('b') || lower.Contains('#');

        if (lower.StartsWith("13", StringComparison.Ordinal))
        {
            pianoIntervals.AddRange([2, 9]);
        }
        else if (lower.StartsWith("11", StringComparison.Ordinal))
        {
            pianoIntervals.AddRange([2, 5]);
        }
        else if (lower.StartsWith("9", StringComparison.Ordinal))
        {
            pianoIntervals.AddRange([2, 9]);
        }
        else if (!hasAlteration)
        {
            // Unaltered 7th chords retain the established jazz colour voicing.
            pianoIntervals.AddRange([2, 9]);
        }

        ApplyAlteration(lower, "b5", natural: 7, altered: 6, bassIntervals, pianoIntervals);
        ApplyAlteration(lower, "#5", natural: 7, altered: 8, bassIntervals, pianoIntervals);
        ApplyAlteration(lower, "b9", natural: 2, altered: 1, bassIntervals: null, pianoIntervals);
        ApplyAlteration(lower, "#9", natural: 2, altered: 3, bassIntervals: null, pianoIntervals);
        ApplyAlteration(lower, "#11", natural: 5, altered: 6, bassIntervals: null, pianoIntervals);
        ApplyAlteration(lower, "b13", natural: 9, altered: 8, bassIntervals: null, pianoIntervals);

        return Create(
            root,
            symbol ?? root + quality,
            bassIntervals.Distinct().ToArray(),
            pianoIntervals.Distinct().ToArray());
    }

    public static ChordSpec AlteredDominant(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}7alt", [0, 4, 7, 10, 1, 8], [4, 10, 1, 8]);


    public static ChordSpec MinorKeyDominant(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}7", [0, 4, 7, 10], [4, 10, 1, 8]);

    public static ChordSpec ApplyMinorTargetTensions(ChordSpec chord, ChordSpec nextChord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        ArgumentNullException.ThrowIfNull(nextChord);

        if (!IsDominantSeventh(chord) ||
            !IsMinorHarmony(nextChord) ||
            !ResolvesDownPerfectFifth(chord, nextChord))
        {
            return chord;
        }

        var naturalNinth = Mod12(chord.RootPitchClass + 2);
        var flatNinth = Mod12(chord.RootPitchClass + 1);
        var naturalThirteenth = Mod12(chord.RootPitchClass + 9);
        var flatThirteenth = Mod12(chord.RootPitchClass + 8);
        var pianoPitchClasses = chord.PianoPitchClasses
            .Select(pitchClass => pitchClass == naturalNinth
                ? flatNinth
                : pitchClass == naturalThirteenth
                    ? flatThirteenth
                    : pitchClass)
            .Distinct()
            .ToArray();

        if (pianoPitchClasses.SequenceEqual(chord.PianoPitchClasses))
        {
            return chord;
        }

        return chord with
        {
            PianoPitchClasses = pianoPitchClasses,
            PianoVoicing = BuildAscendingVoicing(pianoPitchClasses)
        };
    }

    private static bool ResolvesDownPerfectFifth(ChordSpec dominant, ChordSpec target)
        => Mod12(target.RootPitchClass - dominant.RootPitchClass) == 5;

    public static ChordSpec GetFollowingChord(
        TuneBar bar,
        long offset,
        ChordSpec nextBarChord)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(nextBarChord);

        return bar.ChordChanges
            .FirstOrDefault(change => change.StartBeat * SessionConstants.Ppq > offset)
            ?.Chord ?? nextBarChord;
    }

    public static ChordSpec Minor7Flat5(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m7b5", [0, 3, 6, 10], [3, 10, 6, 5]);

    public static ChordSpec Minor6(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m6", [0, 3, 7, 9], [3, 9, 2, 7]);

    public static ChordSpec Diminished(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}dim", [0, 3, 6], [3, 6, 0, 9]);

    public static ChordSpec Diminished7(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}dim7", [0, 3, 6, 9], [3, 6, 9, 0]);

    public static ChordSpec Augmented(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}aug", [0, 4, 8], [4, 8, 0, 2]);

    public static ChordSpec Suspended4(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}sus4", [0, 5, 7, 10], [5, 10, 2, 7]);

    public static ChordSpec Suspended2(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}sus2", [0, 2, 7, 10], [2, 10, 5, 7]);

    public static ChordSpec Slash(ChordSpec harmony, string bassNote, string? symbol = null)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        var bassPitchClass = ParsePitchClass(bassNote);
        var bassRoot = BassNoteForPitchClass(bassPitchClass);
        var bassFifth = FifthForBass(bassRoot);
        var bassPitchClasses = harmony.BassPitchClasses
            .Append(bassPitchClass)
            .Distinct()
            .ToArray();

        return harmony with
        {
            Symbol = symbol ?? $"{harmony.Symbol}/{bassNote}",
            BassRoot = bassRoot,
            BassFifth = bassFifth,
            BassPitchClasses = bassPitchClasses,
            HarmonicRootPitchClass = harmony.RootPitchClass
        };
    }

    private static void ApplyAlteration(
        string quality,
        string token,
        int natural,
        int altered,
        List<int>? bassIntervals,
        List<int> pianoIntervals)
    {
        if (!quality.Contains(token, StringComparison.Ordinal))
        {
            return;
        }

        bassIntervals?.RemoveAll(interval => interval == natural);
        if (bassIntervals is not null && !bassIntervals.Contains(altered))
        {
            bassIntervals.Add(altered);
        }

        pianoIntervals.RemoveAll(interval => interval == natural);
        if (!pianoIntervals.Contains(altered))
        {
            pianoIntervals.Add(altered);
        }
    }

    private static ChordSpec Create(
        string root,
        string symbol,
        IReadOnlyList<int> bassIntervals,
        IReadOnlyList<int> pianoIntervals)
    {
        var rootPitchClass = ParsePitchClass(root);
        var bassRoot = BassNoteForPitchClass(rootPitchClass);
        var bassFifth = FifthForBass(bassRoot);
        var bassPitchClasses = bassIntervals
            .Select(interval => Mod12(rootPitchClass + interval))
            .Distinct()
            .ToArray();
        var pianoPitchClasses = pianoIntervals
            .Select(interval => Mod12(rootPitchClass + interval))
            .Distinct()
            .ToArray();
        var pianoVoicing = BuildAscendingVoicing(pianoPitchClasses);

        return new ChordSpec(
            symbol,
            bassRoot,
            bassFifth,
            pianoVoicing,
            bassPitchClasses,
            pianoPitchClasses,
            rootPitchClass);
    }

    private static byte[] BuildAscendingVoicing(IReadOnlyList<int> pitchClasses)
    {
        var notes = new byte[pitchClasses.Count];
        var minimums = new[] { 48, 53, 58, 63 };
        var previous = 47;

        for (var i = 0; i < pitchClasses.Count; i++)
        {
            var minimum = Math.Max(previous + 1, minimums[Math.Min(i, minimums.Length - 1)]);
            var note = minimum;
            while (Mod12(note) != pitchClasses[i])
            {
                note++;
            }

            while (note > 72 && note - 12 > previous)
            {
                note -= 12;
            }

            notes[i] = (byte)note;
            previous = note;
        }

        return notes;
    }

    private static byte BassNoteForPitchClass(int pitchClass)
    {
        var note = 31;
        while (Mod12(note) != pitchClass)
        {
            note++;
        }

        return (byte)note;
    }

    private static byte FifthForBass(byte bassRoot)
    {
        var fifth = bassRoot + 7;
        return (byte)(fifth <= 43 ? fifth : fifth - 12);
    }

    private static int ParsePitchClass(string noteName) => noteName.Trim() switch
    {
        "C" or "B#" => 0,
        "C#" or "Db" => 1,
        "D" => 2,
        "D#" or "Eb" => 3,
        "E" or "Fb" => 4,
        "F" or "E#" => 5,
        "F#" or "Gb" => 6,
        "G" => 7,
        "G#" or "Ab" => 8,
        "A" => 9,
        "A#" or "Bb" => 10,
        "B" or "Cb" => 11,
        _ => throw new ArgumentException($"Unknown note name: {noteName}", nameof(noteName))
    };

    private static bool IsDominantSeventh(ChordSpec chord)
    {
        var harmony = chord.Symbol.Split('/', 2)[0].ToLowerInvariant();
        return (harmony.Contains('7') || harmony is "9" or "11" or "13") &&
            !harmony.Contains("maj", StringComparison.Ordinal) &&
            !harmony.Contains('m') &&
            !harmony.Contains("dim", StringComparison.Ordinal) &&
            !harmony.Contains("sus", StringComparison.Ordinal);
    }

    private static bool IsMinorHarmony(ChordSpec chord)
    {
        var harmony = chord.Symbol.Split('/', 2)[0].ToLowerInvariant();
        return harmony.Contains('m') &&
            !harmony.Contains("maj", StringComparison.Ordinal) &&
            !harmony.Contains("dim", StringComparison.Ordinal);
    }

    private static int Mod12(int value) => (value % 12 + 12) % 12;
}
