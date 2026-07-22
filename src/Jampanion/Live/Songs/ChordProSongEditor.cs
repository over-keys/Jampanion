using System.Text;
using Jampanion.Core.Music;

namespace Jampanion.Live.Songs;

internal static class ChordProSongEditor
{
    private enum GridKind
    {
        None,
        Main,
        Ending
    }

    public static TuneForm ReplaceChord(
        string path,
        int barIndex,
        int chordIndex,
        string chordSymbol,
        bool endingForm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(chordSymbol);

        var original = File.ReadAllText(path);
        var tune = ChordProSongParser.Parse(original, Path.GetFileNameWithoutExtension(path));
        var bars = GetBars(tune, endingForm);
        if (barIndex < 0 || barIndex >= bars.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(barIndex));
        }

        var targetBar = bars[barIndex];
        if (chordIndex < 0 || chordIndex >= targetBar.ChordChanges.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(chordIndex));
        }

        ChordChange[] changes;
        if (string.IsNullOrWhiteSpace(chordSymbol))
        {
            if (targetBar.ChordChanges.Count == 1)
            {
                throw new ArgumentException(
                    "A bar must contain at least one chord. The only chord in a bar cannot be deleted.",
                    nameof(chordSymbol));
            }

            changes = targetBar.ChordChanges
                .Where((_, index) => index != chordIndex)
                .Select((change, index) => chordIndex == 0 && index == 0
                    ? new ChordChange(0, change.Chord)
                    : change)
                .ToArray();
        }
        else
        {
            var normalizedSymbol = NormalizeChordSymbol(chordSymbol);
            var parsedChord = IsNoChord(normalizedSymbol)
                ? ChordSpec.NoChord
                : ChordSymbolParser.Parse(normalizedSymbol);
            changes = targetBar.ChordChanges
                .Select((change, index) => index == chordIndex
                    ? new ChordChange(change.StartBeat, parsedChord)
                    : change)
                .ToArray();
        }
        var replacementBar = new TuneBar(
            targetBar.Index,
            targetBar.Section,
            targetBar.BeatsPerBar,
            changes);

        var updated = ReplaceBarCell(original, tune, barIndex, replacementBar, endingForm);
        return WriteValidated(path, updated);
    }

    public static TuneForm InsertChord(
        string path,
        int barIndex,
        int startBeat,
        string chordSymbol,
        bool endingForm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(chordSymbol);

        var original = File.ReadAllText(path);
        var tune = ChordProSongParser.Parse(original, Path.GetFileNameWithoutExtension(path));
        var bars = GetBars(tune, endingForm);
        if (barIndex < 0 || barIndex >= bars.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(barIndex));
        }

        var targetBar = bars[barIndex];
        if (startBeat <= 0 || startBeat >= targetBar.BeatsPerBar)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startBeat),
                $"The insertion beat must be from 2 to {targetBar.BeatsPerBar}.");
        }

        if (targetBar.ChordChanges.Any(change => change.StartBeat == startBeat))
        {
            throw new ArgumentException(
                $"Beat {startBeat + 1} already contains a chord change.",
                nameof(startBeat));
        }

        var normalizedSymbol = NormalizeChordSymbol(chordSymbol);
        var parsedChord = IsNoChord(normalizedSymbol)
            ? ChordSpec.NoChord
            : ChordSymbolParser.Parse(normalizedSymbol);
        var changes = targetBar.ChordChanges
            .Append(new ChordChange(startBeat, parsedChord))
            .OrderBy(change => change.StartBeat)
            .ToArray();
        var replacementBar = new TuneBar(
            targetBar.Index,
            targetBar.Section,
            targetBar.BeatsPerBar,
            changes);

        var updated = ReplaceBarCell(original, tune, barIndex, replacementBar, endingForm);
        return WriteValidated(path, updated);
    }

    public static TuneForm SetRehearsalMark(
        string path,
        int barIndex,
        string rehearsalMark,
        bool endingForm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalizedMark = NormalizeRehearsalMark(rehearsalMark);
        var original = File.ReadAllText(path);
        var tune = ChordProSongParser.Parse(original, Path.GetFileNameWithoutExtension(path));
        var bars = GetBars(tune, endingForm);
        if (barIndex < 0 || barIndex >= bars.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(barIndex));
        }

        var updated = SetGridLinePrefix(original, barIndex, normalizedMark, endingForm);
        return WriteValidated(path, updated);
    }

    private static IReadOnlyList<TuneBar> GetBars(TuneForm tune, bool endingForm)
    {
        if (!endingForm)
        {
            return tune.Bars;
        }

        if (!tune.HasSeparateEndingForm)
        {
            throw new ArgumentException("This tune does not contain a separate ending grid.");
        }

        return tune.EndingFormBars;
    }

    private static string ReplaceBarCell(
        string original,
        TuneForm tune,
        int targetBarIndex,
        TuneBar replacementBar,
        bool endingForm)
    {
        var document = SplitDocument(original);
        var location = FindBarLocation(document.Lines, targetBarIndex, endingForm);
        var line = document.Lines[location.LineIndex];
        var firstSeparator = line.IndexOf('|');
        var prefix = line[..(firstSeparator + 1)];
        var cells = line[(firstSeparator + 1)..].Split('|').ToList();
        cells[location.CellIndex] = PreservePadding(
            cells[location.CellIndex],
            SerializeBar(replacementBar));
        document.Lines[location.LineIndex] = prefix + string.Join("|", cells);
        return JoinDocument(document);
    }

    private static string SetGridLinePrefix(
        string original,
        int targetBarIndex,
        string rehearsalMark,
        bool endingForm)
    {
        var document = SplitDocument(original);
        var location = FindBarLocation(document.Lines, targetBarIndex, endingForm);
        var line = document.Lines[location.LineIndex];
        var firstSeparator = line.IndexOf('|');
        var cells = line[(firstSeparator + 1)..].Split('|').ToList();
        var firstNonEmptyCell = cells.FindIndex(cell => !string.IsNullOrWhiteSpace(cell));
        if (firstNonEmptyCell < 0)
        {
            throw new InvalidOperationException("The selected grid line contains no chord bars.");
        }

        var markPrefix = rehearsalMark.Length == 0 ? "  " : rehearsalMark + " ";
        if (location.CellIndex == firstNonEmptyCell)
        {
            document.Lines[location.LineIndex] = markPrefix + line[firstSeparator..];
            return JoinDocument(document);
        }

        var beforeCells = cells.Take(location.CellIndex).ToList();
        var afterCells = cells.Skip(location.CellIndex).ToList();
        var originalPrefix = line[..firstSeparator];
        var firstLine = originalPrefix + "|" + string.Join("|", beforeCells);
        if (!firstLine.EndsWith('|'))
        {
            firstLine += "|";
        }

        var secondLine = markPrefix + "|" + string.Join("|", afterCells);
        document.Lines[location.LineIndex] = firstLine;
        document.Lines.Insert(location.LineIndex + 1, secondLine);
        return JoinDocument(document);
    }

    private static GridBarLocation FindBarLocation(
        IReadOnlyList<string> lines,
        int targetBarIndex,
        bool endingForm)
    {
        var desiredKind = endingForm ? GridKind.Ending : GridKind.Main;
        var hasGridMarkers = lines.Any(line =>
            IsDirective(line, "start_of_grid") ||
            IsDirective(line, "sog") ||
            IsDirective(line, "start_of_ending_grid") ||
            IsDirective(line, "x-ai-jam-ending-grid"));
        var currentKind = hasGridMarkers ? GridKind.None : GridKind.Main;
        var barIndex = 0;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (IsDirective(line, "start_of_grid") || IsDirective(line, "sog"))
            {
                currentKind = GridKind.Main;
                continue;
            }

            if (IsDirective(line, "end_of_grid") || IsDirective(line, "eog"))
            {
                currentKind = GridKind.None;
                continue;
            }

            if (IsDirective(line, "start_of_ending_grid") || IsDirective(line, "x-ai-jam-ending-grid"))
            {
                currentKind = GridKind.Ending;
                if (desiredKind == GridKind.Ending)
                {
                    barIndex = 0;
                }
                continue;
            }

            if (IsDirective(line, "end_of_ending_grid"))
            {
                currentKind = GridKind.None;
                continue;
            }

            if (currentKind != desiredKind || !line.Contains('|'))
            {
                continue;
            }

            var firstSeparator = line.IndexOf('|');
            var cells = line[(firstSeparator + 1)..].Split('|');
            for (var cellIndex = 0; cellIndex < cells.Length; cellIndex++)
            {
                if (string.IsNullOrWhiteSpace(cells[cellIndex]))
                {
                    continue;
                }

                if (barIndex == targetBarIndex)
                {
                    return new GridBarLocation(lineIndex, cellIndex);
                }

                barIndex++;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(targetBarIndex),
            "The selected bar could not be located in the ChordPro grid.");
    }

    private static string SerializeBar(TuneBar bar)
    {
        var changes = bar.ChordChanges;
        if (changes.Count == 1 && changes[0].StartBeat == 0)
        {
            return StorageSymbol(changes[0].Chord);
        }

        var baseLength = bar.BeatsPerBar / changes.Count;
        var remainder = bar.BeatsPerBar % changes.Count;
        var expectedBeat = 0;
        var evenlyDistributed = true;
        for (var index = 0; index < changes.Count; index++)
        {
            if (changes[index].StartBeat != expectedBeat)
            {
                evenlyDistributed = false;
                break;
            }

            expectedBeat += baseLength + (index < remainder ? 1 : 0);
        }

        if (evenlyDistributed)
        {
            return string.Join(" ", changes.Select(change => StorageSymbol(change.Chord)));
        }

        var beats = Enumerable.Repeat(".", bar.BeatsPerBar).ToArray();
        foreach (var change in changes)
        {
            beats[change.StartBeat] = StorageSymbol(change.Chord);
        }

        return string.Join(" ", beats);
    }

    private static string StorageSymbol(ChordSpec chord) => chord.IsNoChord ? "N.C." : chord.Symbol;

    private static string NormalizeChordSymbol(string value)
    {
        var normalized = value.Trim()
            .Replace('♯', '#')
            .Replace('♭', 'b');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Enter a chord symbol.", nameof(value));
        }

        return IsNoChord(normalized) ? "N.C." : normalized;
    }

    private static bool IsNoChord(string value) => value is "N.C." or "N.C" or "NC";

    private static string NormalizeRehearsalMark(string value)
    {
        var normalized = value.Trim().Trim('[', ']', ' ');
        if (normalized.Length == 0)
        {
            // Empty text means remove the rehearsal mark from this grid line.
            return string.Empty;
        }

        if (normalized.IndexOfAny(['|', '{', '}', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("A rehearsal mark cannot contain |, braces, or a line break.", nameof(value));
        }

        return normalized;
    }

    private static string PreservePadding(string originalCell, string value)
    {
        var leading = originalCell.Length - originalCell.TrimStart().Length;
        var trailing = originalCell.Length - originalCell.TrimEnd().Length;
        var left = leading == 0 ? " " : originalCell[..leading];
        var right = trailing == 0 ? " " : originalCell[(originalCell.Length - trailing)..];
        return left + value + right;
    }

    private static bool IsDirective(string line, string directive)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
        {
            return false;
        }

        var inner = trimmed[1..^1];
        var separator = inner.IndexOf(':');
        var name = (separator >= 0 ? inner[..separator] : inner).Trim();
        return name.Equals(directive, StringComparison.OrdinalIgnoreCase);
    }

    private static TextDocument SplitDocument(string text)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var trailingNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (trailingNewline && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return new TextDocument(lines, newline, trailingNewline);
    }

    private static string JoinDocument(TextDocument document)
    {
        var text = string.Join(document.Newline, document.Lines);
        return document.TrailingNewline ? text + document.Newline : text;
    }

    private static TuneForm WriteValidated(string path, string content)
    {
        var parsed = ChordProSongParser.Parse(content, Path.GetFileNameWithoutExtension(path));
        var temporaryPath = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // A stale temporary file is harmless and is never scanned as a song.
            }
        }

        return parsed;
    }

    private sealed record TextDocument(List<string> Lines, string Newline, bool TrailingNewline);
    private readonly record struct GridBarLocation(int LineIndex, int CellIndex);
}
