namespace Jampanion.Core.Music;

public static class TuneTransposer
{
    public static TuneKeyInfo GetKeyInfo(TuneForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var normalized = NormalizeKeyName(form.Key);
        if (TryParseRoot(normalized, out var pitchClass, out var rootLength))
        {
            return new TuneKeyInfo(
                normalized,
                pitchClass,
                normalized[rootLength..].Equals("m", StringComparison.OrdinalIgnoreCase),
                normalized[..rootLength].Contains('b', StringComparison.Ordinal));
        }

        return new TuneKeyInfo(
            "C",
            form.TonicChord.RootPitchClass,
            false,
            false);
    }

    public static TuneForm Transpose(TuneForm form, string targetKey, bool? preferFlatsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        var sourceKey = GetKeyInfo(form);
        var target = ParseKey(targetKey);
        var semitones = SignedDistance(sourceKey.PitchClass, target.PitchClass);
        var preferFlats = preferFlatsOverride ?? target.PreferFlats;
        var bars = TransposeBars(form.Bars, semitones, preferFlats);
        var endingBars = form.HasSeparateEndingForm
            ? TransposeBars(form.EndingFormBars, semitones, preferFlats)
            : null;

        return new TuneForm(
            form.Id,
            form.Title,
            target.Name,
            bars,
            form.DefaultTempoBpm,
            endingBars,
            form.OriginalStyle,
            form.TimeSignature,
            form.CodaStartIndex,
            form.SectionStyles);
    }

    public static TuneForm TransposeAuto(TuneForm form, string targetKey)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        var sourceKey = GetKeyInfo(form);
        var target = ParseKey(targetKey);
        if (sourceKey.PitchClass == target.PitchClass && sourceKey.IsMinor == target.IsMinor)
        {
            // Auto means the original .cho spelling wins while the tune remains
            // in its source key. Do not re-spell every chord just because the
            // transposition control was refreshed.
            return form;
        }

        return Transpose(form, targetKey);
    }

    public static bool GetAutoPreferFlats(TuneForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var flatRoots = 0;
        var sharpRoots = 0;
        foreach (var change in form.Bars.SelectMany(bar => bar.ChordChanges))
        {
            var symbol = change.Chord.Symbol.Trim();
            if (change.Chord.IsNoChord || !TryParseRoot(symbol, out _, out var rootLength))
            {
                continue;
            }

            if (symbol[rootLength - 1] == 'b')
            {
                flatRoots++;
            }
            else if (symbol[rootLength - 1] == '#')
            {
                sharpRoots++;
            }
        }

        if (flatRoots == sharpRoots)
        {
            return GetKeyInfo(form).PreferFlats;
        }

        return flatRoots > sharpRoots;
    }

    private static IReadOnlyList<TuneBar> TransposeBars(
        IReadOnlyList<TuneBar> bars,
        int semitones,
        bool preferFlats)
    {
        return bars
            .Select(bar => new TuneBar(
                bar.Index,
                bar.Section,
                bar.BeatsPerBar,
                bar.ChordChanges
                    .Select(change => new ChordChange(
                        change.StartBeat,
                        change.Chord.IsNoChord
                            ? change.Chord
                            : TransposeChord(change.Chord, semitones, preferFlats)))
                    .ToArray()))
            .ToArray();
    }

    private static ChordSpec TransposeChord(ChordSpec chord, int semitones, bool preferFlats)
    {
        var symbol = TransposeSymbol(chord.Symbol, semitones, preferFlats);
        return ChordSymbolParser.Parse(symbol);
    }

    public static string TransposeChordSymbol(string symbol, int semitones, bool preferFlats)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var normalized = symbol.Trim()
            .Replace('♯', '#')
            .Replace('♭', 'b');
        if (normalized is "N.C." or "N.C" or "NC")
        {
            return "N.C.";
        }

        _ = ChordSymbolParser.Parse(normalized);
        return TransposeSymbol(normalized, semitones, preferFlats);
    }

    public static string RespellChordSymbol(string symbol, bool preferFlats) =>
        TransposeChordSymbol(symbol, 0, preferFlats);

    private static string TransposeSymbol(string symbol, int semitones, bool preferFlats)
    {
        var normalized = symbol.Trim();
        if (!TryParseRoot(normalized, out var rootPitchClass, out var rootLength))
        {
            throw new FormatException($"Invalid chord symbol '{symbol}'.");
        }

        var remainder = normalized[rootLength..];
        var slashIndex = remainder.LastIndexOf('/');
        var slashBassPitchClass = 0;
        var hasSlashBass = slashIndex >= 0 &&
            TryParsePitchClass(remainder[(slashIndex + 1)..], out slashBassPitchClass);
        var quality = hasSlashBass ? remainder[..slashIndex] : remainder;
        var transposedRoot = PitchClassName(rootPitchClass + semitones, preferFlats);
        if (!hasSlashBass)
        {
            return transposedRoot + quality;
        }

        return transposedRoot + quality + "/" + PitchClassName(slashBassPitchClass + semitones, preferFlats);
    }

    private static TuneKeyInfo ParseKey(string key)
    {
        var normalized = NormalizeKeyName(key);
        if (!TryParseRoot(normalized, out var pitchClass, out var rootLength))
        {
            throw new FormatException($"Invalid target key '{key}'.");
        }

        var suffix = normalized[rootLength..];
        var isMinor = suffix.Equals("m", StringComparison.OrdinalIgnoreCase);
        if (suffix.Length > 0 && !isMinor)
        {
            throw new FormatException($"Invalid target key '{key}'.");
        }

        return new TuneKeyInfo(
            normalized,
            pitchClass,
            isMinor,
            normalized[..rootLength].Contains('b', StringComparison.Ordinal));
    }

    private static string NormalizeKeyName(string key)
    {
        var normalized = key.Trim()
            .Replace(" minor", "m", StringComparison.OrdinalIgnoreCase)
            .Replace(" major", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (normalized.EndsWith("-", StringComparison.Ordinal))
        {
            normalized = normalized[..^1] + "m";
        }

        if (normalized.Length > 0)
        {
            normalized = char.ToUpperInvariant(normalized[0]) + normalized[1..];
        }

        return normalized;
    }

    private static bool TryParseRoot(string value, out int pitchClass, out int rootLength)
    {
        rootLength = value.Length > 1 && value[1] is '#' or 'b' ? 2 : 1;
        if (value.Length < rootLength || !TryParsePitchClass(value[..rootLength], out pitchClass))
        {
            rootLength = 0;
            pitchClass = 0;
            return false;
        }

        return true;
    }

    private static bool TryParsePitchClass(string value, out int pitchClass)
    {
        pitchClass = value.Length > 0
            ? char.ToUpperInvariant(value[0]) switch
            {
                'C' => 0,
                'D' => 2,
                'E' => 4,
                'F' => 5,
                'G' => 7,
                'A' => 9,
                'B' => 11,
                _ => -1
            }
            : -1;
        if (pitchClass < 0 || value.Length > 2 || (value.Length == 2 && value[1] is not ('#' or 'b')))
        {
            pitchClass = 0;
            return false;
        }

        if (value.Length == 2)
        {
            pitchClass += value[1] == '#' ? 1 : -1;
        }

        pitchClass = Mod12(pitchClass);
        return true;
    }

    private static int SignedDistance(int source, int target)
    {
        var distance = Mod12(target - source);
        return distance > 6 ? distance - 12 : distance;
    }

    private static string PitchClassName(int pitchClass, bool preferFlats) =>
        (preferFlats
            ? new[] { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" }
            : new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" })[Mod12(pitchClass)];

    private static int Mod12(int value) => (value % 12 + 12) % 12;
}

public readonly record struct TuneKeyInfo(
    string Name,
    int PitchClass,
    bool IsMinor,
    bool PreferFlats);
