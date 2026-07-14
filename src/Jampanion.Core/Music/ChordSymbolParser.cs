using System.Text.RegularExpressions;

namespace Jampanion.Core.Music;

public static partial class ChordSymbolParser
{
    public static ChordSpec Parse(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        if (symbol.Trim() is "N.C." or "N.C" or "NC")
        {
            return ChordSpec.NoChord;
        }

        var normalized = Normalize(symbol);
        var slashIndex = normalized.LastIndexOf('/');
        string harmonySymbol;
        string? bassNote = null;
        var slashSuffix = slashIndex > 0 ? NormalizeNoteName(normalized[(slashIndex + 1)..]) : string.Empty;
        if (slashIndex > 0 && NoteNameRegex().IsMatch(slashSuffix))
        {
            harmonySymbol = normalized[..slashIndex];
            bassNote = slashSuffix;
        }
        else
        {
            harmonySymbol = normalized;
        }

        var match = ChordRegex().Match(harmonySymbol);
        if (!match.Success)
        {
            throw new FormatException($"Invalid chord symbol '{symbol}'.");
        }

        var root = NormalizeNoteName(match.Groups["root"].Value);
        var quality = match.Groups["quality"].Value;
        var chord = CreateHarmony(root, quality, normalized: bassNote is null ? normalized : harmonySymbol);
        return bassNote is null ? chord : ChordFactory.Slash(chord, bassNote, normalized);
    }

    private static ChordSpec CreateHarmony(string root, string quality, string normalized)
    {
        var compact = quality
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal)
            .Replace("Δ", "maj", StringComparison.Ordinal)
            .Replace("△", "maj", StringComparison.Ordinal)
            .Replace("−", "-", StringComparison.Ordinal)
            .Replace("°", "dim", StringComparison.Ordinal)
            .Replace("ø", "m7b5", StringComparison.Ordinal)
            .Trim();
        var lower = compact.ToLowerInvariant();

        if (lower.Length == 0 || lower is "maj" or "major")
        {
            return ChordFactory.Major(root, normalized);
        }

        if (lower is "maj7" or "ma7" or "major7" || compact is "M7")
        {
            return ChordFactory.Major7(root, normalized);
        }

        if (lower is "maj9" or "major9" || compact is "M9")
        {
            return ChordFactory.Major9(root, normalized);
        }

        if (lower is "maj11" or "major11" || compact is "M11")
        {
            return ChordFactory.Major11(root, normalized);
        }

        if (lower is "maj13" or "major13" || compact is "M13")
        {
            return ChordFactory.Major13(root, normalized);
        }

        if (lower is "6" or "maj6")
        {
            return ChordFactory.Major6(root, normalized);
        }


        if (lower is "6/9" or "69")
        {
            return ChordFactory.MajorSixNine(root, normalized);
        }

        if (lower is "m" or "min" or "minor" or "-")
        {
            return ChordFactory.Minor(root, normalized);
        }

        if (lower is "m6" or "min6" or "-6")
        {
            return ChordFactory.Minor6(root, normalized);
        }

        if (lower is "m7b5" or "min7b5" or "-7b5" or "halfdim" or "halfdim7")
        {
            return ChordFactory.Minor7Flat5(root, normalized);
        }

        if (lower is "m7" or "min7" or "minor7" or "-7")
        {
            return ChordFactory.Minor7(root, normalized);
        }


        if (lower is "m9" or "min9" or "minor9" or "-9")
        {
            return ChordFactory.Minor9(root, normalized);
        }

        if (lower is "m11" or "min11" or "minor11" or "-11")
        {
            return ChordFactory.Minor11(root, normalized);
        }

        if (lower is "m13" or "min13" or "minor13" or "-13")
        {
            return ChordFactory.Minor13(root, normalized);
        }

        if (lower is "dim" or "o")
        {
            return ChordFactory.Diminished(root, normalized);
        }

        if (lower is "dim7" or "o7")
        {
            return ChordFactory.Diminished7(root, normalized);
        }

        if (lower is "aug" or "+" or "+5")
        {
            return ChordFactory.Augmented(root, normalized);
        }

        if (lower is "sus" or "sus4" or "7sus" or "7sus4")
        {
            return ChordFactory.Suspended4(root, normalized);
        }

        if (lower is "sus2")
        {
            return ChordFactory.Suspended2(root, normalized);
        }

        if (lower.Contains("alt", StringComparison.Ordinal))
        {
            return ChordFactory.AlteredDominant(root, normalized);
        }

        if (DominantQualityRegex().IsMatch(lower))
        {
            return ChordFactory.Dominant(root, lower, normalized);
        }

        if (lower is "add9" or "2")
        {
            return ChordFactory.Add9(root, normalized);
        }

        if (lower == "5")
        {
            return ChordFactory.Major(root, normalized);
        }

        throw new FormatException($"Unsupported chord quality in '{normalized}'.");
    }

    private static string Normalize(string value) => value.Trim()
        .Replace("♯", "#", StringComparison.Ordinal)
        .Replace("♭", "b", StringComparison.Ordinal)
        .Replace("–", "-", StringComparison.Ordinal)
        .Replace("—", "-", StringComparison.Ordinal);

    private static string NormalizeNoteName(string note)
    {
        var normalized = Normalize(note);
        if (normalized.Length == 0)
        {
            return normalized;
        }

        return char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }

    [GeneratedRegex("^(?<root>[A-Ga-g](?:#|b)?)(?<quality>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChordRegex();

    [GeneratedRegex("^[A-G](?:#|b)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NoteNameRegex();

    [GeneratedRegex("^(?:7|9|11|13)(?:(?:b|#)(?:5|9|11|13))*$", RegexOptions.CultureInvariant)]
    private static partial Regex DominantQualityRegex();
}
