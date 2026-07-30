using System.Text;
using System.Text.Json.Serialization;
using Jampanion.Core.Music;

namespace Jampanion.Web.Models;

public sealed class WebSongDocument
{
    private static readonly HashSet<string> ModelOwnedDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "t", "key", "time", "time_signature", "tempo",
        "x-ai-jam-id", "x-ai-jam-style", "x-ai-jam-coda-start",
        "x-jampanion-section-style", "x_jampanion_section_style",
        "x-ai-jam-section-style", "x_ai_jam_section_style",
        "start_of_grid", "sog", "end_of_grid", "eog",
        "start_of_ending_grid", "end_of_ending_grid", "x-ai-jam-ending-grid"
    };

    public string Id { get; set; } = "untitled";
    public string Title { get; set; } = "Untitled";
    public string Key { get; set; } = string.Empty;
    public string OriginalKey { get; set; } = string.Empty;
    public string TimeSignature { get; set; } = "4/4";
    public int TempoBpm { get; set; } = 140;
    public AccompanimentStyle Style { get; set; } = AccompanimentStyle.Swing;
    public List<WebEditableBar> Bars { get; set; } = [];
    public List<WebEditableBar> EndingBars { get; set; } = [];
    public int? CodaStartIndex { get; set; }
    public Dictionary<string, AccompanimentStyle> SectionStyles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ChordPro metadata that the accompaniment engine does not interpret, such
    // as iReal Pro's composer subtitle and original style, must survive Web edits.
    // The modeled directives above are regenerated from the current UI state.
    public List<string> PreservedHeaderLines { get; set; } = [];

    [JsonIgnore]
    public int BeatsPerBar => TimeSignature == "3/4" ? 3 : 4;

    [JsonIgnore]
    public bool HasEndingForm => EndingBars.Count > 0;

    public static WebSongDocument FromTuneForm(TuneForm tune, string? originalSource = null)
    {
        ArgumentNullException.ThrowIfNull(tune);
        return new WebSongDocument
        {
            Id = tune.Id,
            Title = tune.Title,
            Key = tune.Key,
            OriginalKey = tune.Key,
            TimeSignature = tune.TimeSignature,
            TempoBpm = tune.DefaultTempoBpm,
            Style = tune.AccompanimentStyle,
            Bars = CreateBars(tune.Bars),
            EndingBars = tune.HasSeparateEndingForm ? CreateBars(tune.EndingFormBars) : [],
            CodaStartIndex = tune.CodaStartIndex,
            SectionStyles = tune.SectionStyles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            PreservedHeaderLines = ExtractPreservedHeaderLines(originalSource)
        };
    }

    public static WebSongDocument Parse(string source, string? sourceName = null) =>
        FromTuneForm(ChordProSongParser.Parse(source, sourceName), source);

    public TuneForm ToTuneForm() => ChordProSongParser.Parse(ToChordPro(), Title);

    public string ToChordPro()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{{title: {CleanDirectiveValue(Title)}}}");
        builder.AppendLine($"{{x-ai-jam-id: {CleanDirectiveValue(Id)}}}");
        if (!string.IsNullOrWhiteSpace(Key))
        {
            builder.AppendLine($"{{key: {CleanDirectiveValue(Key)}}}");
        }

        foreach (var line in PreservedHeaderLines)
        {
            var cleaned = CleanPreservedHeaderLine(line);
            if (cleaned.Length > 0)
            {
                builder.AppendLine(cleaned);
            }
        }

        builder.AppendLine($"{{time: {TimeSignature}}}");
        builder.AppendLine($"{{tempo: {Math.Clamp(TempoBpm, 40, 300)}}}");
        // Write the explicit playback style after preserved {style: ...}
        // metadata so the Jampanion style remains authoritative.
        builder.AppendLine($"{{x-ai-jam-style: {AccompanimentStyleNames.StorageName(Style)}}}");

        foreach (var pair in SectionStyles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                builder.AppendLine($"{{x-jampanion-section-style: {CleanDirectiveValue(pair.Key)}|{AccompanimentStyleNames.StorageName(pair.Value)}}}");
            }
        }

        if (CodaStartIndex is int codaStart && codaStart >= 0)
        {
            builder.AppendLine($"{{x-ai-jam-coda-start: {codaStart}}}");
        }

        builder.AppendLine("{start_of_grid}");
        AppendGrid(builder, Bars, BeatsPerBar);
        builder.AppendLine("{end_of_grid}");

        if (EndingBars.Count > 0)
        {
            builder.AppendLine("{start_of_ending_grid}");
            AppendGrid(builder, EndingBars, BeatsPerBar);
            builder.AppendLine("{end_of_ending_grid}");
        }

        return builder.ToString();
    }

    public WebSongDocument DeepClone() => Parse(ToChordPro(), Title);

    public void Normalize()
    {
        Title = string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title.Trim();
        Id = CreateId(string.IsNullOrWhiteSpace(Id) ? Title : Id);
        TempoBpm = Math.Clamp(TempoBpm, 40, 300);
        TimeSignature = TimeSignature == "3/4" ? "3/4" : "4/4";
        NormalizeBars(Bars, BeatsPerBar);
        NormalizeBars(EndingBars, BeatsPerBar);
        PreservedHeaderLines = PreservedHeaderLines
            .Select(CleanPreservedHeaderLine)
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public string EffectiveSectionAt(int barIndex)
    {
        var section = string.Empty;
        for (var index = 0; index <= barIndex && index < Bars.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(Bars[index].RehearsalMark))
            {
                section = Bars[index].RehearsalMark.Trim();
            }
        }
        return section;
    }

    private static List<WebEditableBar> CreateBars(IReadOnlyList<TuneBar> source)
    {
        var result = new List<WebEditableBar>(source.Count);
        string? priorSection = null;
        foreach (var bar in source)
        {
            var cells = Enumerable.Repeat(".", bar.BeatsPerBar).ToArray();
            foreach (var change in bar.ChordChanges)
            {
                cells[change.StartBeat] = change.Chord.Symbol;
            }

            var isSectionStart = priorSection is null || !string.Equals(priorSection, bar.Section, StringComparison.OrdinalIgnoreCase);
            result.Add(new WebEditableBar
            {
                Index = bar.Index,
                RehearsalMark = isSectionStart ? bar.Section : string.Empty,
                BeatCells = cells.ToList()
            });
            priorSection = bar.Section;
        }
        return result;
    }

    private static void AppendGrid(StringBuilder builder, IReadOnlyList<WebEditableBar> bars, int beatsPerBar)
    {
        foreach (var bar in bars)
        {
            var cells = NormalizeBeatCells(bar.BeatCells, beatsPerBar);
            builder.Append(CleanGridLabel(bar.RehearsalMark));
            builder.Append(" | ");
            builder.Append(string.Join(' ', cells));
            builder.AppendLine(" |");
        }
    }

    private static void NormalizeBars(List<WebEditableBar> bars, int beatsPerBar)
    {
        for (var index = 0; index < bars.Count; index++)
        {
            bars[index].Index = index;
            bars[index].RehearsalMark = CleanGridLabel(bars[index].RehearsalMark);
            bars[index].BeatCells = NormalizeBeatCells(bars[index].BeatCells, beatsPerBar).ToList();
        }
    }

    private static string[] NormalizeBeatCells(IReadOnlyList<string>? source, int beatsPerBar)
    {
        var result = Enumerable.Repeat(".", beatsPerBar).ToArray();
        if (source is not null)
        {
            for (var beat = 0; beat < Math.Min(beatsPerBar, source.Count); beat++)
            {
                var value = source[beat]?.Trim() ?? string.Empty;
                result[beat] = string.IsNullOrWhiteSpace(value) || value == "/" ? "." : value;
            }
        }

        if (result[0] == ".")
        {
            result[0] = "N.C.";
        }
        return result;
    }

    private static List<string> ExtractPreservedHeaderLines(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return [];
        }

        var result = new List<string>();
        var normalized = source
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                result.Add(line.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));
                continue;
            }

            if (!TryReadDirectiveName(line, out var name))
            {
                // The first chord-grid line marks the end of header metadata.
                if (line.Contains('|'))
                {
                    break;
                }
                continue;
            }

            if (name is "start_of_grid" or "sog" or "start_of_ending_grid" or "x-ai-jam-ending-grid")
            {
                break;
            }
            if (!ModelOwnedDirectives.Contains(name))
            {
                result.Add(CleanPreservedHeaderLine(line));
            }
        }

        return result
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryReadDirectiveName(string line, out string name)
    {
        name = string.Empty;
        if (line.Length < 2 || line[0] != '{' || line[^1] != '}')
        {
            return false;
        }

        var inner = line[1..^1];
        var separator = inner.IndexOf(':');
        name = (separator >= 0 ? inner[..separator] : inner).Trim().ToLowerInvariant();
        return name.Length > 0;
    }

    private static string CleanPreservedHeaderLine(string? value)
    {
        var line = (value ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        if (line.StartsWith('#'))
        {
            return line;
        }
        if (!TryReadDirectiveName(line, out var name) || ModelOwnedDirectives.Contains(name))
        {
            return string.Empty;
        }
        return line;
    }

    private static string CleanDirectiveValue(string? value) =>
        (value ?? string.Empty).Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private static string CleanGridLabel(string? value) =>
        (value ?? string.Empty).Replace("|", string.Empty, StringComparison.Ordinal)
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    public static string CreateId(string value)
    {
        var normalized = new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }
        normalized = normalized.Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "untitled" : normalized;
    }
}

public sealed class WebEditableBar
{
    public int Index { get; set; }
    public string RehearsalMark { get; set; } = string.Empty;
    public List<string> BeatCells { get; set; } = [];
}

public sealed record StoredWebSong(string Id, string Title, string Source);

public sealed record StoredWebSongMetadata(string Id, string Title);
