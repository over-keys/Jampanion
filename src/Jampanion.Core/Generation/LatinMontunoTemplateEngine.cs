using Jampanion.Core.Music;

namespace Jampanion.Core.Generation;

internal enum LatinMontunoTemplateKind
{
    MajorIiVi,
    MinorIiVi,
    HarmonicMinorImV,
    SusToDominant,
    PedalIFlatVii,
    StaticMinorChromatic,
    StaticDominant,
    DominantUnisonFromFive,
    DominantOuterRootFlatSeven,
    DominantOuterThreeNine,
    MajorSixUnisonFromRoot,
    MinorSixUnisonFromRoot,
    MinorSevenUnisonFromRoot,
    TriadFromFive
}

internal enum LatinMontunoTexture
{
    OctaveUnison,
    OuterOctaveInnerDyad,
    UpperStructure,
    GuideToneProgression
}

internal readonly record struct LatinMontunoRhythmEvent(
    int BarIndex,
    int LocalStep,
    int AbsoluteStep,
    int CycleEventIndex,
    bool IsOuter,
    bool TieAcrossBar);

internal sealed record LatinMontunoEvent(
    long Tick,
    ChordSpec Chord,
    LatinMontunoTemplateKind Template,
    LatinMontunoTexture Texture,
    int? OuterDegree,
    IReadOnlyList<int> InnerDegrees,
    bool IsOuter,
    bool IsAnticipation,
    bool TieAcrossBar,
    int CycleEventIndex,
    int? PitchRootPitchClass);

internal static class LatinMontunoTemplateEngine
{
    private static readonly int[] BaseTwoSideSteps = [0, 2, 3, 5, 7];
    private static readonly int[] BaseThreeSideSteps = [1, 3, 5, 7];

    public static IReadOnlyList<LatinMontunoEvent> Build(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        LatinChorusStage stage,
        int seed,
        int previousCellIndex)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        if (bars.Count == 0)
        {
            return Array.Empty<LatinMontunoEvent>();
        }

        var progression = FlattenProgression(bars, followingChord);
        var template = SelectTemplate(progression);
        var rhythm = BuildRhythm(bars, stage, seed, previousCellIndex);
        var events = new List<LatinMontunoEvent>(rhythm.Count);
        foreach (var item in rhythm)
        {
            var bar = bars[item.BarIndex];
            var nextBarChord = item.BarIndex + 1 < bars.Count
                ? bars[item.BarIndex + 1].Chord
                : followingChord;
            var offset = item.LocalStep * SessionConstants.Ppq / 2L;
            var (chord, anticipation) = ResolveEffectiveHarmony(bar, nextBarChord, offset);
            var composition = Compose(template, progression, chord, item);
            var innerDegrees = AddExplicitTensions(chord, composition.InnerDegrees);
            events.Add(new LatinMontunoEvent(
                (long)item.BarIndex * bar.BarTicks + offset,
                chord,
                template,
                composition.Texture,
                composition.OuterDegree,
                innerDegrees,
                item.IsOuter,
                anticipation,
                item.TieAcrossBar,
                item.CycleEventIndex,
                template == LatinMontunoTemplateKind.PedalIFlatVii
                    ? progression[0].BassFoundationPitchClass
                    : null));
        }

        return events;
    }

    private static IReadOnlyList<int> AddExplicitTensions(
        ChordSpec chord,
        IReadOnlyList<int> baseDegrees)
    {
        var degrees = baseDegrees.ToList();
        var symbol = chord.Symbol;
        var hasFlatNine = symbol.Contains("b9", StringComparison.OrdinalIgnoreCase);
        var hasSharpNine = symbol.Contains("#9", StringComparison.OrdinalIgnoreCase);
        var hasSharpEleven = symbol.Contains("#11", StringComparison.OrdinalIgnoreCase);
        var hasFlatThirteen = symbol.Contains("b13", StringComparison.OrdinalIgnoreCase);

        if (hasFlatNine) degrees.Add(1);
        else if (hasSharpNine) degrees.Add(3);
        else if (symbol.Contains('9')) degrees.Add(2);

        if (hasSharpEleven) degrees.Add(6);
        else if (symbol.Contains("11", StringComparison.OrdinalIgnoreCase)) degrees.Add(5);

        if (hasFlatThirteen) degrees.Add(8);
        else if (symbol.Contains("13", StringComparison.OrdinalIgnoreCase)) degrees.Add(9);

        return degrees.Distinct().ToArray();
    }

    internal static int PitchClass(ChordSpec chord, int degree, int? rootPitchClass = null) =>
        Mod12((rootPitchClass ?? chord.RootPitchClass) + degree);

    internal static IReadOnlyList<int> Pattern(LatinMontunoTemplateKind template) => template switch
    {
        LatinMontunoTemplateKind.DominantUnisonFromFive => [7, 10, 2, 4, 7, 10, 2, 4, 7],
        LatinMontunoTemplateKind.MajorSixUnisonFromRoot => [0, 4, 7, 9, 0, 4, 7, 9, 0],
        LatinMontunoTemplateKind.MinorSixUnisonFromRoot => [0, 3, 7, 9, 0, 3, 7, 9, 0],
        LatinMontunoTemplateKind.MinorSevenUnisonFromRoot => [0, 3, 7, 10, 0, 3, 7, 10, 0],
        LatinMontunoTemplateKind.TriadFromFive => [7, 4, 0, 7, 7, 4, 0, 7, 7],
        _ => [0, 4, 7, 10, 0, 4, 7, 10, 0]
    };

    private static IReadOnlyList<LatinMontunoRhythmEvent> BuildRhythm(
        IReadOnlyList<TuneBar> bars,
        LatinChorusStage stage,
        int seed,
        int previousCellIndex)
    {
        var barCount = bars.Count;
        var events = new List<LatinMontunoRhythmEvent>(barCount * 6);
        var mamboVariant = (int)(DeterministicNoise.Unit(seed, 6301) * 2.0);
        var cycleCounts = new int[(barCount + 1) / 2];

        for (var barIndex = 0; barIndex < barCount; barIndex++)
        {
            var globalBarIndex = bars[barIndex].Index;
            var alternateFourBarPhrase = stage == LatinChorusStage.Montuno &&
                globalBarIndex / SessionConstants.BarsPerSegment % 2 == 1 &&
                globalBarIndex % 4 == 2;
            var isTwoSide = barIndex % 2 == 0;
            var steps = isTwoSide
                ? TwoSideSteps(
                    stage,
                    mamboVariant,
                    alternateFourBarPhrase ||
                    stage == LatinChorusStage.Montuno &&
                    previousCellIndex > 0 &&
                    previousCellIndex % 2 == 1 &&
                    barIndex % 4 == 2)
                : BaseThreeSideSteps;
            var cycle = barIndex / 2;
            foreach (var localStep in steps)
            {
                var absoluteStep = barIndex * 8 + localStep;
                var isOuter = isTwoSide
                    ? localStep is 0 or 3 or 4 or 6 or 7
                    : localStep is 3 or 7;
                events.Add(new LatinMontunoRhythmEvent(
                    barIndex,
                    localStep,
                    absoluteStep,
                    cycleCounts[cycle]++,
                    isOuter,
                    isTwoSide && localStep == 7));
            }
        }

        return events;
    }

    private static IReadOnlyList<int> TwoSideSteps(LatinChorusStage stage, int variant, bool phraseVariant)
    {
        if (phraseVariant)
        {
            // A four-bar answer can add one beat-three anchor, but the base
            // 2-3 cell and its 4& tie remain intact.
            return [0, 2, 3, 4, 5, 7];
        }

        if (stage != LatinChorusStage.Mambo)
        {
            return BaseTwoSideSteps;
        }

        // Mambo adds a little density without piling the attacks into beats 1
        // and 2. Keep the 2-3 cell balanced across the two halves of the bar;
        // it must never turn into a four-quarter ostinato or a front-loaded
        // velocity accent.
        return variant == 0
            ? [0, 2, 3, 4, 6, 7]
            : [0, 1, 2, 4, 6, 7];
    }

    private static (ChordSpec Chord, bool Anticipation) ResolveEffectiveHarmony(
        TuneBar bar,
        ChordSpec nextBarChord,
        long offset)
    {
        var nextChange = bar.ChordChanges
            .Select(change => (Tick: (long)change.StartBeat * SessionConstants.Ppq, change.Chord))
            .FirstOrDefault(change => change.Tick > offset && change.Tick - offset <= SessionConstants.Ppq / 2);
        if (nextChange.Chord is not null)
        {
            return (nextChange.Chord, true);
        }

        if (offset >= bar.BarTicks - SessionConstants.Ppq / 2)
        {
            return (nextBarChord, true);
        }

        return (bar.GetChordAtTick(offset), false);
    }

    private static IReadOnlyList<ChordSpec> FlattenProgression(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord)
    {
        // Preserve repeated bars.  Duration and phrase position are part of
        // the context: four bars of one dominant should be eligible for an
        // outer-voice descarga, while a short two-bar statement should keep
        // the compact unison vocabulary.
        return bars
            .SelectMany(bar => bar.ChordChanges.Select(change => change.Chord))
            .Append(followingChord)
            .ToArray();
    }

    private static LatinMontunoTemplateKind SelectTemplate(IReadOnlyList<ChordSpec> progression)
    {
        if (MatchesMajorIiVi(progression)) return LatinMontunoTemplateKind.MajorIiVi;
        if (MatchesMinorIiVi(progression)) return LatinMontunoTemplateKind.MinorIiVi;
        if (MatchesHarmonicMinorImV(progression)) return LatinMontunoTemplateKind.HarmonicMinorImV;
        if (MatchesSusResolution(progression)) return LatinMontunoTemplateKind.SusToDominant;
        if (MatchesPedal(progression)) return LatinMontunoTemplateKind.PedalIFlatVii;

        var first = progression[0];
        if (progression.All(chord => chord.RootPitchClass == first.RootPitchClass))
        {
            if (IsMinor(first)) return LatinMontunoTemplateKind.StaticMinorChromatic;
            if (IsDominant(first))
            {
                return progression.Count >= 4
                    ? progression.Count % 2 == 0
                        ? LatinMontunoTemplateKind.DominantOuterThreeNine
                        : LatinMontunoTemplateKind.DominantOuterRootFlatSeven
                    : LatinMontunoTemplateKind.DominantUnisonFromFive;
            }
        }

        if (IsDominant(first))
        {
            return progression.Count >= 4
                ? progression.Count % 2 == 0
                    ? LatinMontunoTemplateKind.DominantOuterThreeNine
                    : LatinMontunoTemplateKind.DominantOuterRootFlatSeven
                : LatinMontunoTemplateKind.DominantUnisonFromFive;
        }

        if (IsMajorSix(first)) return LatinMontunoTemplateKind.MajorSixUnisonFromRoot;
        if (IsMinorSix(first)) return LatinMontunoTemplateKind.MinorSixUnisonFromRoot;
        if (IsMinorSeven(first)) return LatinMontunoTemplateKind.MinorSevenUnisonFromRoot;
        return LatinMontunoTemplateKind.TriadFromFive;
    }

    internal static string SelectTemplateNameForProbe(IReadOnlyList<ChordSpec> progression)
    {
        ArgumentNullException.ThrowIfNull(progression);
        if (progression.Count == 0)
        {
            throw new ArgumentException("A progression must contain at least one chord.", nameof(progression));
        }

        return SelectTemplate(progression).ToString();
    }

    private static (LatinMontunoTexture Texture, int? OuterDegree, IReadOnlyList<int> InnerDegrees) Compose(
        LatinMontunoTemplateKind template,
        IReadOnlyList<ChordSpec> progression,
        ChordSpec chord,
        LatinMontunoRhythmEvent rhythm)
    {
        if (progression.Count > 2 && template is
            LatinMontunoTemplateKind.MinorSevenUnisonFromRoot or
            LatinMontunoTemplateKind.MajorSixUnisonFromRoot or
            LatinMontunoTemplateKind.MinorSixUnisonFromRoot or
            LatinMontunoTemplateKind.TriadFromFive)
        {
            return ChangingHarmonyComposition(chord, rhythm);
        }

        var index = rhythm.CycleEventIndex;
        switch (template)
        {
            case LatinMontunoTemplateKind.MajorIiVi:
                return GuideToneComposition(progression, chord, rhythm, minor: false);
            case LatinMontunoTemplateKind.MinorIiVi:
                return GuideToneComposition(progression, chord, rhythm, minor: true);
            case LatinMontunoTemplateKind.HarmonicMinorImV:
                return HarmonicMinorComposition(chord, rhythm, index);
            case LatinMontunoTemplateKind.SusToDominant:
                return SusComposition(chord, rhythm);
            case LatinMontunoTemplateKind.PedalIFlatVii:
                return PedalComposition(rhythm, index);
            case LatinMontunoTemplateKind.StaticMinorChromatic:
                return StaticMinorComposition(chord, rhythm, index);
            case LatinMontunoTemplateKind.DominantOuterRootFlatSeven:
                return OuterComposition(rhythm, index, [0, 10], [4, 7]);
            case LatinMontunoTemplateKind.DominantOuterThreeNine:
                return OuterComposition(rhythm, index, [4, 2], [7, 10]);
            case LatinMontunoTemplateKind.DominantUnisonFromFive:
            case LatinMontunoTemplateKind.MajorSixUnisonFromRoot:
            case LatinMontunoTemplateKind.MinorSixUnisonFromRoot:
            case LatinMontunoTemplateKind.MinorSevenUnisonFromRoot:
            case LatinMontunoTemplateKind.TriadFromFive:
                return (
                    LatinMontunoTexture.OctaveUnison,
                    Pattern(template)[index % Pattern(template).Count],
                    Array.Empty<int>());
            default:
                return (LatinMontunoTexture.OctaveUnison, 0, Array.Empty<int>());
        }
    }

    private static (LatinMontunoTexture Texture, int? OuterDegree, IReadOnlyList<int> InnerDegrees) GuideToneComposition(
        IReadOnlyList<ChordSpec> progression,
        ChordSpec chord,
        LatinMontunoRhythmEvent rhythm,
        bool minor)
    {
        if (!rhythm.IsOuter)
        {
            return (LatinMontunoTexture.GuideToneProgression, null, minor ? [3, 10] : [4, 7]);
        }

        var index = IndexOf(progression, chord);
        var outerDegree = IsDominant(chord)
            ? 4
            : IsMinor(chord) || IsHalfDiminished(chord)
                ? 10
                : index >= 0 && index > 0 && IsMajorHarmony(chord)
                    ? rhythm.CycleEventIndex % 3 == 1 ? 9 : 11
                    : 10;
        if (!minor && IsDominant(chord)) outerDegree = 4;
        if (minor && IsDominant(chord)) outerDegree = 4;
        return (LatinMontunoTexture.GuideToneProgression, outerDegree, InnerDegreesForChord(chord));
    }

    private static (LatinMontunoTexture Texture, int? OuterDegree, IReadOnlyList<int> InnerDegrees) ChangingHarmonyComposition(
        ChordSpec chord,
        LatinMontunoRhythmEvent rhythm)
    {
        if (!rhythm.IsOuter)
        {
            return (LatinMontunoTexture.OuterOctaveInnerDyad, null, InnerDegreesForChord(chord));
        }

        var outer = IsDominant(chord) || IsMinor(chord)
            ? rhythm.CycleEventIndex % 2 == 0 ? 0 : 10
            : rhythm.CycleEventIndex % 2 == 0 ? 0 : 9;
        return (LatinMontunoTexture.OuterOctaveInnerDyad, outer, InnerDegreesForChord(chord));
    }

    private static IReadOnlyList<int> InnerDegreesForChord(ChordSpec chord)
    {
        if (IsDominant(chord)) return [4, 7];
        if (IsMinor(chord)) return [3, 7];
        if (IsSuspended(chord)) return [5, 10];
        return [4, 7];
    }

    private static (LatinMontunoTexture Texture, int? OuterDegree, IReadOnlyList<int> InnerDegrees) HarmonicMinorComposition(
        ChordSpec chord,
        LatinMontunoRhythmEvent rhythm,
        int index)
    {
        var dominant = IsDominant(chord);
        var phraseStep = rhythm.AbsoluteStep % 16;
        var outer = dominant
            ? phraseStep == 11 ? 0 : 1
            : phraseStep == 3 ? 8 : 7;
        return (LatinMontunoTexture.GuideToneProgression, outer, dominant ? [4, 10] : [0, 3]);
    }

    private static (LatinMontunoTexture Texture, int? OuterDegree, IReadOnlyList<int> InnerDegrees) SusComposition(
        ChordSpec chord,
        LatinMontunoRhythmEvent rhythm)
    {
        var resolved = !IsSuspended(chord);
        if (rhythm.IsOuter)
        {
            return (LatinMontunoTexture.GuideToneProgression, resolved ? 4 : 5, resolved ? [7, 0] : [10, 2]);
        }

        return (LatinMontunoTexture.GuideToneProgression, null, resolved ? [4, 7, 0] : [10, 2]);
    }

    private static (LatinMontunoTexture Texture, int? OuterDegree, IReadOnlyList<int> InnerDegrees) PedalComposition(
        LatinMontunoRhythmEvent rhythm,
        int index)
    {
        var upper = index % 2 == 0 ? new[] { 0, 4, 7 } : new[] { 10, 2, 5 };
        return (LatinMontunoTexture.UpperStructure, null, upper);
    }

    private static (LatinMontunoTexture Texture, int? OuterDegree, IReadOnlyList<int> InnerDegrees) StaticMinorComposition(
        ChordSpec chord,
        LatinMontunoRhythmEvent rhythm,
        int index)
    {
        if (rhythm.IsOuter)
        {
            var contour = (rhythm.AbsoluteStep % 16) switch
            {
                0 => 0,
                3 => 11,
                7 => 10,
                11 => 9,
                15 => 10,
                _ => 0
            };
            return (LatinMontunoTexture.OuterOctaveInnerDyad, contour, [3, 7]);
        }

        return (LatinMontunoTexture.OuterOctaveInnerDyad, null, [3, 7]);
    }

    private static (LatinMontunoTexture Texture, int? OuterDegree, IReadOnlyList<int> InnerDegrees) OuterComposition(
        LatinMontunoRhythmEvent rhythm,
        int index,
        IReadOnlyList<int> outer,
        IReadOnlyList<int> inner)
    {
        if (rhythm.IsOuter)
        {
            return (LatinMontunoTexture.OuterOctaveInnerDyad, outer[index % outer.Count], inner);
        }

        return (LatinMontunoTexture.OuterOctaveInnerDyad, null, inner);
    }

    private static bool MatchesMajorIiVi(IReadOnlyList<ChordSpec> chords)
    {
        for (var index = 0; index + 2 < chords.Count; index++)
        {
            if (IsMinorSeven(chords[index]) &&
                IsDominant(chords[index + 1]) &&
                IsMajorHarmony(chords[index + 2]) &&
                Mod12(chords[index + 1].RootPitchClass - chords[index].RootPitchClass) == 5 &&
                Mod12(chords[index + 2].RootPitchClass - chords[index + 1].RootPitchClass) == 5)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesMinorIiVi(IReadOnlyList<ChordSpec> chords)
    {
        for (var index = 0; index + 2 < chords.Count; index++)
        {
            if (IsHalfDiminished(chords[index]) &&
                IsDominant(chords[index + 1]) &&
                IsMinor(chords[index + 2]) &&
                Mod12(chords[index + 1].RootPitchClass - chords[index].RootPitchClass) == 5 &&
                Mod12(chords[index + 2].RootPitchClass - chords[index + 1].RootPitchClass) == 5)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesHarmonicMinorImV(IReadOnlyList<ChordSpec> chords) =>
        chords.Count >= 2 &&
        IsMinor(chords[0]) &&
        IsDominant(chords[1]) &&
        Mod12(chords[1].RootPitchClass - chords[0].RootPitchClass) == 7;

    private static bool MatchesSusResolution(IReadOnlyList<ChordSpec> chords) =>
        chords.Count >= 2 && IsSuspended(chords[0]) && IsDominant(chords[1]);

    private static bool MatchesPedal(IReadOnlyList<ChordSpec> chords) =>
        chords.Count >= 2 &&
        chords.Select(chord => chord.BassFoundationPitchClass).Distinct().Count() == 1 &&
        chords.Select(chord => chord.RootPitchClass).Distinct().Count() > 1;

    private static bool IsMajorSix(ChordSpec chord) =>
        chord.Symbol.Contains("6", StringComparison.OrdinalIgnoreCase) && !IsMinor(chord);

    private static bool IsMinorSix(ChordSpec chord) =>
        IsMinor(chord) && chord.Symbol.Contains("6", StringComparison.OrdinalIgnoreCase);

    private static bool IsMinorSeven(ChordSpec chord) =>
        IsMinor(chord) && chord.Symbol.Contains("7", StringComparison.OrdinalIgnoreCase);

    private static bool IsHalfDiminished(ChordSpec chord) =>
        chord.Symbol.Contains("m7b5", StringComparison.OrdinalIgnoreCase) ||
        chord.Symbol.Contains("ø", StringComparison.Ordinal);

    private static bool IsDominant(ChordSpec chord) =>
        chord.Symbol.Contains("7", StringComparison.OrdinalIgnoreCase) &&
        !chord.Symbol.Contains("maj7", StringComparison.OrdinalIgnoreCase) &&
        !IsMinor(chord) &&
        !chord.Symbol.Contains("dim", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuspended(ChordSpec chord) =>
        chord.Symbol.Contains("sus", StringComparison.OrdinalIgnoreCase);

    private static bool IsMinor(ChordSpec chord) =>
        chord.Symbol.Contains('m', StringComparison.OrdinalIgnoreCase) &&
        !chord.Symbol.Contains("maj", StringComparison.OrdinalIgnoreCase);

    private static bool IsMajorHarmony(ChordSpec chord) =>
        !IsMinor(chord) && !IsDominant(chord) && !IsHalfDiminished(chord) && !IsSuspended(chord);

    private static bool SameChord(ChordSpec left, ChordSpec right) =>
        left.Symbol.Equals(right.Symbol, StringComparison.OrdinalIgnoreCase) &&
        left.RootPitchClass == right.RootPitchClass &&
        left.BassFoundationPitchClass == right.BassFoundationPitchClass;

    private static int Mod12(int value) => (value % 12 + 12) % 12;

    private static int IndexOf(IReadOnlyList<ChordSpec> chords, ChordSpec target)
    {
        for (var index = 0; index < chords.Count; index++)
        {
            if (SameChord(chords[index], target)) return index;
        }

        return -1;
    }
}

public static class LatinMontunoTemplateProbe
{
    public readonly record struct PreviewEvent(
        long Tick,
        string ChordSymbol,
        string Template,
        string Texture,
        bool IsOuter,
        int? OuterDegree,
        IReadOnlyList<int> InnerDegrees,
        bool IsAnticipation,
        bool TieAcrossBar,
        int? PitchRootPitchClass);

    public static IReadOnlyList<int> DegreePattern(string id) => id switch
    {
        "dominant_unison_from_5" => LatinMontunoTemplateEngine.Pattern(LatinMontunoTemplateKind.DominantUnisonFromFive),
        "major6_unison_from_root" => LatinMontunoTemplateEngine.Pattern(LatinMontunoTemplateKind.MajorSixUnisonFromRoot),
        "minor6_unison_from_root" => LatinMontunoTemplateEngine.Pattern(LatinMontunoTemplateKind.MinorSixUnisonFromRoot),
        "minor7_unison_from_root" => LatinMontunoTemplateEngine.Pattern(LatinMontunoTemplateKind.MinorSevenUnisonFromRoot),
        "triad_from_5" => LatinMontunoTemplateEngine.Pattern(LatinMontunoTemplateKind.TriadFromFive),
        _ => throw new ArgumentException($"Unknown montuno template: {id}", nameof(id))
    };

    public static string SelectTemplateName(IReadOnlyList<ChordSpec> progression) =>
        LatinMontunoTemplateEngine.SelectTemplateNameForProbe(progression);

    public static IReadOnlyList<PreviewEvent> BuildPreview(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        bool mambo = false,
        int seed = 1,
        int previousCellIndex = 0)
    {
        var stage = mambo ? LatinChorusStage.Mambo : LatinChorusStage.Montuno;
        return LatinMontunoTemplateEngine.Build(bars, followingChord, stage, seed, previousCellIndex)
            .Select(item => new PreviewEvent(
                item.Tick,
                item.Chord.Symbol,
                item.Template.ToString(),
                item.Texture.ToString(),
                item.IsOuter,
                item.OuterDegree,
                item.InnerDegrees,
                item.IsAnticipation,
                item.TieAcrossBar,
                item.PitchRootPitchClass))
            .ToArray();
    }
}
