namespace Jampanion.Core.Music;

public sealed class TuneForm
{
    public TuneForm(string title, IReadOnlyList<TuneBar> bars)
        : this(CreateId(title), title, string.Empty, bars, 140)
    {
    }

    public TuneForm(
        string id,
        string title,
        IReadOnlyList<TuneBar> bars,
        ChordSpec endingChord,
        int defaultTempoBpm)
        : this(id, title, string.Empty, bars, defaultTempoBpm)
    {
        if (endingChord.Symbol != EndingChord.Symbol)
        {
            throw new ArgumentException("The ending chord must be the final chord in the form.", nameof(endingChord));
        }
    }

    public TuneForm(
        string id,
        string title,
        string key,
        IReadOnlyList<TuneBar> bars,
        int defaultTempoBpm,
        IReadOnlyList<TuneBar>? endingFormBars = null,
        string style = "",
        string timeSignature = "4/4",
        int? codaStartIndex = null,
        IReadOnlyDictionary<string, AccompanimentStyle>? sectionStyles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(bars);

        var beatsPerBar = ParseBeatsPerBar(timeSignature);
        ValidateBars(bars, SessionConstants.BarsPerSegment, nameof(bars), beatsPerBar);
        if (endingFormBars is not null)
        {
            ValidateBars(endingFormBars, EndingPlanBarCount, nameof(endingFormBars), beatsPerBar);
            if (codaStartIndex is int endingIndex && (endingIndex < 0 || endingIndex >= endingFormBars.Count))
            {
                throw new ArgumentOutOfRangeException(nameof(codaStartIndex));
            }
        }
        else if (codaStartIndex is not null)
        {
            throw new ArgumentException("A Coda start requires a separate ending form.", nameof(codaStartIndex));
        }

        if (defaultTempoBpm is < 40 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultTempoBpm));
        }

        Id = id;
        Title = title;
        Key = key.Trim();
        TimeSignature = $"{beatsPerBar}/4";
        BeatsPerBar = beatsPerBar;
        BarTicks = SessionConstants.GetBarTicks(beatsPerBar);
        OriginalStyle = style.Trim();
        AccompanimentStyle = AccompanimentStyleNames.Parse(OriginalStyle, TimeSignature);
        SectionStyles = NormalizeSectionStyles(sectionStyles, beatsPerBar);
        Bars = bars;
        EndingFormBars = endingFormBars ?? bars;
        HasSeparateEndingForm = endingFormBars is not null;
        CodaStartIndex = HasSeparateEndingForm ? codaStartIndex : null;
        CodaBars = CodaStartIndex is int startIndex
            ? EndingFormBars.Skip(startIndex).ToArray()
            : Array.Empty<TuneBar>();
        CodaJumpBarIndex = CodaBars.Count > 0 && CodaStartIndex is int codaIndex
            ? Math.Max(0, codaIndex - 1)
            : null;
        LoopStartBarIndex = FindLoopStartBarIndex(Bars);
        LoopStartSegmentIndex = LoopStartBarIndex % SessionConstants.BarsPerSegment == 0
            ? LoopStartBarIndex / SessionConstants.BarsPerSegment
            : 0;
        EndingChord = EndingFormBars[^1].ChordChanges[^1].Chord;
        TonicChord = CreateTonicChord(Key, EndingChord);
        DefaultTempoBpm = defaultTempoBpm;
    }

    public string Id { get; }
    public string Title { get; }
    public string Key { get; }
    public string OriginalStyle { get; }
    public AccompanimentStyle AccompanimentStyle { get; }
    public IReadOnlyDictionary<string, AccompanimentStyle> SectionStyles { get; }
    public string TimeSignature { get; }
    public int BeatsPerBar { get; }
    public long BarTicks { get; }
    public IReadOnlyList<TuneBar> Bars { get; }
    public IReadOnlyList<TuneBar> EndingFormBars { get; }
    public bool HasSeparateEndingForm { get; }
    public int? CodaStartIndex { get; }
    public IReadOnlyList<TuneBar> CodaBars { get; }
    public bool HasCoda => CodaBars.Count > 0;
    public int? CodaJumpBarIndex { get; }
    public int LoopStartBarIndex { get; }
    public int LoopStartSegmentIndex { get; }
    public bool HasLeadIn => LoopStartBarIndex > 0;
    public bool HasIntro => HasLeadIn;
    public ChordSpec EndingChord { get; }
    public ChordSpec TonicChord { get; }
    public int DefaultTempoBpm { get; }
    public int HalfChorusBars => Bars.Count / 2;
    public int SegmentCount => GetSegmentCount(Bars.Count);
    public int EndingLeadInBarCount => EndingFormBars.Count;
    public int EndingLeadInSegmentCount => GetSegmentCount(EndingLeadInBarCount);
    public int EndingStartBar => EndingFormBars.Count + 1;
    public const int EndingPlanBarCount = 1;

    public int GetSegmentBarCount(int segmentIndex)
    {
        if (segmentIndex is < 0 || segmentIndex >= SegmentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        }

        return Math.Min(SessionConstants.BarsPerSegment, Bars.Count - segmentIndex * SessionConstants.BarsPerSegment);
    }

    public int GetEndingLeadInSegmentBarCount(int segmentIndex)
    {
        if (segmentIndex is < 0 || segmentIndex >= EndingLeadInSegmentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        }

        return Math.Min(
            SessionConstants.BarsPerSegment,
            EndingLeadInBarCount - segmentIndex * SessionConstants.BarsPerSegment);
    }

    public TuneForm WithAccompanimentStyle(
        AccompanimentStyle style,
        bool preserveSectionStyles = false)
    {
        if (style == AccompanimentStyle.JazzWaltz && BeatsPerBar != 3)
        {
            throw new ArgumentException("Jazz Waltz playback requires a 3/4 tune.", nameof(style));
        }

        if (style != AccompanimentStyle.JazzWaltz && BeatsPerBar == 3)
        {
            throw new ArgumentException("3/4 tunes can currently be played only as Jazz Waltz.", nameof(style));
        }

        return new TuneForm(
            Id,
            Title,
            Key,
            Bars,
            DefaultTempoBpm,
            HasSeparateEndingForm ? EndingFormBars : null,
            AccompanimentStyleNames.DisplayName(style),
            TimeSignature,
            CodaStartIndex,
            preserveSectionStyles ? SectionStyles : null);
    }

    public TuneForm WithSectionStyle(string section, AccompanimentStyle? style)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        var normalizedSection = section.Trim();
        var updated = SectionStyles.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        if (style is AccompanimentStyle selectedStyle)
        {
            updated[normalizedSection] = selectedStyle;
        }
        else
        {
            updated.Remove(normalizedSection);
        }

        return new TuneForm(
            Id,
            Title,
            Key,
            Bars,
            DefaultTempoBpm,
            HasSeparateEndingForm ? EndingFormBars : null,
            OriginalStyle,
            TimeSignature,
            CodaStartIndex,
            updated);
    }

    public AccompanimentStyle ResolveStyleForSection(string? section)
    {
        if (!string.IsNullOrWhiteSpace(section) &&
            SectionStyles.TryGetValue(section.Trim(), out var sectionStyle))
        {
            return sectionStyle;
        }

        return AccompanimentStyle;
    }

    public AccompanimentStyle ResolveStyleAtBar(int barIndex, bool useEndingForm = false)
    {
        var sourceBars = useEndingForm ? EndingFormBars : Bars;
        if (barIndex < 0 || barIndex >= sourceBars.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(barIndex));
        }

        return ResolveStyleForSection(sourceBars[barIndex].Section);
    }

    public bool UsesStyle(AccompanimentStyle style) =>
        AccompanimentStyle == style || SectionStyles.Values.Contains(style);

    public override string ToString() => Title;

    private static IReadOnlyDictionary<string, AccompanimentStyle> NormalizeSectionStyles(
        IReadOnlyDictionary<string, AccompanimentStyle>? sectionStyles,
        int beatsPerBar)
    {
        var normalized = new Dictionary<string, AccompanimentStyle>(StringComparer.OrdinalIgnoreCase);
        if (sectionStyles is null)
        {
            return normalized;
        }

        foreach (var pair in sectionStyles)
        {
            var section = pair.Key?.Trim();
            if (string.IsNullOrWhiteSpace(section))
            {
                throw new ArgumentException("A section-style assignment must have a rehearsal-mark name.", nameof(sectionStyles));
            }

            if (beatsPerBar == 3 && pair.Value != AccompanimentStyle.JazzWaltz)
            {
                throw new ArgumentException("3/4 sections can currently use only Jazz Waltz.", nameof(sectionStyles));
            }

            if (beatsPerBar == 4 && pair.Value == AccompanimentStyle.JazzWaltz)
            {
                throw new ArgumentException("Jazz Waltz section playback requires a 3/4 tune.", nameof(sectionStyles));
            }

            normalized[section] = pair.Value;
        }

        return normalized;
    }

    private static int ParseBeatsPerBar(string value)
    {
        var normalized = value.Trim();
        return normalized switch
        {
            "3/4" => 3,
            "4/4" => 4,
            _ => throw new ArgumentException($"Time signature {value} is not supported. Jampanion supports 3/4 Jazz Waltz and 4/4 Swing, Jazz Ballad, Bossa Nova, or Latin / Mambo.", nameof(value))
        };
    }

    private static int GetSegmentCount(int barCount) =>
        (barCount + SessionConstants.BarsPerSegment - 1) / SessionConstants.BarsPerSegment;

    private static int FindLoopStartBarIndex(IReadOnlyList<TuneBar> bars)
    {
        var firstMarkedBar = bars
            .Select((bar, index) => (bar, index))
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.bar.Section));
        if (firstMarkedBar.bar is null || !IsLeadInSection(firstMarkedBar.bar.Section))
        {
            return 0;
        }

        for (var index = firstMarkedBar.index + 1; index < bars.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(bars[index].Section) &&
                !IsLeadInSection(bars[index].Section))
            {
                return index;
            }
        }

        return 0;
    }

    private static bool IsLeadInSection(string section) =>
        string.Equals(section.Trim(), "I", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(section.Trim(), "Intro", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(section.Trim(), "V", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(section.Trim(), "Verse", StringComparison.OrdinalIgnoreCase);

    private static ChordSpec CreateTonicChord(string key, ChordSpec fallback)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return fallback;
        }

        var normalized = key.Trim()
            .Replace(" minor", "m", StringComparison.OrdinalIgnoreCase)
            .Replace(" major", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (normalized.EndsWith("-", StringComparison.Ordinal))
        {
            normalized = normalized[..^1] + "m";
        }

        try
        {
            return ChordSymbolParser.Parse(normalized);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static void ValidateBars(
        IReadOnlyList<TuneBar> bars,
        int minimumBars,
        string parameterName,
        int beatsPerBar)
    {
        if (bars.Count < minimumBars)
        {
            throw new ArgumentException(
                $"A tune form must contain at least {minimumBars} bars.",
                parameterName);
        }

        if (bars.Select((bar, index) => bar.Index == index).Any(matches => !matches))
        {
            throw new ArgumentException("Tune bar indices must be consecutive and zero-based.", parameterName);
        }

        if (bars.Any(bar => bar.BeatsPerBar != beatsPerBar))
        {
            throw new ArgumentException("Every bar must use the tune's time signature.", parameterName);
        }
    }

    private static string CreateId(string title) => string.Concat(
        title.ToLowerInvariant().Where(character => char.IsLetterOrDigit(character)));
}
