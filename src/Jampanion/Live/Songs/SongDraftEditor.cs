using System.Security.Cryptography;
using System.Text;
using Jampanion.Core.Music;

namespace Jampanion.Live.Songs;

internal static class SongDraftEditor
{
    private enum GridKind
    {
        None,
        Main,
        Ending
    }

    public static string GetFileFingerprint(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return GetContentFingerprint(File.ReadAllText(path));
    }

    public static string GetContentFingerprint(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return ComputeFingerprint(content);
    }

    public static TuneForm ReplaceChord(
        TuneForm tune,
        int barIndex,
        int chordIndex,
        string chordSymbol,
        bool endingForm)
    {
        ArgumentNullException.ThrowIfNull(tune);
        ArgumentNullException.ThrowIfNull(chordSymbol);

        var bars = GetBars(tune, endingForm);
        ValidateBarIndex(bars, barIndex);
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
            var parsedChord = ParseChord(chordSymbol);
            changes = targetBar.ChordChanges
                .Select((change, index) => index == chordIndex
                    ? new ChordChange(change.StartBeat, parsedChord)
                    : change)
                .ToArray();
        }

        return ReplaceBar(
            tune,
            barIndex,
            new TuneBar(targetBar.Index, targetBar.Section, targetBar.BeatsPerBar, changes),
            endingForm);
    }

    public static TuneForm InsertChord(
        TuneForm tune,
        int barIndex,
        int startBeat,
        string chordSymbol,
        bool endingForm)
    {
        ArgumentNullException.ThrowIfNull(tune);
        ArgumentException.ThrowIfNullOrWhiteSpace(chordSymbol);

        var bars = GetBars(tune, endingForm);
        ValidateBarIndex(bars, barIndex);
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

        var changes = targetBar.ChordChanges
            .Append(new ChordChange(startBeat, ParseChord(chordSymbol)))
            .OrderBy(change => change.StartBeat)
            .ToArray();

        return ReplaceBar(
            tune,
            barIndex,
            new TuneBar(targetBar.Index, targetBar.Section, targetBar.BeatsPerBar, changes),
            endingForm);
    }

    public static TuneForm SetRehearsalMark(
        TuneForm tune,
        int barIndex,
        string rehearsalMark,
        bool endingForm)
    {
        ArgumentNullException.ThrowIfNull(tune);
        var normalizedMark = NormalizeRehearsalMark(rehearsalMark);
        var sourceBars = GetBars(tune, endingForm);
        ValidateBarIndex(sourceBars, barIndex);

        var updatedBars = sourceBars.ToArray();
        var existingSection = updatedBars[barIndex].Section;
        var segmentEnd = barIndex + 1;
        while (segmentEnd < updatedBars.Length &&
               string.Equals(
                   updatedBars[segmentEnd].Section,
                   existingSection,
                   StringComparison.Ordinal))
        {
            segmentEnd++;
        }

        var replacementSection = normalizedMark.Length > 0
            ? normalizedMark
            : barIndex > 0 ? updatedBars[barIndex - 1].Section : string.Empty;

        for (var index = barIndex; index < segmentEnd; index++)
        {
            var bar = updatedBars[index];
            updatedBars[index] = new TuneBar(
                bar.Index,
                replacementSection,
                bar.BeatsPerBar,
                bar.ChordChanges);
        }

        return ReplaceBars(tune, updatedBars, endingForm);
    }

    public static TuneForm ApplySongSettings(
        TuneForm tune,
        int tempoBpm,
        AccompanimentStyle style,
        string targetKey,
        bool? preferFlats)
    {
        ArgumentNullException.ThrowIfNull(tune);
        if (tempoBpm is < 40 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(tempoBpm));
        }

        var transposed = preferFlats is null
            ? TuneTransposer.TransposeAuto(tune, targetKey)
            : TuneTransposer.Transpose(tune, targetKey, preferFlats);

        return new TuneForm(
            transposed.Id,
            transposed.Title,
            transposed.Key,
            transposed.Bars,
            tempoBpm,
            transposed.HasSeparateEndingForm ? transposed.EndingFormBars : null,
            AccompanimentStyleNames.StorageName(style),
            transposed.TimeSignature,
            transposed.CodaStartIndex,
            transposed.SectionStyles);
    }

    public static TuneForm SaveChart(
        string path,
        TuneForm tune,
        string expectedFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(tune);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);

        var original = File.ReadAllText(path);
        ValidateExpectedFingerprint(original, expectedFingerprint);
        var persisted = ChordProSongParser.Parse(
            original,
            Path.GetFileNameWithoutExtension(path));
        ValidateCompatibleForms(persisted, tune);

        var updated = ApplySectionMarkers(
            original,
            persisted.Bars,
            tune.Bars,
            endingForm: false);
        if (tune.HasSeparateEndingForm)
        {
            updated = ApplySectionMarkers(
                updated,
                persisted.EndingFormBars,
                tune.EndingFormBars,
                endingForm: true);
        }

        updated = ApplyChordCells(updated, tune.Bars, endingForm: false);
        if (tune.HasSeparateEndingForm)
        {
            updated = ApplyChordCells(
                updated,
                tune.EndingFormBars,
                endingForm: true);
        }

        updated = ReplaceSectionStyleDirectives(updated, tune.SectionStyles);
        return WriteValidated(path, updated, expectedFingerprint);
    }

    public static TuneForm SaveSongSettings(
        string path,
        int tempoBpm,
        AccompanimentStyle style,
        string targetKey,
        bool? preferFlats,
        string expectedFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);

        var original = File.ReadAllText(path);
        ValidateExpectedFingerprint(original, expectedFingerprint);
        var persisted = ChordProSongParser.Parse(
            original,
            Path.GetFileNameWithoutExtension(path));
        var updatedTune = ApplySongSettings(
            persisted,
            tempoBpm,
            style,
            targetKey,
            preferFlats);

        var updated = SetDirective(
            original,
            ["key"],
            "key",
            updatedTune.Key);
        updated = SetDirective(
            updated,
            ["style", "x-ai-jam-style"],
            "style",
            AccompanimentStyleNames.StorageName(style));
        updated = SetDirective(
            updated,
            ["tempo"],
            "tempo",
            tempoBpm.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        // Only the persisted chart is transposed here. Unsaved chart edits
        // remain in the in-memory TuneForm until CHORD SHEET Save.
        updated = ApplyChordCells(
            updated,
            updatedTune.Bars,
            endingForm: false);
        if (updatedTune.HasSeparateEndingForm)
        {
            updated = ApplyChordCells(
                updated,
                updatedTune.EndingFormBars,
                endingForm: true);
        }

        return WriteValidated(path, updated, expectedFingerprint);
    }

    private static TuneForm ReplaceBar(
        TuneForm tune,
        int barIndex,
        TuneBar replacement,
        bool endingForm)
    {
        var bars = GetBars(tune, endingForm).ToArray();
        bars[barIndex] = replacement;
        return ReplaceBars(tune, bars, endingForm);
    }

    private static TuneForm ReplaceBars(
        TuneForm tune,
        IReadOnlyList<TuneBar> replacementBars,
        bool endingForm)
    {
        var mainBars = endingForm ? tune.Bars : replacementBars;
        var endingBars = tune.HasSeparateEndingForm
            ? endingForm ? replacementBars : tune.EndingFormBars
            : null;

        return new TuneForm(
            tune.Id,
            tune.Title,
            tune.Key,
            mainBars,
            tune.DefaultTempoBpm,
            endingBars,
            tune.OriginalStyle,
            tune.TimeSignature,
            tune.CodaStartIndex,
            tune.SectionStyles);
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

    private static void ValidateBarIndex(IReadOnlyList<TuneBar> bars, int barIndex)
    {
        if (barIndex < 0 || barIndex >= bars.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(barIndex));
        }
    }

    private static ChordSpec ParseChord(string value)
    {
        var normalized = value.Trim()
            .Replace('♯', '#')
            .Replace('♭', 'b');
        if (normalized is "N.C." or "N.C" or "NC")
        {
            return ChordSpec.NoChord;
        }

        if (normalized.Length == 0)
        {
            throw new ArgumentException("Enter a chord symbol.", nameof(value));
        }

        return ChordSymbolParser.Parse(normalized);
    }

    private static string NormalizeRehearsalMark(string value)
    {
        var normalized = (value ?? string.Empty).Trim().Trim('[', ']', ' ');
        if (normalized.IndexOfAny(['|', '{', '}', '\r', '\n']) >= 0)
        {
            throw new ArgumentException(
                "A rehearsal mark cannot contain |, braces, or a line break.",
                nameof(value));
        }

        return normalized;
    }

    private static void ValidateCompatibleForms(TuneForm persisted, TuneForm draft)
    {
        if (persisted.Bars.Count != draft.Bars.Count ||
            persisted.HasSeparateEndingForm != draft.HasSeparateEndingForm ||
            (persisted.HasSeparateEndingForm &&
             persisted.EndingFormBars.Count != draft.EndingFormBars.Count))
        {
            throw new InvalidOperationException(
                "The .cho file changed outside Jampanion. Refresh the song before saving.");
        }
    }

    private static string ApplySectionMarkers(
        string content,
        IReadOnlyList<TuneBar> persistedBars,
        IReadOnlyList<TuneBar> draftBars,
        bool endingForm)
    {
        var updated = content;
        for (var index = 0; index < draftBars.Count; index++)
        {
            var persistedMarker = SectionMarkerAt(persistedBars, index);
            var draftMarker = SectionMarkerAt(draftBars, index);
            if (!string.Equals(
                    persistedMarker,
                    draftMarker,
                    StringComparison.Ordinal))
            {
                updated = SetGridLinePrefix(
                    updated,
                    index,
                    draftMarker,
                    endingForm);
            }
        }

        return updated;
    }

    private static string SectionMarkerAt(IReadOnlyList<TuneBar> bars, int index)
    {
        if (index < 0 || index >= bars.Count)
        {
            return string.Empty;
        }

        var section = bars[index].Section;
        return index == 0 ||
               !string.Equals(section, bars[index - 1].Section, StringComparison.Ordinal)
            ? section
            : string.Empty;
    }

    private static string ApplyChordCells(
        string content,
        IReadOnlyList<TuneBar> bars,
        bool endingForm)
    {
        var updated = content;
        for (var index = 0; index < bars.Count; index++)
        {
            updated = ReplaceBarCell(updated, index, bars[index], endingForm);
        }

        return updated;
    }

    private static string ReplaceBarCell(
        string original,
        int targetBarIndex,
        TuneBar replacementBar,
        bool endingForm)
    {
        var document = SplitDocument(original);
        var location = FindBarLocation(
            document.Lines,
            targetBarIndex,
            endingForm);
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
        var location = FindBarLocation(
            document.Lines,
            targetBarIndex,
            endingForm);
        var line = document.Lines[location.LineIndex];
        var firstSeparator = line.IndexOf('|');
        var cells = line[(firstSeparator + 1)..].Split('|').ToList();
        var firstNonEmptyCell = cells.FindIndex(
            cell => !string.IsNullOrWhiteSpace(cell));
        if (firstNonEmptyCell < 0)
        {
            throw new InvalidOperationException(
                "The selected grid line contains no chord bars.");
        }

        var markPrefix = rehearsalMark.Length == 0 ? "  " : rehearsalMark + " ";
        if (location.CellIndex == firstNonEmptyCell)
        {
            document.Lines[location.LineIndex] =
                markPrefix + line[firstSeparator..];
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

    private static string ReplaceSectionStyleDirectives(
        string content,
        IReadOnlyDictionary<string, AccompanimentStyle> sectionStyles)
    {
        var document = SplitDocument(content);
        document.Lines.RemoveAll(IsSectionStyleDirective);
        var insertionIndex = FindDirectiveInsertionIndex(document.Lines);
        var directives = sectionStyles
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
                $"{{x-jampanion-section-style: {pair.Key}|{AccompanimentStyleNames.StorageName(pair.Value)}}}")
            .ToArray();
        document.Lines.InsertRange(insertionIndex, directives);
        return JoinDocument(document);
    }

    private static string SetDirective(
        string content,
        IReadOnlyCollection<string> aliases,
        string canonicalName,
        string value)
    {
        var document = SplitDocument(content);
        var matchingIndices = document.Lines
            .Select((line, index) => (line, index))
            .Where(item =>
                TryGetDirectiveName(item.line, out var name) &&
                aliases.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        var replacement = $"{{{canonicalName}: {value}}}";

        if (matchingIndices.Length == 0)
        {
            document.Lines.Insert(
                FindDirectiveInsertionIndex(document.Lines),
                replacement);
            return JoinDocument(document);
        }

        document.Lines[matchingIndices[0]] = replacement;
        for (var index = matchingIndices.Length - 1; index >= 1; index--)
        {
            document.Lines.RemoveAt(matchingIndices[index]);
        }

        return JoinDocument(document);
    }

    private static int FindDirectiveInsertionIndex(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (IsDirective(lines[index], "start_of_grid") ||
                IsDirective(lines[index], "sog") ||
                lines[index].Contains('|'))
            {
                return index;
            }
        }

        return lines.Count;
    }

    private static bool IsSectionStyleDirective(string line) =>
        TryGetDirectiveName(line, out var name) &&
        name is "x-jampanion-section-style" or
            "x_jampanion_section_style" or
            "x-ai-jam-section-style" or
            "x_ai_jam_section_style";

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
            if (IsDirective(line, "start_of_grid") ||
                IsDirective(line, "sog"))
            {
                currentKind = GridKind.Main;
                continue;
            }

            if (IsDirective(line, "end_of_grid") ||
                IsDirective(line, "eog"))
            {
                currentKind = GridKind.None;
                continue;
            }

            if (IsDirective(line, "start_of_ending_grid") ||
                IsDirective(line, "x-ai-jam-ending-grid"))
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
            return string.Join(
                " ",
                changes.Select(change => StorageSymbol(change.Chord)));
        }

        var beats = Enumerable.Repeat(".", bar.BeatsPerBar).ToArray();
        foreach (var change in changes)
        {
            beats[change.StartBeat] = StorageSymbol(change.Chord);
        }

        return string.Join(" ", beats);
    }

    private static string StorageSymbol(ChordSpec chord) =>
        chord.IsNoChord ? "N.C." : chord.Symbol;

    private static string PreservePadding(string originalCell, string value)
    {
        var leading = originalCell.Length - originalCell.TrimStart().Length;
        var trailing = originalCell.Length - originalCell.TrimEnd().Length;
        var left = leading == 0 ? " " : originalCell[..leading];
        var right = trailing == 0
            ? " "
            : originalCell[(originalCell.Length - trailing)..];
        return left + value + right;
    }

    private static bool IsDirective(string line, string directive) =>
        TryGetDirectiveName(line, out var name) &&
        name.Equals(directive, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetDirectiveName(string line, out string name)
    {
        name = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
        {
            return false;
        }

        var inner = trimmed[1..^1];
        var separator = inner.IndexOf(':');
        name = (separator >= 0 ? inner[..separator] : inner)
            .Trim()
            .ToLowerInvariant();
        return name.Length > 0;
    }

    private static TextDocument SplitDocument(string text)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";
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
        return document.TrailingNewline
            ? text + document.Newline
            : text;
    }

    private static TuneForm WriteValidated(
        string path,
        string content,
        string expectedFingerprint)
    {
        var parsed = ChordProSongParser.Parse(
            content,
            Path.GetFileNameWithoutExtension(path));
        var temporaryPath = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Recheck immediately before the atomic replacement. This catches
            // an external edit made after the initial read but before Save.
            var currentContent = File.ReadAllText(path);
            ValidateExpectedFingerprint(
                currentContent,
                expectedFingerprint);

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
            }
        }

        return parsed;
    }

    private static void ValidateExpectedFingerprint(
        string content,
        string expectedFingerprint)
    {
        if (!string.Equals(
                ComputeFingerprint(content),
                expectedFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The .cho file was changed outside Jampanion. " +
                "Refresh the song before saving.");
        }
    }

    private static string ComputeFingerprint(string content) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed record TextDocument(
        List<string> Lines,
        string Newline,
        bool TrailingNewline);

    private readonly record struct GridBarLocation(
        int LineIndex,
        int CellIndex);
}
