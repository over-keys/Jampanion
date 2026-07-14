using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal sealed record DrumGenerationResult(
    IReadOnlyList<ScheduledNote> Notes,
    int LastPatternIndex,
    IReadOnlyList<int> PatternIndices,
    int LastFillVariant,
    bool SectionEndedWithFill,
    int LastRidePhraseIndex,
    int LastCompPatternIndex);

internal static class DrumGrooveGenerator
{
    private static readonly long[][] TwoBeatCompPatterns =
    [
        [], [800], [1760], [1440], [320, 1280], [1280], [800, 1760]
    ];

    private static readonly long[][] FourBeatCompPatterns =
    [
        [800], [1280], [320, 1440], [800, 1760], [320, 1280], [960, 1760]
    ];

    private static readonly long[][] HighEnergyCompPatterns =
    [
        [320, 800, 1280], [480, 1280, 1760], [320, 960, 1760],
        [800, 1120, 1440], [320, 800, 1760], [480, 1280]
    ];

    // Each entry is a four-bar ride sentence. Repetition and omission occur
    // inside the sentence; a new segment does not select four unrelated bars.
    private static readonly RidePhrase[] TwoBeatRidePhrases =
    [
        P(100, [0,480,960,1440], [0,480,800,960,1440], [0,800,960,1440], [0,480,960,1440,1760]),
        P(101, [0,480,800,960,1440], [0,800,960,1440], [0,480,960,1440], [0,480,800,960,1760]),
        P(102, [0,800,960,1440], [0,480,960,1440,1760], [0,480,800,960,1440], [0,800,960,1760]),
        P(103, [0,480,960,1440], [0,480,960,1440], [0,800,960,1440,1760], [0,480,800,960,1760])
    ];

    private static readonly RidePhrase[] FourBeatRidePhrases =
    [
        P(200, [0,480,800,960,1440,1760], [0,480,960,1440,1760], [0,480,800,960,1440], [0,480,800,960,1440,1760]),
        P(201, [0,480,960,1440,1760], [0,480,800,960,1440,1760], [0,480,960,1280,1440], [0,320,480,960,1440,1760]),
        P(202, [0,480,800,960,1440], [0,480,960,1440,1760], [0,320,480,800,960,1440], [0,480,800,960,1440,1760]),
        P(203, [0,320,480,960,1440,1760], [0,480,800,960,1440], [0,480,960,1440,1760], [0,480,800,960,1280,1440])
    ];

    private static readonly RidePhrase[] HighEnergyRidePhrases =
    [
        P(300, [0,480,800,960,1440,1760], [0,320,480,960,1440,1760], [0,480,800,960,1280,1440], [0,480,800,960,1440,1600,1760]),
        P(301, [0,320,480,960,1440,1760], [0,480,800,960,1440,1760], [0,480,960,1280,1440,1760], [0,320,480,800,960,1440]),
        P(302, [0,480,800,960,1280,1440], [0,480,960,1440,1600,1760], [0,320,480,800,960,1440], [0,480,800,960,1440,1760])
    ];

    public static DrumGenerationResult Generate(
        RhythmFeel feel,
        IReadOnlyList<BarArrangement> arrangements,
        int previousPatternIndex,
        int previousFillVariant,
        bool previousSectionEndedWithFill,
        int previousRidePhraseIndex,
        int previousCompPatternIndex,
        int seed,
        PerformanceGuidance? performanceGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(arrangements);
        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var notes = new List<ScheduledNote>(arrangements.Count * 16);
        var patterns = new int[arrangements.Count];
        var lastPattern = previousPatternIndex;
        var lastCompPattern = previousCompPatternIndex;
        var lastFillVariant = previousFillVariant;
        var sectionEndedWithFill = false;
        var highEnergy = guidance.IsHighStageActive;
        var ridePhrase = SelectRidePhrase(feel, highEnergy, previousRidePhraseIndex, seed);
        var segmentLength = (long)arrangements.Count * SessionConstants.BarTicks;

        for (var bar = 0; bar < arrangements.Count; bar++)
        {
            var arrangement = arrangements[bar];
            var barStart = (long)bar * SessionConstants.BarTicks;
            if (bar == 0 && previousSectionEndedWithFill)
                Add(notes, barStart, 150, 49, feel == RhythmFeel.TwoBeat ? (byte)60 : (byte)68, SwingTiming.RideDelayTicks, segmentLength);

            var rideOffsets = ridePhrase.Bars[Math.Min(bar, ridePhrase.Bars.Count - 1)];
            AddTimekeeping(notes, barStart, feel, arrangement, rideOffsets, guidance, seed, bar, segmentLength);

            var strongBoundary = arrangement.Boundary >= BoundaryStrength.Section;
            var explicitDrumSetup = arrangement.Responder == ResponderRole.Drums && arrangement.Function == PhraseFunction.Setup;
            if (arrangement.IsSectionEnding && (strongBoundary || explicitDrumSetup))
            {
                var fillProbability = FillProbability(arrangement.Boundary, feel, guidance, previousSectionEndedWithFill);
                if (DeterministicNoise.Unit(seed, bar, 1901) < fillProbability)
                {
                    var variant = SelectFillVariant(previousFillVariant, feel, seed, bar);
                    AddEndingFill(notes, barStart, feel, variant, arrangement.Boundary, segmentLength);
                    lastFillVariant = variant;
                    sectionEndedWithFill = true;
                    patterns[bar] = -20 - variant;
                }
                else
                {
                    AddOptionalSetup(notes, barStart, feel, arrangement.Boundary, seed, bar, segmentLength);
                    patterns[bar] = -2;
                }
                lastPattern = patterns[bar];
                continue;
            }

            if (arrangement.Responder != ResponderRole.Drums)
            {
                patterns[bar] = -1;
                lastPattern = -1;
                continue;
            }

            var source = highEnergy ? HighEnergyCompPatterns : feel == RhythmFeel.TwoBeat ? TwoBeatCompPatterns : FourBeatCompPatterns;
            var motifSourceBar = bar >= 2 && DeterministicNoise.Unit(seed, bar, 1903) < 0.56 ? bar - 2 : -1;
            int index;
            if (motifSourceBar >= 0 && patterns[motifSourceBar] >= 0)
            {
                var original = patterns[motifSourceBar] % source.Length;
                index = DeterministicNoise.Unit(seed, bar, 1904) < 0.55 ? original : (original + 1) % source.Length;
            }
            else
            {
                var candidates = Enumerable.Range(0, source.Length).ToArray();
                if (highEnergy)
                {
                    candidates = candidates
                        .OrderByDescending(i => source[i].Length)
                        .Take(Math.Max(2, source.Length / 2))
                        .ToArray();
                }
                index = candidates[(int)(DeterministicNoise.Unit(seed, bar, 1907) * candidates.Length) % candidates.Length];
                if (index == lastCompPattern && candidates.Length > 1) index = candidates[(Array.IndexOf(candidates, index) + 1) % candidates.Length];
            }

            AddComping(notes, barStart, source[index], feel, arrangement, guidance, seed, bar, segmentLength);
            patterns[bar] = index;
            lastPattern = index;
            lastCompPattern = index;
        }

        return new DrumGenerationResult(notes, lastPattern, patterns, lastFillVariant, sectionEndedWithFill, ridePhrase.Index, lastCompPattern);
    }

    private static RidePhrase SelectRidePhrase(RhythmFeel feel, bool highEnergy, int previousIndex, int seed)
    {
        var source = highEnergy ? HighEnergyRidePhrases : feel == RhythmFeel.TwoBeat ? TwoBeatRidePhrases : FourBeatRidePhrases;
        var selected = (int)(DeterministicNoise.Unit(seed, 1881) * source.Length) % source.Length;
        if (source[selected].Index == previousIndex && source.Length > 1) selected = (selected + 1) % source.Length;
        return source[selected];
    }

    private static void AddTimekeeping(
        List<ScheduledNote> notes, long barStart, RhythmFeel feel, BarArrangement arrangement,
        IReadOnlyList<long> rideOffsets, PerformanceGuidance guidance, int seed, int bar, long segmentLength)
    {
        var high = guidance.IsHighStageActive;
        for (var i = 0; i < rideOffsets.Count; i++)
        {
            var offset = rideOffsets[i];
            var baseVelocity = RideBaseVelocity(feel, offset);
            var lift = (high ? 4 : 0) + arrangement.DynamicLift;
            var crashProbability = high ? 0.28 : 0.18;
            var useCrash = high && arrangement.InvitesDrumStatement && arrangement.Function is PhraseFunction.Build or PhraseFunction.Setup && offset == 0 && DeterministicNoise.Unit(seed, bar, i, 1920) < crashProbability;
            var velocity = (byte)Math.Clamp(baseVelocity + lift + Math.Round(DeterministicNoise.Unit(seed, bar, i, 1921) * 4 - 2), 40, feel == RhythmFeel.TwoBeat ? 78 : 90);
            Add(notes, barStart + offset, feel == RhythmFeel.TwoBeat ? 70 : 55, useCrash ? (byte)49 : (byte)51, velocity, SwingTiming.RideDelayTicks, segmentLength);
        }

        AddKickGrammar(notes, barStart, feel, high, seed, bar, segmentLength);
        // Pedal hi-hat marks 2 and 4 but should not eclipse the ride cymbal, which is
        // the primary time voice in straight-ahead swing.
        var hatLift = (high ? 3 : 0) + Math.Clamp(arrangement.DynamicLift, -2, 3);
        var hatVariation = (int)Math.Round(DeterministicNoise.Unit(seed, bar, 1922) * 2 - 1);
        Add(notes, barStart + SessionConstants.Ppq, 55, 44, (byte)((feel == RhythmFeel.TwoBeat ? 54 : 60) + hatLift + hatVariation), SwingTiming.HiHatDelayTicks, segmentLength);
        Add(notes, barStart + 3L * SessionConstants.Ppq, 55, 44, (byte)((feel == RhythmFeel.TwoBeat ? 58 : 64) + hatLift - hatVariation), SwingTiming.HiHatDelayTicks, segmentLength);

        if (high &&
            arrangement.InvitesDrumStatement &&
            arrangement.Function is PhraseFunction.Build or PhraseFunction.Setup &&
            DeterministicNoise.Unit(seed, bar, 1925) < 0.68)
        {
            var offset = DeterministicNoise.Unit(seed, bar, 1926) < 0.54 ? 1280L : 800L;
            var note = feel == RhythmFeel.TwoBeat ? (byte)37 : (byte)38;
            Add(notes, barStart + offset, 65, note, (byte)(feel == RhythmFeel.TwoBeat ? 47 : 55), 5, segmentLength);
        }
    }

    private static int RideBaseVelocity(RhythmFeel feel, long offset)
    {
        var beat = (int)(offset / SessionConstants.Ppq);
        var position = offset % SessionConstants.Ppq;
        if (position == 0)
        {
            return feel == RhythmFeel.TwoBeat
                ? beat switch { 0 => 54, 1 => 58, 2 => 53, _ => 59 }
                : beat switch { 0 => 60, 1 => 64, 2 => 59, _ => 65 };
        }

        // Skip notes lift into the following quarter note. They should be clear but
        // lighter than the quarter-note cymbal pulse.
        var leadsToTwoOrFour = beat is 0 or 2;
        return feel == RhythmFeel.TwoBeat
            ? leadsToTwoOrFour ? 48 : 45
            : leadsToTwoOrFour ? 52 : 48;
    }

    private static void AddKickGrammar(List<ScheduledNote> notes, long barStart, RhythmFeel feel, bool highStage, int seed, int bar, long segmentLength)
    {
        var selector = DeterministicNoise.Unit(seed, bar / 2, 1923);
        if (feel == RhythmFeel.TwoBeat)
        {
            if (selector < 0.78)
            {
                var kickLift = highStage ? 2 : 0;
                Add(notes, barStart, 55, 36, (byte)(18 + kickLift), SwingTiming.KickDelayTicks, segmentLength);
                Add(notes, barStart + 2L * SessionConstants.Ppq, 55, 36, (byte)(16 + kickLift), SwingTiming.KickDelayTicks, segmentLength);
            }
            else if (selector < 0.86)
            {
                for (var beat = 0; beat < 4; beat++) Add(notes, barStart + beat * SessionConstants.Ppq, 55, 36, (byte)(beat % 2 == 0 ? 17 : 14), SwingTiming.KickDelayTicks, segmentLength);
            }
        }
        else
        {
            var omitted = selector < 0.82 ? (int)(DeterministicNoise.Unit(seed, bar, 1924) * 4) : -1;
            for (var beat = 0; beat < 4; beat++)
            {
                if (beat == omitted) continue;
                var kickLift = highStage ? 2 : 0;
                Add(notes, barStart + beat * SessionConstants.Ppq, 55, 36, (byte)((beat % 2 == 0 ? 18 : 15) + kickLift), SwingTiming.KickDelayTicks, segmentLength);
            }
        }
    }

    private static void AddComping(
        List<ScheduledNote> notes, long barStart, IReadOnlyList<long> offsets, RhythmFeel feel,
        BarArrangement arrangement, PerformanceGuidance guidance, int seed, int bar, long segmentLength)
    {
        foreach (var offset in offsets)
        {
            var offbeat = offset % SessionConstants.Ppq != 0;
            var bombProbability = guidance.HighStage ? 0.18 : 0.14;
            var bomb = feel == RhythmFeel.FourBeat && offbeat && guidance.IsHighStageActive && DeterministicNoise.Unit(seed, bar, (int)offset, 1908) < bombProbability;
            var note = bomb ? (byte)36 : feel == RhythmFeel.TwoBeat && DeterministicNoise.Unit(seed, bar, (int)offset, 1909) < 0.16 ? (byte)37 : (byte)38;
            var minimum = bomb ? 44 : feel == RhythmFeel.TwoBeat ? 28 : 35;
            var compLift = guidance.HighStage ? 3 : 0;
            var velocity = (byte)Math.Clamp(minimum + arrangement.DynamicLift + compLift + Math.Round(DeterministicNoise.Unit(seed, bar, (int)offset, 1911) * 9), 1, 127);
            Add(notes, barStart + offset, 60, note, velocity, 5, segmentLength);
        }
    }

    private static double FillProbability(BoundaryStrength boundary, RhythmFeel feel, PerformanceGuidance guidance, bool previousFill)
    {
        var probability = boundary switch { BoundaryStrength.Chorus => 0.18, BoundaryStrength.Section => 0.09, BoundaryStrength.Phrase => 0.018, _ => 0.0 };
        if (feel == RhythmFeel.TwoBeat) probability *= 0.72;
        if (guidance.HighStage) probability += boundary >= BoundaryStrength.Section ? 0.10 : 0.02;
        if (guidance.Intensity == PerformanceIntensity.Low) probability *= 0.65;
        if (previousFill) probability *= 0.45;
        return Math.Clamp(probability, 0, 0.42);
    }

    private static int SelectFillVariant(int previous, RhythmFeel feel, int seed, int bar)
    {
        var count = feel == RhythmFeel.TwoBeat ? 4 : 6;
        var selected = (int)(DeterministicNoise.Unit(seed, bar, 1931) * count) % count;
        return selected == previous ? (selected + 1) % count : selected;
    }

    private static void AddEndingFill(List<ScheduledNote> notes, long barStart, RhythmFeel feel, int variant, BoundaryStrength boundary, long segmentLength)
    {
        var full = boundary >= BoundaryStrength.Section;
        var fill = feel == RhythmFeel.TwoBeat
            ? variant switch
            {
                0 => new[] { H(1760, 37, 47) },
                1 => new[] { H(1440, 37, 39), H(1760, 38, 51) },
                2 => full ? new[] { H(1280, 37, 38), H(1760, 38, 50) } : new[] { H(1760, 37, 45) },
                _ => new[] { H(1600, 37, 42), H(1760, 38, 52) }
            }
            : variant switch
            {
                0 => new[] { H(1440, 38, 47), H(1600, 38, 55), H(1760, 38, 64) },
                1 => full ? new[] { H(1280, 38, 46), H(1440, 45, 52), H(1600, 47, 58), H(1760, 38, 65) } : new[] { H(1600, 38, 52), H(1760, 38, 62) },
                2 => full ? new[] { H(960, 38, 43), H(1120, 38, 47), H(1280, 45, 52), H(1600, 47, 59), H(1760, 38, 66) } : new[] { H(1440, 38, 49), H(1760, 38, 63) },
                3 => new[] { H(1280, 45, 48), H(1440, 45, 52), H(1600, 47, 58), H(1760, 50, 66) },
                4 => new[] { H(1760, 38, 58) },
                _ => new[] { H(1280, 47, 48), H(1600, 45, 57), H(1760, 38, 66) }
            };
        foreach (var hit in fill) Add(notes, barStart + hit.Offset, hit.Offset == 1760 ? 150 : 60, hit.Note, hit.Velocity, 5, segmentLength);
    }

    private static void AddOptionalSetup(List<ScheduledNote> notes, long barStart, RhythmFeel feel, BoundaryStrength boundary, int seed, int bar, long segmentLength)
    {
        var probability = boundary switch { BoundaryStrength.Chorus => 0.58, BoundaryStrength.Section => 0.42, _ => feel == RhythmFeel.TwoBeat ? 0.18 : 0.30 };
        if (DeterministicNoise.Unit(seed, bar, 1937) < probability)
            Add(notes, barStart + 1760, 120, 38, feel == RhythmFeel.TwoBeat ? (byte)38 : (byte)47, 5, segmentLength);
    }

    private static RidePhrase P(int index, params long[][] bars) => new(index, bars);
    private static FillHit H(long offset, byte note, byte velocity) => new(offset, note, velocity);
    private static void Add(List<ScheduledNote> notes, long gridStart, long duration, byte note, byte velocity, long delay, long segmentLength)
    {
        var start = SwingTiming.DrumStart(gridStart, delay);
        if (start >= segmentLength) return;
        notes.Add(new ScheduledNote(start, SwingTiming.ClampDuration(start, duration, segmentLength), note, velocity, SessionConstants.DrumsChannel));
    }

    private sealed record RidePhrase(int Index, IReadOnlyList<long[]> Bars);
    private readonly record struct FillHit(long Offset, byte Note, byte Velocity);
}
