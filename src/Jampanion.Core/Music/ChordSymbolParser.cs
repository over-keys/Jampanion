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

        var normalized = Normalize(symbol)
            .Replace("∆", "^", StringComparison.Ordinal)
            .Replace("ø", "h", StringComparison.OrdinalIgnoreCase);
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
            .Replace("∆", "^", StringComparison.Ordinal)
            .Replace("ø", "h", StringComparison.OrdinalIgnoreCase)
            .Replace("Δ", "maj", StringComparison.Ordinal)
            .Replace("△", "maj", StringComparison.Ordinal)
            .Replace("−", "-", StringComparison.Ordinal)
            .Replace("°", "dim", StringComparison.Ordinal)
            .Replace("ø", "m7b5", StringComparison.Ordinal)
            .Trim();
        var lower = compact.ToLowerInvariant();

        // iReal commonly writes a major seventh as an uppercase M (for
        // example GbM7(#11)).  Do this check before lower-casing can make M7
        // look like the minor prefix m7.  Parentheses have already been
        // removed above, so both M7(#11) and M7#11 arrive here identically.
        if (IsMajorSevenQuality(compact, out var majorSevenQuality))
        {
            return majorSevenQuality.Length == 0
                ? ChordFactory.Major7(root, normalized)
                : ChordFactory.Major7Altered(root, majorSevenQuality, normalized);
        }

        if (IsMajorExtensionQuality(compact, "13", out _))
        {
            return ChordFactory.Major13Altered(root, compact, normalized);
        }

        if (IsMajorExtensionQuality(compact, "9", out _))
        {
            return ChordFactory.Major7Altered(root, compact, normalized);
        }

        if (lower is "majadd4" or "majoradd4")
        {
            return ChordFactory.MajorAdd4(root, normalized);
        }

        if (lower is "^+" or "^#5" or "maj+" or "maj#5")
        {
            return ChordFactory.Major7Altered(root, "maj7#5", normalized);
        }

        if (lower is "^b5" or "majb5")
        {
            return ChordFactory.Major7Altered(root, "maj7b5", normalized);
        }

        if (lower is "^#11" or "maj#11")
        {
            return ChordFactory.Major7Altered(root, "maj7#11", normalized);
        }

        if (lower is "min^11" or "m^11" or "mmaj11" or "mmin11")
        {
            return ChordFactory.MinorMajor11(root, normalized);
        }

        if (lower is "min^13" or "m^13" or "mmaj13" or "mmin13")
        {
            return ChordFactory.MinorMajor13(root, normalized);
        }

        if (IsMinorMajorQuality(compact, out var minorMajorQuality))
        {
            return minorMajorQuality.StartsWith("9", StringComparison.Ordinal)
                ? ChordFactory.MinorMajor9(root, normalized)
                : ChordFactory.MinorMajor7(root, normalized);
        }

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

        if (lower is "m69" or "min69" or "-69")
        {
            return ChordFactory.MinorSixNine(root, normalized);
        }

        if (lower is "mb6" or "minb6" or "-b6")
        {
            return ChordFactory.MinorFlatSix(root, normalized);
        }

        if (lower is "m#5" or "min#5" or "-#5")
        {
            return ChordFactory.MinorSharpFive(root, normalized);
        }

        if (lower is "m7b5" or "min7b5" or "-7b5" or "halfdim" or "halfdim7" or "h" or "h7")
        {
            return ChordFactory.Minor7Flat5(root, normalized);
        }

        if (lower is "h9" or "m7b5(9)" or "m7b5add9")
        {
            return ChordFactory.HalfDiminished9(root, normalized);
        }

        if (lower is "m7b6" or "min7b6")
        {
            return ChordFactory.Minor7FlatSix(root, normalized);
        }

        if (lower is "m9b6" or "min9b6")
        {
            return ChordFactory.Minor9FlatSix(root, normalized);
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

        if (lower is "sus2" or "2")
        {
            return ChordFactory.Suspended2(root, normalized);
        }

        if (lower.Contains("sus", StringComparison.Ordinal))
        {
            return ChordFactory.SuspendedAltered(root, lower, normalized);
        }

        if (lower.Contains("alt", StringComparison.Ordinal))
        {
            return ChordFactory.AlteredDominant(root, normalized);
        }

        if (DominantQualityRegex().IsMatch(lower))
        {
            return ChordFactory.Dominant(root, lower, normalized);
        }

        if (lower is "add9" or "add2")
        {
            return ChordFactory.Add9(root, normalized);
        }

        if (lower is "minadd4" or "madd4")
        {
            return ChordFactory.MinorAdd4(root, normalized);
        }

        if (lower is "-add9" or "-add2" or "madd9" or "madd2")
        {
            return ChordFactory.MinorAdd9(root, normalized);
        }

        if (lower is "7add13")
        {
            return ChordFactory.Dominant(root, "13", normalized);
        }

        if (lower is "7+" or "9+")
        {
            return ChordFactory.Dominant(root, lower[..1] + "#5", normalized);
        }

        if (lower == "5")
        {
            return ChordFactory.Power(root, normalized);
        }

        throw new FormatException($"Unsupported chord quality in '{normalized}'.");
    }

    private static bool IsMajorSevenQuality(string compact, out string suffix)
    {
        if (compact == "^")
        {
            suffix = string.Empty;
            return true;
        }

        if (compact.StartsWith("M7", StringComparison.Ordinal))
        {
            suffix = compact[2..];
            return true;
        }

        foreach (var prefix in new[] { "maj7", "major7", "ma7", "^7" })
        {
            if (compact.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                suffix = compact[prefix.Length..];
                return true;
            }
        }

        suffix = string.Empty;
        return false;
    }

    private static bool IsMajorExtensionQuality(string compact, string extension, out string suffix)
    {
        foreach (var prefix in new[] { $"M{extension}", $"maj{extension}", $"major{extension}", $"ma{extension}", $"^{extension}" })
        {
            var comparison = prefix.StartsWith("M", StringComparison.Ordinal) && !prefix.StartsWith("maj", StringComparison.Ordinal)
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (compact.StartsWith(prefix, comparison))
            {
                suffix = compact[prefix.Length..];
                return suffix.Length > 0;
            }
        }

        suffix = string.Empty;
        return false;
    }

    private static bool IsMinorMajorQuality(string compact, out string extension)
    {
        foreach (var prefix in new[]
        {
            "mM7", "mM9", "mMaj7", "mMaj9", "mmaj7", "mmaj9",
            "m^7", "m^9", "min^7", "min^9", "-^7", "-^9",
            "m^", "min^", "-^"
        })
        {
            var comparison = prefix.Contains('M') && !prefix.Contains("Maj", StringComparison.Ordinal)
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (compact.StartsWith(prefix, comparison))
            {
                extension = prefix.EndsWith("9", StringComparison.Ordinal) ? "9" : "7";
                return true;
            }
        }

        extension = string.Empty;
        return false;
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
