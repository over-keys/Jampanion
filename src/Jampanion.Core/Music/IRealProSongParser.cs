using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jampanion.Core.Music;

public static partial class IRealProSongParser
{
    private const string ChordDataPrefix = "1r34LbKcu7";
    private const int DefaultTempoBpm = 140;
    private const string FineMarker = "~0~";
    private const string SecondEndingMarker = "~1~";
    private const string SegnoMarker = "~2~";
    private const string RepeatCountMarkerPrefix = "~r";

    public static IRealProImportDocument Parse(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var urls = ExtractUrls(source);
        if (urls.Count == 0)
        {
            throw new IRealProImportException("No iReal Pro song link (irealb://) was found.");
        }

        var songs = new List<IRealProImportedSong>();
        var documentWarnings = new List<string>();
        foreach (var url in urls)
        {
            ParseUrl(url, songs, documentWarnings);
        }

        if (songs.Count == 0)
        {
            var detail = documentWarnings.Count == 0 ? string.Empty : $" {documentWarnings[0]}";
            throw new IRealProImportException($"No supported 3/4 or 4/4 songs could be imported.{detail}");
        }

        return new IRealProImportDocument(songs, documentWarnings);
    }

    private static IReadOnlyList<string> ExtractUrls(string source)
    {
        var decoded = WebUtility.HtmlDecode(source);
        var matches = IRealUrlRegex().Matches(decoded);
        if (matches.Count > 0)
        {
            return matches
                .Cast<Match>()
                .Select(match => match.Groups["url"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        var trimmed = decoded.Trim();
        return trimmed.StartsWith("irealb://", StringComparison.OrdinalIgnoreCase)
            ? [trimmed]
            : [];
    }

    private static void ParseUrl(
        string encodedUrl,
        List<IRealProImportedSong> songs,
        List<string> documentWarnings)
    {
        string decodedUrl;
        try
        {
            decodedUrl = Uri.UnescapeDataString(encodedUrl);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            throw new IRealProImportException("The iReal Pro link contains invalid URL encoding.", ex);
        }

        var schemeSeparator = decodedUrl.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            throw new IRealProImportException("The iReal Pro link has no valid URL scheme.");
        }

        var payload = decodedUrl[(schemeSeparator + 3)..];
        var songPayloads = payload.Split("===", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var songPayload in songPayloads)
        {
            if (!songPayload.Contains('='))
            {
                continue;
            }

            var title = GetTitleForError(songPayload);
            try
            {
                songs.Add(ParseSong(songPayload));
            }
            catch (IRealProImportException ex)
            {
                documentWarnings.Add($"Skipped '{title}': {ex.Message}");
            }
        }
    }

    private static IRealProImportedSong ParseSong(string songPayload)
    {
        var parts = EqualsSeparatorRegex().Split(songPayload);
        if (parts.Length < 5)
        {
            throw new IRealProImportException("The song metadata is incomplete.");
        }

        var title = parts[0].Trim();
        var composer = parts[1].Trim();
        var style = parts[2].Trim();
        var key = NormalizeKey(parts[3]);
        if (title.Length == 0)
        {
            throw new IRealProImportException("The song has no title.");
        }

        var chordFieldIndex = FindChordField(parts);
        if (chordFieldIndex < 0)
        {
            throw new IRealProImportException("The encoded chord data was not found.");
        }

        var encodedChords = parts[chordFieldIndex];
        var prefixIndex = encodedChords.IndexOf(ChordDataPrefix, StringComparison.Ordinal);
        var rawChordString = Unscramble(encodedChords[(prefixIndex + ChordDataPrefix.Length)..]);
        var timeSignature = GetTimeSignature(rawChordString);
        var beatsPerBar = timeSignature switch
        {
            "3/4" => 3,
            "4/4" => 4,
            _ => throw new IRealProImportException(
                $"Time signature {timeSignature} is not supported. Jampanion supports 3/4 Jazz Waltz and 4/4 Swing, Jazz Ballad, Bossa Nova, or Latin / Mambo.")
        };

        var warnings = new List<string>();
        var supportedStyle = AccompanimentStyleNames.IsSwing(style) ||
            AccompanimentStyleNames.IsJazzBallad(style) ||
            AccompanimentStyleNames.IsBossaNova(style) ||
            AccompanimentStyleNames.IsJazzWaltz(style) ||
            AccompanimentStyleNames.IsAfroCubanLatin(style);
        if (style.Length > 0 && !supportedStyle)
        {
            warnings.Add(
                timeSignature == "3/4"
                    ? $"Original iReal style '{style}' is preserved as metadata; this version plays the imported 3/4 chart as Jazz Waltz."
                    : $"Original iReal style '{style}' is preserved as metadata; this version plays the imported 4/4 chart as swing.");
        }

        var forms = DecodeForms(rawChordString, warnings, beatsPerBar);
        if (forms.MainMeasures.Count < SessionConstants.BarsPerSegment)
        {
            throw new IRealProImportException(
                $"The main form contains {forms.MainMeasures.Count} bars. Jampanion requires at least 4 bars.");
        }

        if (forms.EndingMeasures is not null)
        {
            warnings.Add(
                $"Final iReal navigation was preserved as a {forms.MainMeasures.Count}-bar loop and a " +
                $"{forms.EndingMeasures.Count}-bar final head/coda form. Jampanion plays the complete final form, then appends a one-bar tonic hold.");
        }

        var tempo = ParseTempo(parts, chordFieldIndex, style);
        var chordPro = BuildChordPro(
            title,
            composer,
            style,
            key,
            tempo,
            timeSignature,
            beatsPerBar,
            forms.MainMeasures,
            forms.EndingMeasures,
            forms.CodaStartIndex,
            warnings);
        try
        {
            _ = ChordProSongParser.Parse(chordPro, title + ".cho");
        }
        catch (ChordProSongParseException ex)
        {
            throw new IRealProImportException($"The converted chart is not playable: {ex.Message}", ex);
        }

        return new IRealProImportedSong(title, composer, style, key, chordPro, warnings);
    }

    private static int FindChordField(IReadOnlyList<string> parts)
    {
        for (var index = 4; index < parts.Count; index++)
        {
            if (parts[index].Contains(ChordDataPrefix, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static string Unscramble(string encoded)
    {
        var result = new StringBuilder(encoded.Length);
        var offset = 0;
        while (encoded.Length - offset > 50)
        {
            var block = encoded.Substring(offset, 50);
            offset += 50;
            result.Append(encoded.Length - offset < 2 ? block : UnscrambleBlock(block));
        }

        result.Append(encoded.AsSpan(offset));
        return result.ToString();
    }

    private static string UnscrambleBlock(string block)
    {
        var result = block.ToCharArray();
        for (var index = 0; index < 5; index++)
        {
            (result[index], result[49 - index]) = (block[49 - index], block[index]);
        }

        for (var index = 10; index < 24; index++)
        {
            (result[index], result[49 - index]) = (block[49 - index], block[index]);
        }

        return new string(result);
    }

    private static string GetTimeSignature(string chordString)
    {
        var match = TimeSignatureRegex().Match(chordString);
        if (!match.Success)
        {
            return "4/4";
        }

        var code = match.Groups["code"].Value;
        return code == "12" ? "12/8" : $"{code[0]}/{code[1]}";
    }

    private static DecodedForms DecodeForms(string rawChordString, List<string> warnings, int beatsPerBar)
    {
        var chordString = CleanupChordString(rawChordString);
        chordString = ExtractNavigationDirectives(chordString, out var directives);
        chordString = chordString.Replace("S", SegnoMarker, StringComparison.Ordinal);
        chordString = RemoveAnnotations(chordString);
        chordString = ExpandLongRepeats(chordString);

        var codaPositions = chordString.Select((character, index) => (character, index))
            .Where(item => item.character == 'Q')
            .Select(item => item.index)
            .ToArray();
        if (codaPositions.Length > 2)
        {
            throw new IRealProImportException("The chart contains more than two coda markers.");
        }

        if (codaPositions.Length == 2)
        {
            var firstCoda = codaPositions[0];
            var secondCoda = codaPositions[1];
            var segno = chordString.IndexOf(SegnoMarker, StringComparison.Ordinal);
            var headReturnStart = segno >= 0 ? segno + SegnoMarker.Length : 0;
            var codaSplit = SplitCodaAtMarker(chordString, secondCoda);

            var mainChordString = RemoveNavigationMarkers(codaSplit.Prefix);
            var headReturn = RemoveNavigationMarkers(chordString[headReturnStart..firstCoda]);
            var coda = RemoveNavigationMarkers(codaSplit.Coda);
            var mainMeasures = DecodeMeasureString(mainChordString, warnings, "main form", beatsPerBar);
            var headReturnMeasures = DecodeMeasureString(headReturn, warnings, "ending form", beatsPerBar);
            var codaMeasures = DecodeMeasureString(coda, warnings, "ending form", beatsPerBar);
            var endingMeasures = headReturnMeasures.Concat(codaMeasures).ToList();
            if (codaMeasures.Count > 0 && string.IsNullOrEmpty(codaMeasures[0].Section))
            {
                endingMeasures[headReturnMeasures.Count] = endingMeasures[headReturnMeasures.Count] with
                {
                    Section = "Ending"
                };
            }

            return new DecodedForms(
                mainMeasures,
                endingMeasures,
                headReturnMeasures.Count);
        }

        if (codaPositions.Length == 1)
        {
            var codaPosition = codaPositions[0];
            var codaSplit = SplitCodaAtMarker(chordString, codaPosition);
            var mainChordString = RemoveNavigationMarkers(codaSplit.Prefix);
            var codaChordString = RemoveNavigationMarkers(codaSplit.Coda);
            var mainMeasures = DecodeMeasureString(mainChordString, warnings, "main form", beatsPerBar);
            var codaMeasures = DecodeMeasureString(codaChordString, warnings, "ending form", beatsPerBar).ToList();
            if (codaMeasures.Count == 0)
            {
                warnings.Add("A single coda marker has no following coda measures and was ignored.");
            }
            else
            {
                if (string.IsNullOrEmpty(codaMeasures[0].Section))
                {
                    codaMeasures[0] = codaMeasures[0] with { Section = "Ending" };
                }

                return new DecodedForms(
                    mainMeasures,
                    mainMeasures.Concat(codaMeasures).ToArray(),
                    mainMeasures.Count);
            }
        }

        var decodedMainMeasures = DecodeMeasureString(
            RemoveNavigationMarkers(chordString),
            warnings,
            "main form",
            beatsPerBar);
        var navigationPass = BuildNavigationPass(decodedMainMeasures, directives, warnings);
        if (navigationPass is not null)
        {
            var mainMeasures = decodedMainMeasures.ToList();
            var targetSection = FindInheritedSection(mainMeasures, navigationPass.StartIndex);
            if (navigationPass.StartIndex < mainMeasures.Count &&
                string.IsNullOrEmpty(mainMeasures[navigationPass.StartIndex].Section) &&
                targetSection.Length > 0)
            {
                mainMeasures[navigationPass.StartIndex] = mainMeasures[navigationPass.StartIndex] with
                {
                    Section = targetSection
                };
            }

            var finalPassSection = navigationPass.IsSecondEnding && targetSection.Length > 0
                ? targetSection + "2"
                : targetSection;
            var finalPass = navigationPass.Measures.ToArray();
            if (finalPass.Length > 0 && finalPassSection.Length > 0)
            {
                finalPass[0] = finalPass[0] with { Section = finalPassSection };
            }

            return new DecodedForms(
                mainMeasures.Concat(finalPass).ToArray(),
                null,
                null);
        }

        return new DecodedForms(
            decodedMainMeasures,
            null,
            null);
    }

    private static string ExtractNavigationDirectives(string chordString, out NavigationDirectives directives)
    {
        var hasDcFine = false;
        var hasDsFine = false;
        var hasDcSecondEnding = false;
        var hasDsSecondEnding = false;
        var hasDcNavigation = false;
        var hasDsNavigation = false;
        var hasStandaloneFine = false;

        var rewritten = StaffTextRegex().Replace(chordString, match =>
        {
            var text = match.Groups["text"].Value;
            text = Regex.Replace(text, @"^\*\d{2}", string.Empty, RegexOptions.CultureInvariant).Trim();
            var isDc = text.Contains("D.C.", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("D.C", StringComparison.OrdinalIgnoreCase);
            var isDs = text.Contains("D.S.", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("D.S", StringComparison.OrdinalIgnoreCase);
            var isSecondEnding = text.Contains("2nd", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("second", StringComparison.OrdinalIgnoreCase);
            var hasFine = text.Contains("Fine", StringComparison.OrdinalIgnoreCase);
            if (isDc || isDs)
            {
                hasDcNavigation |= isDc;
                hasDsNavigation |= isDs;
                hasDcFine |= isDc && hasFine;
                hasDsFine |= isDs && hasFine;
                hasDcSecondEnding |= isDc && isSecondEnding;
                hasDsSecondEnding |= isDs && isSecondEnding;
                return string.Empty;
            }

            if (string.Equals(text, "Fine", StringComparison.OrdinalIgnoreCase))
            {
                hasStandaloneFine = true;
                return FineMarker;
            }

            var repeatCountMatch = Regex.Match(
                text,
                @"^(?<count>[1-9][0-9]*)x$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (repeatCountMatch.Success)
            {
                return $"{RepeatCountMarkerPrefix}{repeatCountMatch.Groups["count"].Value}~";
            }

            return string.Empty;
        });

        directives = new NavigationDirectives(
            hasDcFine || (hasStandaloneFine && hasDcNavigation),
            hasDsFine || (hasStandaloneFine && hasDsNavigation),
            hasDcSecondEnding,
            hasDsSecondEnding);
        return rewritten;
    }

    private static NavigationPass? BuildNavigationPass(
        IReadOnlyList<DecodedMeasure> mainMeasures,
        NavigationDirectives directives,
        List<string> warnings)
    {
        if (!directives.HasFine && !directives.HasSecondEnding)
        {
            return null;
        }

        var startIndex = 0;
        if (directives.ReturnsToSegno)
        {
            startIndex = Array.FindIndex(mainMeasures.ToArray(), measure => measure.IsSegno);
            if (startIndex < 0)
            {
                warnings.Add("D.S. navigation was found without a Segno target; the final form starts at the beginning instead.");
                startIndex = 0;
            }
        }

        if (directives.HasSecondEnding)
        {
            var secondEndingIndex = Array.FindIndex(mainMeasures.ToArray(), measure => measure.IsSecondEndingStart);
            if (secondEndingIndex < 0)
            {
                warnings.Add("D.C./D.S. al 2nd ending was found without an expanded second ending; the ordinary final form is used.");
                return null;
            }

            startIndex = secondEndingIndex;
        }

        var endIndex = mainMeasures.Count - 1;
        if (directives.HasFine)
        {
            var fineIndex = -1;
            for (var index = startIndex; index < mainMeasures.Count; index++)
            {
                if (mainMeasures[index].IsFine)
                {
                    fineIndex = index;
                    break;
                }
            }

            if (fineIndex >= 0)
            {
                endIndex = fineIndex;
            }
            else
            {
                warnings.Add("D.C./D.S. al Fine was found without a reachable Fine marker; the final form continues to its last bar.");
            }
        }

        if (endIndex < startIndex)
        {
            warnings.Add("The D.C./D.S. final navigation target is empty; the ordinary final form is used.");
            return null;
        }

        warnings.Add(
            directives.HasSecondEnding
                ? "D.C./D.S. al 2nd ending navigation was appended to the repeating chorus."
                : directives.ReturnsToSegno
                    ? "D.S. navigation was appended to the repeating chorus."
                    : "D.C. navigation was appended to the repeating chorus.");
        return new NavigationPass(
            mainMeasures.Skip(startIndex).Take(endIndex - startIndex + 1).ToArray(),
            startIndex,
            directives.HasSecondEnding);
    }

    private static string FindInheritedSection(IReadOnlyList<DecodedMeasure> measures, int index)
    {
        for (var candidate = Math.Min(index, measures.Count - 1); candidate >= 0; candidate--)
        {
            if (!string.IsNullOrEmpty(measures[candidate].Section))
            {
                return measures[candidate].Section;
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<DecodedMeasure> DecodeMeasureString(
        string chordString,
        List<string> warnings,
        string formName,
        int beatsPerBar)
    {
        var rawMeasureStrings = MeasureSeparatorRegex().Split(chordString)
            .Where(measure => measure.Trim().Length > 0)
            .ToList();

        var measures = new List<DecodedMeasure>(rawMeasureStrings.Count);
        var pendingSection = string.Empty;
        foreach (var measureString in rawMeasureStrings)
        {
            var isFine = measureString.Contains(FineMarker, StringComparison.Ordinal);
            var isSecondEndingStart = measureString.Contains(SecondEndingMarker, StringComparison.Ordinal);
            var isSegno = measureString.Contains(SegnoMarker, StringComparison.Ordinal);
            var measureText = measureString
                .Replace(FineMarker, string.Empty, StringComparison.Ordinal)
                .Replace(SecondEndingMarker, string.Empty, StringComparison.Ordinal)
                .Replace(SegnoMarker, string.Empty, StringComparison.Ordinal);
            var sectionMatch = RehearsalMarkRegex().Match(measureText);
            var section = sectionMatch.Success
                ? NormalizeRehearsalMark(sectionMatch.Groups["mark"].Value)
                : pendingSection;
            var chordText = sectionMatch.Success
                ? measureText.Remove(sectionMatch.Index, sectionMatch.Length)
                : measureText;

            var repeatMatch = Regex.Match(
                chordText,
                @"^[\s,]*(?<repeat>[xr])[\s,]*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (repeatMatch.Success)
            {
                chordText = repeatMatch.Groups["repeat"].Value.ToLowerInvariant();
            }
            else if (chordText.Trim().Length == 0 || chordText.Trim().Trim(',').Length == 0)
            {
                pendingSection = section;
                continue;
            }

            measures.Add(new DecodedMeasure([chordText], section, isFine, isSecondEndingStart, isSegno));
            pendingSection = string.Empty;
        }
        ExpandSingleAndDoubleRepeats(measures);
        FillSlashPlaceholders(measures);

        var decodedMeasures = new List<DecodedMeasure>(measures.Count);
        for (var measureIndex = 0; measureIndex < measures.Count; measureIndex++)
        {
            var measure = measures[measureIndex];
            var section = measure.Section;
            var chordText = measure.Chords[0];
            var chordMatches = IRealChordRegex().Matches(chordText);
            if (chordMatches.Count == 0)
            {
                throw new IRealProImportException(
                    $"Could not read a chord in {formName} measure {decodedMeasures.Count + 1}.");
            }

            var chordCells = GetChordCells(
                chordText,
                chordMatches,
                beatsPerBar,
                warnings,
                formName,
                decodedMeasures.Count + 1);

            var chords = new List<string>(chordMatches.Count);
            foreach (Match match in chordMatches)
            {
                var original = match.Groups[1].Value.Trim();
                if (original is "n" or "nn" || original.StartsWith("N", StringComparison.Ordinal))
                {
                    chords.Add("N.C.");
                    continue;
                }

                chords.Add(NormalizeChord(original, warnings));
            }

            // iReal uses slash placeholders (p) to carry the preceding chord
            // across one or more cells. They are not additional harmony
            // changes; retain the later cells only when a placeholder caused
            // the repeated symbol.
            if (measure.HasContinuationPlaceholders)
            {
                (chords, chordCells) = CollapseContinuationChords(chords, chordCells);
            }

            if (chords.Count > beatsPerBar)
            {
                throw new IRealProImportException(
                    $"{formName} measure {decodedMeasures.Count + 1} contains {chords.Count} chord changes; at most {beatsPerBar} are supported in {beatsPerBar}/4.");
            }

            if (beatsPerBar == 3 && chords.Count == 2 && chordCells.SequenceEqual(new[] { 0, 2 }))
            {
                warnings.Add(
                    $"{formName} measure {decodedMeasures.Count + 1} has two chords; Jampanion assigns them to beats 1-2 and beat 3.");
            }

            var normalizedChordCells = chordCells.ToList();
            if (normalizedChordCells.Count > 0 && normalizedChordCells[0] > 0)
            {
                var carriedChord = decodedMeasures.Count > 0
                    ? decodedMeasures[^1].Chords[^1]
                    : chords[0];
                if (chords.Count < beatsPerBar)
                {
                    chords.Insert(0, carriedChord);
                    normalizedChordCells.Insert(0, 0);
                }
                else
                {
                    warnings.Add(
                        $"{formName} measure {decodedMeasures.Count + 1} begins after an empty beat; " +
                        "the first written chord is used at the bar downbeat.");
                    normalizedChordCells[0] = 0;
                }
            }

            decodedMeasures.Add(new DecodedMeasure(
                chords,
                section,
                measure.IsFine,
                measure.IsSecondEndingStart,
                measure.IsSegno,
                normalizedChordCells));
        }

        return decodedMeasures;
    }

    private static (List<string> Chords, List<int> Cells) CollapseContinuationChords(
        IReadOnlyList<string> chords,
        IReadOnlyList<int> cells)
    {
        if (chords.Count != cells.Count)
        {
            return (chords.ToList(), cells.ToList());
        }

        var collapsedChords = new List<string>(chords.Count);
        var collapsedCells = new List<int>(cells.Count);
        for (var index = 0; index < chords.Count; index++)
        {
            if (collapsedChords.Count > 0 &&
                string.Equals(collapsedChords[^1], chords[index], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            collapsedChords.Add(chords[index]);
            collapsedCells.Add(cells[index]);
        }

        return (collapsedChords, collapsedCells);
    }

    private static string CleanupChordString(string chordString)
    {
        chordString = Regex.Replace(chordString, "LZ|K", "|", RegexOptions.CultureInvariant);
        chordString = chordString.Replace("cl", "x", StringComparison.Ordinal);
        chordString = Regex.Replace(chordString, @"\*\s*\*", string.Empty, RegexOptions.CultureInvariant);
        chordString = Regex.Replace(chordString, "Y+", string.Empty, RegexOptions.CultureInvariant);
        chordString = Regex.Replace(chordString, "XyQ", " ", RegexOptions.CultureInvariant);
        chordString = Regex.Replace(chordString, @"\|\s*\|", "|", RegexOptions.CultureInvariant);
        chordString = chordString.Replace("Z", string.Empty, StringComparison.Ordinal);
        return chordString.TrimEnd();
    }

    private static IReadOnlyList<int> GetChordCells(
        string chordText,
        MatchCollection chordMatches,
        int beatsPerBar,
        List<string> warnings,
        string formName,
        int measureNumber)
    {
        var cells = new List<int>(chordMatches.Count);
        var cell = 0;
        var scanIndex = 0;
        foreach (Match match in chordMatches)
        {
            for (var index = scanIndex; index < match.Index; index++)
            {
                if (chordText[index] == ' ')
                {
                    cell++;
                }
            }

            if (cell >= beatsPerBar)
            {
                if (chordMatches.Count <= beatsPerBar)
                {
                    warnings.Add(
                        $"{formName} measure {measureNumber} uses overfull iReal spacing; " +
                        $"the {chordMatches.Count}-chord group was imported as a dense beat sequence.");
                    return DenseChordCells(chordMatches.Count, beatsPerBar);
                }

                if (chordMatches.Count == beatsPerBar)
                {
                    return Enumerable.Range(0, beatsPerBar).ToArray();
                }

                throw new IRealProImportException(
                    $"{formName} measure {measureNumber} places a chord beyond beat {beatsPerBar}. " +
                    "The iReal cell spacing is ambiguous, so Jampanion will not guess a beat allocation.");
            }

            cells.Add(cell);
            cell++;
            for (var index = match.Index; index < match.Index + match.Length; index++)
            {
                if (chordText[index] == ' ')
                {
                    cell++;
                }
            }
            scanIndex = match.Index + match.Length;
        }

        return cells;
    }

    private static IReadOnlyList<int> DenseChordCells(int chordCount, int beatsPerBar)
    {
        if (chordCount == 2 && beatsPerBar >= 3)
        {
            // Two written changes conventionally divide the bar at beat 3;
            // keep that established iReal interpretation even when formatting
            // spaces make the encoded cell index look one beat too long.
            return [0, 2];
        }

        return Enumerable.Range(0, chordCount).ToArray();
    }

    private static string RemoveAnnotations(string chordString)
    {
        chordString = Regex.Replace(chordString, @"[\[\]]", "|", RegexOptions.CultureInvariant);
        chordString = Regex.Replace(chordString, @"\|\s*\|", "|", RegexOptions.CultureInvariant);
        chordString = Regex.Replace(chordString, "<.*?>", string.Empty, RegexOptions.CultureInvariant);
        chordString = Regex.Replace(chordString, @"\([^)]*\)", string.Empty, RegexOptions.CultureInvariant);
        chordString = chordString.Replace("f", string.Empty, StringComparison.Ordinal);
        chordString = Regex.Replace(chordString, @"(?<!a)l(?!t)", string.Empty, RegexOptions.CultureInvariant);
        chordString = Regex.Replace(chordString, @"(?<!su)s(?!us)", string.Empty, RegexOptions.CultureInvariant);
        chordString = Regex.Replace(chordString, @"T\d+", string.Empty, RegexOptions.CultureInvariant);
        return chordString.Replace("+*", "+", StringComparison.Ordinal);
    }

    private static (string Prefix, string Coda) SplitCodaAtMarker(string chordString, int codaPosition)
    {
        var prefix = chordString[..codaPosition];
        var coda = chordString[(codaPosition + 1)..];
        var sectionMatch = Regex.Match(
            prefix,
            @"(?<mark>\*[A-Z])\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!sectionMatch.Success)
        {
            return (prefix, coda);
        }

        prefix = prefix.Remove(sectionMatch.Index, sectionMatch.Length);
        coda = sectionMatch.Groups["mark"].Value + coda;
        return (prefix, coda);
    }

    private static string ExpandLongRepeats(string chordString)
    {
        for (var pass = 0; pass < 100; pass++)
        {
            var repeatMatch = LongRepeatRegex().Match(chordString);
            if (!repeatMatch.Success)
            {
                return chordString;
            }

            var body = repeatMatch.Groups[1].Value;
            var suffix = chordString[(repeatMatch.Index + repeatMatch.Length)..];
            var repeatCount = 2;
            var bodyRepeatCount = RepeatCountMarkerRegex().Match(body);
            if (bodyRepeatCount.Success)
            {
                repeatCount = int.Parse(bodyRepeatCount.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture);
                body = body.Remove(bodyRepeatCount.Index, bodyRepeatCount.Length);
            }
            else
            {
                var suffixRepeatCount = RepeatCountMarkerRegex().Match(suffix);
                if (suffixRepeatCount.Success && suffix[..suffixRepeatCount.Index].Trim().Length == 0)
                {
                    repeatCount = int.Parse(suffixRepeatCount.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    suffix = suffix.Remove(0, suffixRepeatCount.Index + suffixRepeatCount.Length);
                }
            }

            if (repeatCount is < 1 or > 32)
            {
                throw new IRealProImportException(
                    $"An iReal repeat count of {repeatCount} is outside the supported range 1-32.");
            }

            var endingMatch = RepeatEndingRegex().Match(body);
            if (endingMatch.Success)
            {
                var firstPass = RepeatEndingMarkerRegex().Replace(body, string.Empty);
                suffix = suffix.TrimStart();
                var trailingBar = suffix.StartsWith('|') ? string.Empty : "|";
                var expanded = chordString[..repeatMatch.Index] + "|" + firstPass + trailingBar + suffix;
                var repeatPartMatch = BeforeFirstEndingRegex().Match(body);
                if (!repeatPartMatch.Success)
                {
                    throw new IRealProImportException("A repeated section contains an invalid ending marker.");
                }

                var repeatedBody = RemoveNavigationMarkers(repeatPartMatch.Groups[1].Value);
                chordString = RepeatEndingAfterBarRegex().Replace(expanded, match =>
                    "|" + SecondEndingMarker + repeatedBody + match.Groups["section"].Value);
            }
            else
            {
                var repeatedBody = RemoveNavigationMarkers(body);
                var repeatedPasses = new StringBuilder(body.Length * Math.Max(1, repeatCount - 1));
                for (var repeatIndex = 1; repeatIndex < repeatCount; repeatIndex++)
                {
                    repeatedPasses.Append(" |").Append(repeatedBody);
                }

                var suffixStartsWithBar = suffix.TrimStart().StartsWith("|", StringComparison.Ordinal);
                var trailingBar = suffixStartsWithBar || suffix.Trim().Length == 0 ? string.Empty : "|";
                chordString = chordString[..repeatMatch.Index] + "|" + body + repeatedPasses +
                              trailingBar + suffix + "|";
            }
        }

        throw new IRealProImportException("Too many nested repeat markers were found.");
    }

    private static void ExpandSingleAndDoubleRepeats(List<DecodedMeasure> measures)
    {
        for (var index = 0; index < measures.Count; index++)
        {
            if (measures[index].Chords.Count == 1 && measures[index].Chords[0] == "x")
            {
                if (index == 0)
                {
                    throw new IRealProImportException("The first measure cannot repeat a previous measure.");
                }

                measures[index] = measures[index] with
                {
                    Chords = measures[index - 1].Chords,
                    HasContinuationPlaceholders = measures[index - 1].HasContinuationPlaceholders
                };
            }
        }

        for (var index = 0; index < measures.Count; index++)
        {
            if (measures[index].Chords.Count != 1 || measures[index].Chords[0] != "r")
            {
                continue;
            }

            if (index < 2)
            {
                throw new IRealProImportException("A two-measure repeat has no preceding two measures.");
            }

            var first = measures[index - 2];
            var second = measures[index - 1];
            measures[index] = measures[index] with
            {
                Chords = first.Chords,
                HasContinuationPlaceholders = first.HasContinuationPlaceholders
            };
            measures.Insert(index + 1, measures[index] with
            {
                Chords = second.Chords,
                Section = string.Empty,
                HasContinuationPlaceholders = second.HasContinuationPlaceholders
            });
            index++;
        }
    }

    private static void FillSlashPlaceholders(List<DecodedMeasure> measures)
    {
        for (var measureIndex = 0; measureIndex < measures.Count; measureIndex++)
        {
            var hasContinuationPlaceholders = measures[measureIndex].HasContinuationPlaceholders ||
                measures[measureIndex].Chords.Any(chord => chord.Contains('p'));
            while (measures[measureIndex].Chords.Any(chord => chord.Contains('p')))
            {
                var chords = measures[measureIndex].Chords.ToList();
                var slashIndex = chords.FindIndex(chord => chord.Contains('p', StringComparison.Ordinal));
                var chord = chords[slashIndex];
                var placeholderIndex = chord.IndexOf('p');
                string previousChord;
                if (placeholderIndex == 0)
                {
                    if (measureIndex == 0)
                    {
                        throw new IRealProImportException("The first measure begins with a continuation slash.");
                    }

                    var previousMatches = IRealChordRegex().Matches(measures[measureIndex - 1].Chords[^1]);
                    if (previousMatches.Count == 0)
                    {
                        throw new IRealProImportException("A continuation slash has no preceding chord.");
                    }

                    previousChord = previousMatches[^1].Groups[1].Value.Trim();
                }
                else
                {
                    var previousMatches = IRealChordRegex().Matches(chord[..placeholderIndex]);
                    if (previousMatches.Count == 0)
                    {
                        throw new IRealProImportException("A continuation slash has no preceding chord.");
                    }

                    previousChord = previousMatches[^1].Groups[1].Value.Trim();
                }

                chords[slashIndex] = chord[..placeholderIndex] + previousChord + chord[(placeholderIndex + 1)..];
                measures[measureIndex] = measures[measureIndex] with { Chords = chords };
            }

            if (hasContinuationPlaceholders)
            {
                measures[measureIndex] = measures[measureIndex] with
                {
                    HasContinuationPlaceholders = true
                };
            }
        }
    }

    private static string NormalizeChord(string chord, List<string> warnings)
    {
        var slashBass = string.Empty;
        var slashIndex = chord.LastIndexOf('/');
        if (slashIndex > 0 && NoteNameRegex().IsMatch(chord[(slashIndex + 1)..]))
        {
            slashBass = "/" + chord[(slashIndex + 1)..];
            chord = chord[..slashIndex];
        }

        var match = RootAndQualityRegex().Match(chord);
        if (!match.Success)
        {
            throw new IRealProImportException($"Unsupported iReal chord symbol '{chord}'.");
        }

        var root = match.Groups["root"].Value;
        var quality = match.Groups["quality"].Value;
        string normalizedQuality;
        if (quality.StartsWith("-", StringComparison.Ordinal))
        {
            normalizedQuality = "m" + quality[1..];
        }
        else if (quality.StartsWith("^", StringComparison.Ordinal))
        {
            normalizedQuality = "maj" + quality[1..];
        }
        else if (quality.StartsWith("h", StringComparison.Ordinal))
        {
            normalizedQuality = "m7b5";
        }
        else if (quality == "o")
        {
            normalizedQuality = "dim";
        }
        else if (quality.StartsWith("o", StringComparison.Ordinal))
        {
            normalizedQuality = "dim7";
        }
        else if (quality.StartsWith("+", StringComparison.Ordinal))
        {
            normalizedQuality = "aug";
        }
        else if (quality.Contains("sus", StringComparison.OrdinalIgnoreCase) && quality is not "sus" and not "sus4" and not "7sus" and not "7sus4")
        {
            normalizedQuality = "7sus";
        }
        else
        {
            normalizedQuality = quality;
        }

        var normalized = root + normalizedQuality + slashBass;
        try
        {
            _ = ChordSymbolParser.Parse(normalized);
            return normalized;
        }
        catch (FormatException)
        {
            var simplified = SimplifyChord(root, normalizedQuality, slashBass);
            try
            {
                _ = ChordSymbolParser.Parse(simplified);
            }
            catch (FormatException ex)
            {
                throw new IRealProImportException($"Unsupported iReal chord symbol '{chord}'.", ex);
            }

            warnings.Add($"Chord '{chord + slashBass}' was simplified to '{simplified}' for playback.");
            return simplified;
        }
    }

    private static string SimplifyChord(string root, string quality, string slashBass)
    {
        var lower = quality.ToLowerInvariant();
        if (lower.Contains("alt", StringComparison.Ordinal))
        {
            return root + "7alt" + slashBass;
        }

        if (lower.Contains("sus", StringComparison.Ordinal))
        {
            return root + "7sus" + slashBass;
        }

        if (lower.StartsWith("m7b5", StringComparison.Ordinal) || lower.StartsWith("h", StringComparison.Ordinal))
        {
            return root + "m7b5" + slashBass;
        }

        if (lower.StartsWith("m", StringComparison.Ordinal) || lower.StartsWith("-", StringComparison.Ordinal))
        {
            return root + (lower.Contains('6') ? "m6" : "m7") + slashBass;
        }

        if (lower.StartsWith("maj", StringComparison.Ordinal) || lower.StartsWith("^", StringComparison.Ordinal))
        {
            return root + "maj7" + slashBass;
        }

        if (lower.StartsWith("dim", StringComparison.Ordinal) || lower.StartsWith("o", StringComparison.Ordinal))
        {
            return root + "dim7" + slashBass;
        }

        if (lower.StartsWith("aug", StringComparison.Ordinal) || lower.StartsWith("+", StringComparison.Ordinal))
        {
            return root + "aug" + slashBass;
        }

        if (lower.Any(char.IsDigit))
        {
            return root + "7" + slashBass;
        }

        return root + slashBass;
    }

    private static int ParseTempo(IReadOnlyList<string> parts, int chordFieldIndex, string style)
    {
        for (var index = chordFieldIndex + 1; index < parts.Count; index++)
        {
            if (int.TryParse(parts[index], out var bpm) && bpm is >= 40 and <= 300)
            {
                return bpm;
            }
        }

        var styleTempo = Regex.Match(style, @"(?<bpm>\d{2,3})\s*bpm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (styleTempo.Success && int.TryParse(styleTempo.Groups["bpm"].Value, out var namedBpm) && namedBpm is >= 40 and <= 300)
        {
            return namedBpm;
        }

        var normalized = style.Replace("-", " ", StringComparison.Ordinal).Trim();
        if (normalized.Contains("up tempo swing", StringComparison.OrdinalIgnoreCase)) return 240;
        if (normalized.Contains("medium up swing", StringComparison.OrdinalIgnoreCase)) return 160;
        if (normalized.Contains("medium swing", StringComparison.OrdinalIgnoreCase)) return 120;
        if (normalized.Contains("slow swing", StringComparison.OrdinalIgnoreCase)) return 80;
        if (AccompanimentStyleNames.IsBossaNova(normalized)) return 140;
        if (AccompanimentStyleNames.IsAfroCubanLatin(normalized)) return 180;
        if (AccompanimentStyleNames.IsJazzWaltz(normalized)) return 120;
        return AccompanimentStyleNames.IsJazzBallad(normalized) ? 64 : DefaultTempoBpm;
    }

    private static string NormalizeKey(string key)
    {
        key = key.Trim();
        if (key.EndsWith('-'))
        {
            return key[..^1] + "m";
        }

        return key;
    }

    private static string BuildChordPro(
        string title,
        string composer,
        string style,
        string key,
        int tempo,
        string timeSignature,
        int beatsPerBar,
        IReadOnlyList<DecodedMeasure> measures,
        IReadOnlyList<DecodedMeasure>? endingMeasures,
        int? codaStartIndex,
        IReadOnlyList<string> warnings)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{{title: {SanitizeDirectiveValue(title)}}}");
        if (composer.Length > 0)
        {
            builder.AppendLine($"{{subtitle: {SanitizeDirectiveValue(composer)}}}");
        }
        if (key.Length > 0)
        {
            builder.AppendLine($"{{key: {SanitizeDirectiveValue(key)}}}");
        }
        builder.AppendLine($"{{time: {timeSignature}}}");
        if (style.Length > 0)
        {
            builder.AppendLine($"{{style: {SanitizeDirectiveValue(style)}}}");
        }
        builder.AppendLine($"{{tempo: {tempo}}}");
        builder.AppendLine($"{{x-ai-jam-id: {CreateId(title)}}}");
        builder.AppendLine();
        AppendGrid(builder, "start_of_grid", "end_of_grid", string.Empty, measures, beatsPerBar);
        if (endingMeasures is not null)
        {
            builder.AppendLine();
            if (codaStartIndex is int codaIndex)
            {
                builder.AppendLine($"{{x-ai-jam-coda-start: {codaIndex}}}");
            }
            AppendGrid(builder, "start_of_grid", "end_of_grid", string.Empty, endingMeasures, beatsPerBar, isEndingGrid: true);
        }
        return builder.ToString();
    }

    private static void AppendGrid(
        StringBuilder builder,
        string startDirective,
        string endDirective,
        string sectionName,
        IReadOnlyList<DecodedMeasure> measures,
        int beatsPerBar,
        bool isEndingGrid = false)
    {
        var displayMeasures = NumberRepeatedSections(measures);
        builder.AppendLine($"{{{startDirective}}}");
        if (isEndingGrid)
        {
            builder.AppendLine("{x-ai-jam-ending-grid}");
        }
        for (var index = 0; index < displayMeasures.Count;)
        {
            var section = displayMeasures[index].Section.Length == 0
                ? index == 0 ? sectionName : string.Empty
                : displayMeasures[index].Section;
            builder.Append($"{section} | ");
            var lineBarCount = 1;
            while (lineBarCount < SessionConstants.BarsPerSegment &&
                   index + lineBarCount < displayMeasures.Count &&
                   displayMeasures[index + lineBarCount].Section.Length == 0)
            {
                lineBarCount++;
            }

            for (var offset = 0; offset < lineBarCount; offset++)
            {
                builder.Append(FormatChordCells(displayMeasures[index + offset], beatsPerBar));
                builder.Append(" | ");
            }
            builder.AppendLine();
            index += lineBarCount;
        }
        builder.AppendLine($"{{{endDirective}}}");
    }

    private static string FormatChordCells(DecodedMeasure measure, int beatsPerBar)
    {
        if (measure.ChordCells is null || measure.ChordCells.Count != measure.Chords.Count)
        {
            return string.Join(" ", measure.Chords);
        }

        var cells = Enumerable.Repeat(".", beatsPerBar).ToArray();
        for (var index = 0; index < measure.Chords.Count; index++)
        {
            cells[measure.ChordCells[index]] = measure.Chords[index];
        }

        return string.Join(" ", cells);
    }

    private static IReadOnlyList<DecodedMeasure> NumberRepeatedSections(IReadOnlyList<DecodedMeasure> measures)
    {
        var repeatedCounts = measures
            .Where(measure => measure.Section.Length > 0 &&
                              !string.Equals(measure.Section, "Ending", StringComparison.Ordinal) &&
                              !IsLeadInSection(measure.Section))
            .GroupBy(measure => measure.Section, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        if (repeatedCounts.Count == 0)
        {
            return measures;
        }

        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return measures
            .Select(measure =>
            {
                if (!repeatedCounts.ContainsKey(measure.Section))
                {
                    return measure;
                }

                occurrences.TryGetValue(measure.Section, out var occurrence);
                occurrence++;
                occurrences[measure.Section] = occurrence;
                return measure with { Section = $"{measure.Section}{occurrence}" };
            })
            .ToArray();
    }

    private static string NormalizeRehearsalMark(string mark) => mark.Trim().ToUpperInvariant() switch
    {
        "I" => "Intro",
        "V" => "Verse",
        var normalized => normalized
    };

    private static bool IsLeadInSection(string section) =>
        string.Equals(section, "Intro", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(section, "Verse", StringComparison.OrdinalIgnoreCase);

    private static string RemoveNavigationMarkers(string value) =>
        RepeatCountMarkerRegex().Replace(
            Regex.Replace(value, @"U|S|Q|N\d", string.Empty, RegexOptions.CultureInvariant),
            string.Empty);

    private static string GetTitleForError(string songPayload)
    {
        var separator = songPayload.IndexOf('=');
        var title = separator < 0 ? songPayload : songPayload[..separator];
        return string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
    }

    private static string CreateId(string title)
    {
        var words = Regex.Matches(title.ToLowerInvariant(), "[a-z0-9]+", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Value);
        var id = string.Join('-', words);
        return id.Length == 0 ? "untitled" : id;
    }

    private static string SanitizeDirectiveValue(string value) =>
        value.Replace("}", ")", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    private sealed record DecodedForms(
        IReadOnlyList<DecodedMeasure> MainMeasures,
        IReadOnlyList<DecodedMeasure>? EndingMeasures,
        int? CodaStartIndex);

    private sealed record NavigationPass(
        IReadOnlyList<DecodedMeasure> Measures,
        int StartIndex,
        bool IsSecondEnding);

    private sealed record NavigationDirectives(
        bool HasDcFine,
        bool HasDsFine,
        bool HasDcSecondEnding,
        bool HasDsSecondEnding)
    {
        public bool HasFine => HasDcFine || HasDsFine;
        public bool HasSecondEnding => HasDcSecondEnding || HasDsSecondEnding;
        public bool ReturnsToSegno => HasDsFine || HasDsSecondEnding;
    }

    private sealed record DecodedMeasure(
        IReadOnlyList<string> Chords,
        string Section,
        bool IsFine = false,
        bool IsSecondEndingStart = false,
        bool IsSegno = false,
        IReadOnlyList<int>? ChordCells = null,
        bool HasContinuationPlaceholders = false);

    [GeneratedRegex("(?<url>irealb://[^\\\"'<>\\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IRealUrlRegex();

    [GeneratedRegex("=+", RegexOptions.CultureInvariant)]
    private static partial Regex EqualsSeparatorRegex();

    [GeneratedRegex("T(?<code>\\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex TimeSignatureRegex();

    [GeneratedRegex(@"\||LZ|K|Z|\{|\}|\[|\]", RegexOptions.CultureInvariant)]
    private static partial Regex MeasureSeparatorRegex();

    [GeneratedRegex(@"\{(.+?)\}", RegexOptions.CultureInvariant)]
    private static partial Regex LongRepeatRegex();

    [GeneratedRegex("N(\\d)", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatEndingRegex();

    [GeneratedRegex("N\\d", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatEndingMarkerRegex();

    [GeneratedRegex("([^N]+)N\\d", RegexOptions.CultureInvariant)]
    private static partial Regex BeforeFirstEndingRegex();

    [GeneratedRegex("\\|\\s*(?<section>\\*[A-Z])?\\s*N(\\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepeatEndingAfterBarRegex();

    [GeneratedRegex(@"<(?<text>.*?)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StaffTextRegex();

    [GeneratedRegex(@"\*(?<mark>[A-Z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RehearsalMarkRegex();

    [GeneratedRegex(@"~r(?<count>[1-9][0-9]*)~", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatCountMarkerRegex();

    [GeneratedRegex("(?<!/)([A-GNn][^A-GN/]*(?:/[A-GN][#b]?)?)", RegexOptions.CultureInvariant)]
    private static partial Regex IRealChordRegex();

    [GeneratedRegex("^(?<root>[A-G](?:#|b)?)(?<quality>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex RootAndQualityRegex();

    [GeneratedRegex("^[A-G](?:#|b)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NoteNameRegex();
}
