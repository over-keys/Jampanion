namespace Jampanion.Core.Music;

public static class ChordFactory
{
    public static ChordSpec Major(string root, string? symbol = null) =>
        Create(root, symbol ?? root, [0, 4, 7], [4, 7, 0, 2]);

    public static ChordSpec Minor(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m", [0, 3, 7], [3, 7, 0, 2]);

    public static ChordSpec Major7(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}maj7", [0, 4, 7, 11], [4, 11, 2, 9]);

    public static ChordSpec Major7Altered(
        string root,
        string quality,
        string? symbol = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quality);

        var lower = quality.ToLowerInvariant();
        var bassIntervals = new List<int> { 0, 4, 7, 11 };
        var pianoIntervals = new List<int> { 4, 11, 2, 9 };

        // A major-seventh chord keeps its major 3rd and major 7th.  Only
        // explicitly written alterations are added or substituted; unlike a
        // dominant chord, the basic harmony must never become minor.
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

    public static ChordSpec Major6(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}6", [0, 4, 7, 9], [4, 9, 2, 7]);

    public static ChordSpec Major9(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}maj9", [0, 4, 7, 11], [4, 11, 2, 9]);

    public static ChordSpec Major11(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}maj11", [0, 4, 7, 11], [4, 11, 5, 2]);

    public static ChordSpec Major13(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}maj13", [0, 4, 7, 9, 11], [4, 11, 9, 2]);

    public static ChordSpec Major13Altered(
        string root,
        string quality,
        string? symbol = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quality);

        var lower = quality.ToLowerInvariant();
        var bassIntervals = new List<int> { 0, 4, 7, 9, 11 };
        var pianoIntervals = new List<int> { 4, 11, 9, 2 };
        ApplyAlteration(lower, "#11", natural: 5, altered: 6, bassIntervals: null, pianoIntervals);
        ApplyAlteration(lower, "b9", natural: 2, altered: 1, bassIntervals: null, pianoIntervals);
        ApplyAlteration(lower, "#9", natural: 2, altered: 3, bassIntervals: null, pianoIntervals);
        ApplyAlteration(lower, "b5", natural: 7, altered: 6, bassIntervals, pianoIntervals);
        ApplyAlteration(lower, "#5", natural: 7, altered: 8, bassIntervals, pianoIntervals);

        return Create(
            root,
            symbol ?? root + quality,
            bassIntervals.Distinct().ToArray(),
            pianoIntervals.Distinct().ToArray());
    }

    public static ChordSpec MajorSixNine(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}6/9", [0, 4, 7, 9], [4, 9, 2, 7]);

    public static ChordSpec MajorAdd4(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}maj(add4)", [0, 4, 5, 7], [4, 5, 7, 2]);

    public static ChordSpec Power(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}5", [0, 7], [0, 7]);

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

    public static ChordSpec MinorMajor7(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}mMaj7", [0, 3, 7, 11], [3, 11, 2, 9]);

    public static ChordSpec MinorMajor9(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}mMaj9", [0, 3, 7, 11], [3, 11, 2, 9]);

    public static ChordSpec MinorMajor11(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}mMaj11", [0, 3, 7, 11], [3, 11, 5, 2]);

    public static ChordSpec MinorMajor13(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}mMaj13", [0, 3, 7, 9, 11], [3, 11, 9, 2]);

    public static ChordSpec MinorAdd4(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m(add4)", [0, 3, 5, 7], [3, 5, 7, 2]);

    public static ChordSpec MinorAdd9(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}madd9", [0, 3, 7], [3, 7, 2, 0]);

    public static ChordSpec MinorSixNine(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m6/9", [0, 3, 7, 9], [3, 9, 2, 7]);

    public static ChordSpec MinorFlatSix(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}mb6", [0, 3, 7, 8], [3, 8, 2, 7]);

    public static ChordSpec MinorSharpFive(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m#5", [0, 3, 8], [3, 8, 0, 2]);

    public static ChordSpec Minor7FlatSix(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m7b6", [0, 3, 7, 10], [3, 10, 8, 2]);

    public static ChordSpec Minor9FlatSix(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m9b6", [0, 3, 7, 10], [3, 10, 2, 8]);

    public static ChordSpec HalfDiminished9(string root, string? symbol = null) =>
        Create(root, symbol ?? $"{root}m7b5(9)", [0, 3, 6, 10], [3, 10, 6, 2]);

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

    /// <summary>
    /// Supplies a stable jazz colour for the one-bar tonic hold at the very
    /// end of a performance.  Plain major tonics use a 6/9 colour and plain
    /// minor tonics use m6/9; explicitly written qualities (maj7, m7, sus,
    /// altered, etc.) are left intact because their tension is intentional.
    /// </summary>
    public static ChordSpec ApplyEndingTensions(ChordSpec chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        if (chord.IsNoChord)
        {
            return chord;
        }

        var harmony = chord.Symbol.Split('/', 2)[0].ToLowerInvariant();
        var isMinor = harmony.Contains('m') &&
            !harmony.Contains("maj", StringComparison.Ordinal) &&
            !harmony.Contains("dim", StringComparison.Ordinal);
        var hasExplicitQuality = harmony.Length > 0 &&
            (harmony.Contains('7') ||
             harmony.Contains('6') ||
             harmony.Contains('9') ||
             harmony.Contains("11", StringComparison.Ordinal) ||
             harmony.Contains("13", StringComparison.Ordinal) ||
             harmony.Contains('^') ||
             harmony.Contains("add", StringComparison.Ordinal) ||
             harmony.EndsWith('2') ||
             harmony.EndsWith('5') ||
             harmony.Contains("sus", StringComparison.Ordinal) ||
             harmony.Contains("alt", StringComparison.Ordinal) ||
             harmony.Contains("dim", StringComparison.Ordinal) ||
             harmony.Contains("aug", StringComparison.Ordinal));
        if (hasExplicitQuality)
        {
            return chord;
        }

        // The bass already holds the root.  Keep the piano in a compact
        // upper structure, with the 6th and 9th providing colour without a
        // potentially harsh major-7th against an unqualified tonic.
        var intervals = isMinor
            ? new[] { 3, 9, 2, 7 } // m6/9: b3, 6, 9, 5
            : new[] { 4, 9, 2, 7 }; // 6/9: 3, 6, 9, 5
        var pianoPitchClasses = intervals
            .Select(interval => Mod12(chord.RootPitchClass + interval))
            .Distinct()
            .ToArray();

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

    public static ChordSpec SuspendedAltered(
        string root,
        string quality,
        string? symbol = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quality);

        var lower = quality.ToLowerInvariant();
        var bassIntervals = new List<int> { 0, 5, 7, 10 };
        var pianoIntervals = new List<int> { 5, 10, 2, 7 };
        if (lower.Contains("add3", StringComparison.Ordinal))
        {
            bassIntervals.Add(4);
            pianoIntervals.Add(4);
        }

        ApplyAlteration(lower, "b9", natural: 2, altered: 1, bassIntervals: null, pianoIntervals);
        ApplyAlteration(lower, "b13", natural: 9, altered: 8, bassIntervals: null, pianoIntervals);
        if (lower.StartsWith("13", StringComparison.Ordinal))
        {
            pianoIntervals.Add(9);
        }

        return Create(
            root,
            symbol ?? root + quality,
            bassIntervals.Distinct().ToArray(),
            pianoIntervals.Distinct().ToArray());
    }

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
        var isMinorMajor = harmony.Contains("mmaj", StringComparison.Ordinal) ||
            harmony.Contains("m^", StringComparison.Ordinal) ||
            harmony.Contains("min^", StringComparison.Ordinal);
        return harmony.Contains('m') &&
            (!harmony.Contains("maj", StringComparison.Ordinal) || isMinorMajor) &&
            !harmony.Contains("dim", StringComparison.Ordinal);
    }

    private static int Mod12(int value) => (value % 12 + 12) % 12;
}
