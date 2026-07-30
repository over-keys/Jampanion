using System.Text.RegularExpressions;

namespace Jampanion.Web.Services;

public enum AccidentalPreference
{
    Auto,
    Flats,
    Sharps
}

public static partial class ChordSymbolTransposer
{
    private static readonly string[] SharpNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly string[] FlatNames = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    public static IReadOnlyList<string> KeyNames { get; } = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    public static int PitchClass(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return -1;
        }
        var normalized = note.Trim().Replace("♯", "#", StringComparison.Ordinal).Replace("♭", "b", StringComparison.Ordinal);
        var match = NoteRegex().Match(normalized);
        if (!match.Success)
        {
            return -1;
        }
        var natural = match.Groups[1].Value.ToUpperInvariant() switch
        {
            "C" => 0,
            "D" => 2,
            "E" => 4,
            "F" => 5,
            "G" => 7,
            "A" => 9,
            "B" => 11,
            _ => -1
        };
        var accidental = match.Groups[2].Value;
        return (natural + (accidental == "#" ? 1 : accidental == "b" ? -1 : 0) + 12) % 12;
    }

    public static string TransposeChord(string symbol, int semitones, AccidentalPreference preference)
    {
        var trimmed = symbol.Trim();
        if (trimmed.Length == 0 || trimmed is "." or "/" or "%" || trimmed.StartsWith("N.C", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var match = ChordRegex().Match(trimmed.Replace("♯", "#", StringComparison.Ordinal).Replace("♭", "b", StringComparison.Ordinal));
        if (!match.Success)
        {
            return symbol;
        }

        var root = TransposeNote(match.Groups[1].Value + match.Groups[2].Value, semitones, preference);
        var suffix = match.Groups[3].Value;
        var bass = match.Groups[4].Success
            ? "/" + TransposeNote(match.Groups[4].Value + match.Groups[5].Value, semitones, preference)
            : string.Empty;
        return root + suffix + bass;
    }

    public static string RespellingChord(string symbol, AccidentalPreference preference) =>
        TransposeChord(symbol, 0, preference);

    public static string TransposeKey(string key, int semitones, AccidentalPreference preference)
    {
        var normalized = key.Trim()
            .Replace("♯", "#", StringComparison.Ordinal)
            .Replace("♭", "b", StringComparison.Ordinal);
        var match = NoteRegex().Match(normalized);
        var pitchClass = PitchClass(normalized);
        if (pitchClass < 0 || !match.Success)
        {
            return key;
        }

        var suffix = normalized[match.Length..];
        return NameFor((pitchClass + semitones + 120) % 12, preference, normalized) + suffix;
    }

    private static string TransposeNote(string note, int semitones, AccidentalPreference preference)
    {
        var pitchClass = PitchClass(note);
        return pitchClass < 0 ? note : NameFor((pitchClass + semitones + 120) % 12, preference, note);
    }

    private static string NameFor(int pitchClass, AccidentalPreference preference, string context)
    {
        var useFlats = preference switch
        {
            AccidentalPreference.Flats => true,
            AccidentalPreference.Sharps => false,
            _ => context.Contains('b') || !context.Contains('#') && pitchClass is 1 or 3 or 8 or 10
        };
        return (useFlats ? FlatNames : SharpNames)[pitchClass];
    }

    [GeneratedRegex("^([A-Ga-g])([#b]?)(.*?)(?:/([A-Ga-g])([#b]?))?$")]
    private static partial Regex ChordRegex();

    [GeneratedRegex("^([A-Ga-g])([#b]?)")]
    private static partial Regex NoteRegex();
}
