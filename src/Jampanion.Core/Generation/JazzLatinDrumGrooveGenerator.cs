using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

/// <summary>
/// Drum-set-centred jazz-Latin groove.
///
/// The supplied reference uses a two-bar layered ride line, pedal hi-hat on 2 and 4,
/// kick on 1 and 2&, and side-stick on 2. Tom responses keep that vocabulary but
/// appear only in selected two-bar phrases instead of becoming a mandatory bar marker.
/// </summary>
internal static class JazzLatinDrumGrooveGenerator
{
    // Two-bar ride-bell and ride-cymbal sentences transcribed from the supplied MIDI.
    // The alternatives vary the late-bar pickup while preserving the core pulse.
    private static readonly long[][][] RideSentences =
    [
        [
            [0, 480, 960, 1200, 1680],
            [240, 480, 960, 1200, 1680]
        ],
        [
            [0, 480, 960, 1200, 1440, 1680],
            [240, 480, 960, 1440, 1680]
        ],
        [
            [0, 480, 960, 1200, 1680],
            [240, 480, 960, 1440, 1680]
        ]
    ];

    public static DrumGenerationResult Generate(
        IReadOnlyList<BarArrangement> arrangements,
        int previousPatternIndex,
        int previousFillVariant,
        bool previousSectionEndedWithFill,
        int previousCompPatternIndex,
        int seed,
        LatinChorusStage stage,
        PerformanceGuidance? performanceGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(arrangements);
        if (arrangements.Count == 0)
        {
            throw new ArgumentException(
                "At least one bar is required.",
                nameof(arrangements));
        }

        var guidance =
            performanceGuidance ?? PerformanceGuidance.Neutral;
        var segmentLength =
            (long)arrangements.Count * SessionConstants.BarTicks;
        var notes =
            new List<ScheduledNote>(arrangements.Count * 24);
        var patterns = new int[arrangements.Count];
        var stageLift = stage switch
        {
            LatinChorusStage.Opening or
                LatinChorusStage.HeadOut => -3,
            LatinChorusStage.Ponchando => -1,
            LatinChorusStage.Mambo => 4,
            _ => 1
        };
        var interactionLift =
            guidance.HighStage ? 2 : 0;
        var sentenceIndex = SelectRideSentence(
            seed,
            stage,
            previousPatternIndex,
            previousCompPatternIndex);
        var endedWithFill = false;
        var lastFillVariant = previousFillVariant;

        for (var barIndex = 0;
             barIndex < arrangements.Count;
             barIndex++)
        {
            var arrangement = arrangements[barIndex];
            var barStart =
                (long)barIndex * SessionConstants.BarTicks;
            var parity = barIndex % 2;
            var strongBoundary =
                arrangement.IsSectionEnding &&
                arrangement.Boundary >=
                    BoundaryStrength.Section;
            var fill = strongBoundary &&
                !previousSectionEndedWithFill &&
                (stage == LatinChorusStage.Mambo ||
                 arrangement.IsTransitionLeadIn ||
                 arrangement.Function ==
                    PhraseFunction.Build);

            AddRide(
                notes,
                RideSentences[sentenceIndex][parity],
                barStart,
                barIndex,
                stage,
                stageLift,
                interactionLift,
                arrangement,
                seed,
                segmentLength);
            AddHiHat(
                notes,
                barStart,
                stageLift,
                arrangement,
                segmentLength);
            AddKick(
                notes,
                barStart,
                stageLift,
                interactionLift,
                arrangement,
                segmentLength);
            AddCrossStick(
                notes,
                barStart,
                fill,
                stageLift,
                interactionLift,
                arrangement,
                segmentLength);

            if (fill)
            {
                lastFillVariant =
                    (previousFillVariant + 1 +
                     barIndex + sentenceIndex) % 3;
                AddPhraseFill(
                    notes,
                    barStart,
                    lastFillVariant,
                    stageLift,
                    interactionLift,
                    segmentLength);
                endedWithFill = true;
            }
            else
            {
                AddTomConversation(
                    notes,
                    barStart,
                    parity,
                    stage,
                    stageLift,
                    interactionLift,
                    arrangement,
                    seed,
                    barIndex,
                    segmentLength);
            }

            if (stage == LatinChorusStage.Mambo &&
                arrangement.Function ==
                    PhraseFunction.Build &&
                DeterministicNoise.Unit(
                    seed,
                    barIndex,
                    8803) < 0.38)
            {
                Add(
                    notes,
                    barStart + SessionConstants.Ppq / 2,
                    70,
                    38,
                    (byte)Math.Clamp(
                        43 + stageLift +
                        interactionLift,
                        36,
                        62),
                    2,
                    segmentLength);
            }

            patterns[barIndex] =
                8800 + sentenceIndex * 10 + parity;
        }

        return new DrumGenerationResult(
            notes,
            patterns[^1],
            patterns,
            lastFillVariant,
            SectionEndedWithFill: endedWithFill,
            LastRidePhraseIndex: sentenceIndex,
            LastCompPatternIndex: patterns[^1]);
    }

    private static int SelectRideSentence(
        int seed,
        LatinChorusStage stage,
        int previousPatternIndex,
        int previousCompPatternIndex)
    {
        var selected = (int)(
            DeterministicNoise.Unit(
                seed,
                (int)stage,
                8801) * RideSentences.Length) %
            RideSentences.Length;
        var previous = previousPatternIndex >= 8800
            ? (previousPatternIndex - 8800) / 10
            : previousCompPatternIndex >= 8800
                ? (previousCompPatternIndex - 8800) / 10
                : -1;
        if (RideSentences.Length > 1 &&
            selected == previous)
        {
            selected =
                (selected + 1) % RideSentences.Length;
        }

        return selected;
    }

    private static void AddRide(
        List<ScheduledNote> notes,
        IReadOnlyList<long> offsets,
        long barStart,
        int barIndex,
        LatinChorusStage stage,
        int stageLift,
        int interactionLift,
        BarArrangement arrangement,
        int seed,
        long segmentLength)
    {
        for (var index = 0; index < offsets.Count; index++)
        {
            var offset = offsets[index];
            var accented = offset is 480 or 1680;
            var phraseLift = stage switch
            {
                LatinChorusStage.Mambo => 2,
                LatinChorusStage.Montuno => 1,
                _ => 0
            };
            var contour = (int)Math.Round(
                (DeterministicNoise.Unit(seed, barIndex, index, 8805) - 0.5d) * 2d);
            var lateBeatReduction = offset == 1440
                ? -6
                : offset is 240 or 1200 ? -2 : 0;
            var commonLift =
                stageLift / 3 +
                interactionLift +
                arrangement.DynamicLift / 5 +
                phraseLift +
                contour;
            var bellVelocity = (byte)Math.Clamp(
                (accented ? 58 : 49) + lateBeatReduction + commonLift,
                34,
                68);
            var rideVelocity = (byte)Math.Clamp(
                bellVelocity - 10,
                24,
                58);
            var duration = accented ? 260L : 200L;

            Add(
                notes,
                barStart + offset,
                duration,
                53,
                bellVelocity,
                1 + index % 2,
                segmentLength);
            Add(
                notes,
                barStart + offset,
                duration,
                59,
                rideVelocity,
                2 + index % 2,
                segmentLength);
        }
    }

    private static void AddHiHat(
        List<ScheduledNote> notes,
        long barStart,
        int stageLift,
        BarArrangement arrangement,
        long segmentLength)
    {
        foreach (var offset in new[]
                 {
                     SessionConstants.Ppq,
                     3L * SessionConstants.Ppq
                 })
        {
            Add(
                notes,
                barStart + offset,
                100,
                44,
                (byte)Math.Clamp(
                    43 + stageLift / 3 +
                    arrangement.DynamicLift / 5,
                    35,
                    54),
                0,
                segmentLength);
        }
    }

    private static void AddKick(
        List<ScheduledNote> notes,
        long barStart,
        int stageLift,
        int interactionLift,
        BarArrangement arrangement,
        long segmentLength)
    {
        Add(
            notes,
            barStart,
            SessionConstants.Ppq / 2,
            35,
            (byte)Math.Clamp(
                60 + stageLift / 2 +
                interactionLift +
                arrangement.DynamicLift / 4,
                48,
                76),
            0,
            segmentLength);
        Add(
            notes,
            barStart +
                3L * SessionConstants.Ppq / 2,
            SessionConstants.Ppq / 2,
            35,
            (byte)Math.Clamp(
                58 + stageLift / 2 +
                interactionLift +
                arrangement.DynamicLift / 4,
                46,
                74),
            1,
            segmentLength);
    }

    private static void AddCrossStick(
        List<ScheduledNote> notes,
        long barStart,
        bool fill,
        int stageLift,
        int interactionLift,
        BarArrangement arrangement,
        long segmentLength)
    {
        if (fill)
        {
            return;
        }

        Add(
            notes,
            barStart + SessionConstants.Ppq,
            SessionConstants.Ppq / 2,
            37,
            (byte)Math.Clamp(
                50 + stageLift +
                interactionLift +
                arrangement.DynamicLift / 4,
                40,
                67),
            2,
            segmentLength);
    }

    private static void AddTomConversation(
        List<ScheduledNote> notes,
        long barStart,
        int parity,
        LatinChorusStage stage,
        int stageLift,
        int interactionLift,
        BarArrangement arrangement,
        int seed,
        int barIndex,
        long segmentLength)
    {
        var sparse =
            stage is LatinChorusStage.Opening or LatinChorusStage.HeadOut ||
            arrangement.Function == PhraseFunction.Space;
        var probability = stage switch
        {
            LatinChorusStage.Opening or LatinChorusStage.HeadOut => 0.14d,
            LatinChorusStage.Ponchando => 0.24d,
            LatinChorusStage.Montuno => 0.38d,
            LatinChorusStage.Mambo => 0.55d,
            _ => 0.30d
        };
        if (arrangement.Function == PhraseFunction.Build)
        {
            probability += 0.25d;
        }
        else if (arrangement.Function == PhraseFunction.Release)
        {
            probability += 0.10d;
        }
        if (sparse)
        {
            probability *= 0.45d;
        }

        probability = Math.Clamp(
            probability + arrangement.DynamicLift / 40d + interactionLift / 50d,
            0.05d,
            0.85d);
        if (DeterministicNoise.Unit(seed, barIndex / 2, 8811) >= probability)
        {
            return;
        }

        if (parity == 0)
        {
            Add(
                notes,
                barStart + 7L * SessionConstants.Ppq / 2,
                SessionConstants.Ppq / 2,
                45,
                (byte)Math.Clamp(
                    47 + stageLift +
                    interactionLift +
                    arrangement.DynamicLift / 5,
                    34,
                    60),
                2,
                segmentLength);
            return;
        }

        Add(
            notes,
            barStart + 3L * SessionConstants.Ppq / 2,
            SessionConstants.Ppq / 2,
            43,
            (byte)Math.Clamp(
                40 + stageLift + interactionLift,
                32,
                58),
            2,
            segmentLength);
        Add(
            notes,
            barStart + 2L * SessionConstants.Ppq,
            SessionConstants.Ppq / 2,
            43,
            (byte)Math.Clamp(
                43 + stageLift + interactionLift,
                34,
                60),
            2,
            segmentLength);
        Add(
            notes,
            barStart + 3L * SessionConstants.Ppq,
            SessionConstants.Ppq / 2,
            45,
            (byte)Math.Clamp(
                50 + stageLift + interactionLift,
                38,
                66),
            2,
            segmentLength);
        Add(
            notes,
            barStart + 7L * SessionConstants.Ppq / 2,
            SessionConstants.Ppq / 2,
            45,
            (byte)Math.Clamp(
                47 + stageLift + interactionLift,
                36,
                64),
            2,
            segmentLength);
    }

    private static void AddPhraseFill(
        List<ScheduledNote> notes,
        long barStart,
        int variant,
        int stageLift,
        int interactionLift,
        long segmentLength)
    {
        var pattern = variant switch
        {
            0 => new[]
            {
                (Offset: 1440L, Note: (byte)43),
                (Offset: 1680L, Note: (byte)45)
            },
            1 => new[]
            {
                (Offset: 1200L, Note: (byte)38),
                (Offset: 1440L, Note: (byte)43),
                (Offset: 1680L, Note: (byte)45)
            },
            _ => new[]
            {
                (Offset: 1440L, Note: (byte)45),
                (Offset: 1560L, Note: (byte)43),
                (Offset: 1680L, Note: (byte)47)
            }
        };

        for (var index = 0;
             index < pattern.Length;
             index++)
        {
            Add(
                notes,
                barStart + pattern[index].Offset,
                90,
                pattern[index].Note,
                (byte)Math.Clamp(
                    48 + stageLift +
                    interactionLift +
                    index * 4,
                    39,
                    72),
                2,
                segmentLength);
        }
    }

    private static void Add(
        List<ScheduledNote> notes,
        long gridTick,
        long duration,
        byte note,
        byte velocity,
        long delay,
        long segmentLength)
    {
        var start = gridTick + delay;
        if (start >= segmentLength)
        {
            return;
        }

        notes.Add(new ScheduledNote(
            start,
            Math.Min(duration, segmentLength - start),
            note,
            velocity,
            SessionConstants.DrumsChannel));
    }
}
