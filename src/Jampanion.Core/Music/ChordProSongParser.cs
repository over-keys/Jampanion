using System.Text.RegularExpressions;

namespace Jampanion.Core.Music;

public static partial class ChordProSongParser
{
    public static TuneForm ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
    }

    public static TuneForm Parse(string text, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var hasGridMarkers = lines.Any(line =>
            IsDirective(line, "start_of_grid") ||
            IsDirective(line, "sog") ||
            IsDirective(line, "start_of_ending_grid") ||
            IsDirective(line, "x-ai-jam-ending-grid"));
        var insideGrid = !hasGridMarkers;
        var readingEndingGrid = false;
        var title = string.Empty;
        var key = string.Empty;
        var style = string.Empty;
        var time = "4/4";
        var beatsPerBar = SessionConstants.BeatsPerBar;
        var tempo = 140;
        var id = string.Empty;
        int? declaredCodaStartIndex = null;
        var currentSection = string.Empty;
        var bars = new List<TuneBar>();
        var endingFormBars = new List<TuneBar>();
        var chordCache = new Dictionary<string, ChordSpec>(StringComparer.OrdinalIgnoreCase);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var lineNumber = lineIndex + 1;
            var line = lines[lineIndex].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var directive = DirectiveRegex().Match(line);
            if (directive.Success)
            {
                var name = directive.Groups["name"].Value.Trim().ToLowerInvariant();
                var value = directive.Groups["value"].Value.Trim();
                switch (name)
                {
                    case "start_of_grid":
                    case "sog":
                        insideGrid = true;
                        readingEndingGrid = false;
                        currentSection = string.Empty;
                        break;
                    case "end_of_grid":
                    case "eog":
                        insideGrid = false;
                        break;
                    case "start_of_ending_grid":
                        insideGrid = true;
                        readingEndingGrid = true;
                        currentSection = string.Empty;
                        break;
                    case "x-ai-jam-ending-grid":
                        insideGrid = true;
                        readingEndingGrid = true;
                        currentSection = string.Empty;
                        break;
                    case "end_of_ending_grid":
                        insideGrid = false;
                        readingEndingGrid = false;
                        break;
                    case "title":
                    case "t":
                        title = value;
                        break;
                    case "key":
                        key = value;
                        break;
                    case "style":
                    case "x-ai-jam-style":
                        style = value;
                        break;
                    case "time":
                    case "time_signature":
                        time = value;
                        beatsPerBar = ParseBeatsPerBar(value, lineNumber);
                        break;
                    case "tempo":
                        if (!int.TryParse(value, out tempo) || tempo is < 40 or > 300)
                        {
                            throw new ChordProSongParseException("Tempo must be an integer from 40 to 300 BPM.", lineNumber);
                        }
                        break;
                    case "x-ai-jam-id":
                        id = value;
                        break;
                    case "x-ai-jam-coda-start":
                        if (!int.TryParse(value, out var parsedCodaStartIndex) || parsedCodaStartIndex < 0)
                        {
                            throw new ChordProSongParseException(
                                "x-ai-jam-coda-start must be a non-negative bar index.",
                                lineNumber);
                        }

                        declaredCodaStartIndex = parsedCodaStartIndex;
                        break;
                }

                continue;
            }

            if (!insideGrid)
            {
                continue;
            }

            if (!line.Contains('|'))
            {
                throw new ChordProSongParseException("Expected a chord-grid line containing bar separators (|).", lineNumber);
            }

            var targetBars = readingEndingGrid ? endingFormBars : bars;
            ParseGridLine(line, lineNumber, targetBars, chordCache, key, beatsPerBar, ref currentSection);
        }

        beatsPerBar = ParseBeatsPerBar(time, lineNumber: null);

        if (bars.Count == 0)
        {
            throw new ChordProSongParseException("No chord bars were found.");
        }

        if (bars.Count < SessionConstants.BarsPerSegment)
        {
            throw new ChordProSongParseException(
                $"The form contains {bars.Count} bars. The current accompaniment engine requires at least 4 bars.");
        }

        if (endingFormBars.Count > 0 && endingFormBars.Count < TuneForm.EndingPlanBarCount)
        {
            throw new ChordProSongParseException(
                $"The ending form contains {endingFormBars.Count} bars. At least {TuneForm.EndingPlanBarCount} bars are required.");
        }

        title = string.IsNullOrWhiteSpace(title)
            ? string.IsNullOrWhiteSpace(sourceName) ? "Untitled" : sourceName.Trim()
            : title.Trim();
        id = string.IsNullOrWhiteSpace(id) ? CreateId(title) : id.Trim();
        var inferredCodaStartIndex = endingFormBars
            .Select((bar, index) => (bar, index))
            .Where(item => string.Equals(item.bar.Section, "Ending", StringComparison.OrdinalIgnoreCase) &&
                          (item.index == 0 || !string.Equals(
                              endingFormBars[item.index - 1].Section,
                              "Ending",
                              StringComparison.OrdinalIgnoreCase)))
            .Select(item => (int?)item.index)
            .LastOrDefault();
        var codaStartIndex = declaredCodaStartIndex ?? inferredCodaStartIndex;

        try
        {
            return new TuneForm(
                id,
                title,
                key,
                bars,
                tempo,
                endingFormBars.Count == 0 ? null : endingFormBars,
                style,
                time,
                codaStartIndex >= 0 ? codaStartIndex : null);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            throw new ChordProSongParseException(ex.Message, innerException: ex);
        }
    }

    private static int ParseBeatsPerBar(string time, int? lineNumber)
    {
        return time.Trim() switch
        {
            "3/4" => 3,
            "4/4" => 4,
            _ => throw new ChordProSongParseException(
                $"Time signature {time} is not supported. Jampanion supports 3/4 Jazz Waltz and 4/4 Swing, Bossa Nova, or Latin / Mambo.",
                lineNumber ?? 0)
        };
    }

    private static void ParseGridLine(
        string line,
        int lineNumber,
        List<TuneBar> bars,
        Dictionary<string, ChordSpec> chordCache,
        string key,
        int beatsPerBar,
        ref string currentSection)
    {
        var firstSeparator = line.IndexOf('|');
        var section = line[..firstSeparator].Trim();
        if (section.Length > 0)
        {
            currentSection = section.Trim('[', ']', ' ');
        }

        var cells = line[(firstSeparator + 1)..].Split('|');
        foreach (var rawCell in cells)
        {
            var cell = rawCell.Trim();
            if (cell.Length == 0)
            {
                continue;
            }

            var barNumber = bars.Count + 1;
            bars.Add(ParseBar(cell, lineNumber, barNumber, currentSection, bars, chordCache, key, beatsPerBar));
        }
    }

    private static TuneBar ParseBar(
        string cell,
        int lineNumber,
        int barNumber,
        string section,
        IReadOnlyList<TuneBar> previousBars,
        Dictionary<string, ChordSpec> chordCache,
        string key,
        int beatsPerBar)
    {
        var tokens = cell.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 1 && tokens[0] == "%")
        {
            if (previousBars.Count == 0)
            {
                throw new ChordProSongParseException("The first bar cannot repeat a previous bar.", lineNumber, barNumber);
            }

            var previous = previousBars[^1];
            return new TuneBar(
                barNumber - 1,
                section,
                beatsPerBar,
                previous.ChordChanges.Select(change => new ChordChange(change.StartBeat, change.Chord)).ToArray());
        }

        if (tokens.Any(token => token == "%"))
        {
            throw new ChordProSongParseException("% must be the only token in a repeated bar.", lineNumber, barNumber);
        }

        var explicitCells = tokens.Any(token => token is "." or "/");
        if (explicitCells)
        {
            if (tokens.Length != beatsPerBar)
            {
                throw new ChordProSongParseException(
                    $"Explicit beat notation must contain exactly {beatsPerBar} cells in {beatsPerBar}/4.",
                    lineNumber,
                    barNumber);
            }

            var changes = new List<ChordChange>();
            for (var beat = 0; beat < tokens.Length; beat++)
            {
                var token = tokens[beat];
                if (token is "." or "/")
                {
                    if (beat == 0)
                    {
                        throw new ChordProSongParseException("A bar must begin with a chord symbol.", lineNumber, barNumber);
                    }
                    continue;
                }

                changes.Add(new ChordChange(beat, ParseChord(token, lineNumber, barNumber, chordCache, key)));
            }

            return new TuneBar(barNumber - 1, section, beatsPerBar, changes);
        }

        if (tokens.Length < 1 || tokens.Length > beatsPerBar)
        {
            throw new ChordProSongParseException(
                $"A {beatsPerBar}/4 bar must contain between one and {beatsPerBar} chord symbols.",
                lineNumber,
                barNumber);
        }

        var baseLength = beatsPerBar / tokens.Length;
        var remainder = beatsPerBar % tokens.Length;
        var startBeat = 0;
        var evenlyDistributed = new List<ChordChange>(tokens.Length);
        for (var index = 0; index < tokens.Length; index++)
        {
            evenlyDistributed.Add(new ChordChange(startBeat, ParseChord(tokens[index], lineNumber, barNumber, chordCache, key)));
            startBeat += baseLength + (index < remainder ? 1 : 0);
        }

        return new TuneBar(barNumber - 1, section, beatsPerBar, evenlyDistributed);
    }

    private static ChordSpec ParseChord(
        string token,
        int lineNumber,
        int barNumber,
        Dictionary<string, ChordSpec> chordCache,
        string key)
    {
        if (chordCache.TryGetValue(token, out var cached))
        {
            return cached;
        }

        if (token.Trim() is "N.C." or "N.C" or "NC")
        {
            chordCache[token] = ChordSpec.NoChord;
            return ChordSpec.NoChord;
        }

        try
        {
            var chord = ChordSymbolParser.Parse(token);
            chord = ApplyTuneContext(token, chord, key);
            chordCache[token] = chord;
            return chord;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ChordProSongParseException($"'{token}' is not a supported chord symbol. {ex.Message}", lineNumber, barNumber, ex);
        }
    }


    private static ChordSpec ApplyTuneContext(string token, ChordSpec chord, string key)
    {
        var chordMatch = PlainDominantRegex().Match(token.Trim()
            .Replace("♯", "#", StringComparison.Ordinal)
            .Replace("♭", "b", StringComparison.Ordinal));
        var keyMatch = MinorKeyRegex().Match(key.Trim()
            .Replace("♯", "#", StringComparison.Ordinal)
            .Replace("♭", "b", StringComparison.Ordinal));
        if (!chordMatch.Success || !keyMatch.Success)
        {
            return chord;
        }

        var keyRoot = ChordSymbolParser.Parse(keyMatch.Groups["root"].Value).RootPitchClass;
        if (chord.RootPitchClass != (keyRoot + 7) % 12)
        {
            return chord;
        }

        return ChordFactory.MinorKeyDominant(chordMatch.Groups["root"].Value, chord.Symbol);
    }

    private static bool IsDirective(string line, string directive)
    {
        var match = DirectiveRegex().Match(line.Trim());
        return match.Success && string.Equals(match.Groups["name"].Value.Trim(), directive, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateId(string title)
    {
        var words = Regex.Matches(title.ToLowerInvariant(), "[a-z0-9]+", RegexOptions.CultureInvariant)
            .Select(match => match.Value);
        var id = string.Join('-', words);
        return id.Length == 0 ? "untitled" : id;
    }

    [GeneratedRegex("^(?<root>[A-Ga-g](?:#|b)?)7$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PlainDominantRegex();

    [GeneratedRegex("^(?<root>[A-Ga-g](?:#|b)?)(?:m|min|minor)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MinorKeyRegex();

    [GeneratedRegex("^\\{(?<name>[^}:\\s]+)(?:\\s+[^}:]*)?(?::(?<value>.*))?\\}$", RegexOptions.CultureInvariant)]
    private static partial Regex DirectiveRegex();

}
