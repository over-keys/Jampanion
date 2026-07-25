using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BalladDrumGrooveGenerator
{
    // GM1 has no portable brush kit. These notes reproduce the musical roles
    // of a brush ballad with the standard kit: quiet ride pulse, side-stick
    // taps, pedal hi-hat, feathered bass drum, and sparse phrase cymbals.
    private static readonly long[] QuarterPulse = [0, 480, 960, 1440];
    private static readonly long[] BrushTapOffsets = [320, 800, 1280, 1760];

    public static DrumGenerationResult Generate(
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<BalladChorusStage> stages,
        int previousPatternIndex,
        int previousFillVariant,
        bool previousSectionEndedWithFill,
        int previousRidePhraseIndex,
        int previousCompPatternIndex,
        int seed,
        PerformanceGuidance? performanceGuidance = null,
        TimeFeelProfile? timeFeel = null)
    {
        ArgumentNullException.ThrowIfNull(arrangements);
        ArgumentNullException.ThrowIfNull(stages);
        if (arrangements.Count != stages.Count)
        {
            throw new ArgumentException("Arrangements and ballad stages must have the same length.");
        }

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var timing = timeFeel ?? TimeFeelProfile.Resolve(AccompanimentStyle.JazzBallad, 64);
        var segmentLength = (long)arrangements.Count * SessionConstants.BarTicks;
        var notes = new List<ScheduledNote>(arrangements.Count * 20);
        var patterns = new int[arrangements.Count];
        var endedWithFill = false;
        var lastFill = previousFillVariant;
        var carrySectionAccent = previousSectionEndedWithFill;

        for (var barIndex = 0; barIndex < arrangements.Count; barIndex++)
        {
            var arrangement = arrangements[barIndex];
            var stage = stages[barIndex];
            var barStart = (long)barIndex * SessionConstants.BarTicks;
            var handoffLift = arrangement.IsTransitionLeadIn ? -2 : 0;
            var stageLift = stage switch
            {
                BalladChorusStage.Theme or BalladChorusStage.HeadOut => -4,
                BalladChorusStage.QuietSolo => -2,
                BalladChorusStage.MovingTwoFeel => 0,
                BalladChorusStage.FourFeel => 2,
                _ => 0
            };
            var interactionLift = guidance.HighStage ? 2 : 0;
            var strongBoundary = arrangement.IsSectionEnding &&
                arrangement.Boundary >= BoundaryStrength.Section;

            if (carrySectionAccent && !arrangement.IsHeadOutEntry)
            {
                Add(notes, barStart, 240, 49,
                    (byte)Math.Clamp(36 + stageLift + interactionLift, 30, 48),
                    TimeFeelRole.Ride, timing, segmentLength);
            }
            carrySectionAccent = false;

            // A quiet quarter-note ride gives every GM synth a continuous
            // substitute for the brush circle. Avoid the conventional
            // ding-ding-da-ding figure, which makes the ballad sound like a
            // slowed-down swing groove.
            var pulseVelocity = stage switch
            {
                BalladChorusStage.Theme or BalladChorusStage.HeadOut => 25,
                BalladChorusStage.QuietSolo => 27,
                BalladChorusStage.MovingTwoFeel => 29,
                BalladChorusStage.FourFeel => 32,
                _ => 27
            };
            foreach (var offset in QuarterPulse)
            {
                var downbeatShape = offset is 0 or 960 ? 1 : 0;
                Add(notes, barStart + offset, 90, 51,
                    (byte)Math.Clamp(
                        pulseVelocity + stageLift / 2 + interactionLift +
                        handoffLift + downbeatShape,
                        20,
                        42),
                    TimeFeelRole.Ride, timing, segmentLength);
            }

            // Pedal hi-hat keeps the 2/4 frame without becoming a backbeat.
            Add(notes, barStart + SessionConstants.Ppq, 80, 44,
                (byte)Math.Clamp(30 + stageLift + interactionLift + handoffLift, 24, 42),
                TimeFeelRole.HiHat, timing, segmentLength);
            Add(notes, barStart + 3L * SessionConstants.Ppq, 80, 44,
                (byte)Math.Clamp(32 + stageLift + interactionLift + handoffLift, 25, 44),
                TimeFeelRole.HiHat, timing, segmentLength);

            // Feather the bass drum on all four beats, as in the reference
            // performance. The very low GM velocity makes this felt rather than
            // heard and avoids the empty 1/3-only pulse of the previous version.
            foreach (var offset in QuarterPulse)
            {
                var anchor = offset is 0 or 960 ? 1 : 0;
                Add(notes, barStart + offset, 90, 36,
                    (byte)Math.Clamp(
                        16 + stageLift / 2 + interactionLift + anchor,
                        11,
                        24),
                    TimeFeelRole.Kick, timing, segmentLength);
            }

            // One quiet side-stick answer stands in for a brush tap. It lives on
            // the swing-triplet grid rather than on a fixed 2/4 backbeat.
            var tapProbability = stage switch
            {
                BalladChorusStage.Theme or BalladChorusStage.HeadOut => 0.72,
                BalladChorusStage.QuietSolo => 0.84,
                BalladChorusStage.MovingTwoFeel => 0.91,
                BalladChorusStage.FourFeel => 0.94,
                _ => 0.80
            };
            tapProbability += arrangement.Function switch
            {
                PhraseFunction.Build or PhraseFunction.Setup => 0.05,
                PhraseFunction.Space => -0.18,
                PhraseFunction.Release => -0.08,
                _ => 0
            };
            var hasPhraseFill = strongBoundary &&
                (stage is BalladChorusStage.MovingTwoFeel or BalladChorusStage.FourFeel ||
                    arrangement.IsTransitionLeadIn) &&
                !previousSectionEndedWithFill;
            if (!hasPhraseFill &&
                DeterministicNoise.Unit(seed, barIndex, 7220) <
                    Math.Clamp(tapProbability, 0.42, 0.98))
            {
                var selector = DeterministicNoise.Unit(seed, barIndex, 7221);
                var tapOffset = selector switch
                {
                    < 0.36 => BrushTapOffsets[1],
                    < 0.62 => BrushTapOffsets[2],
                    < 0.86 => BrushTapOffsets[3],
                    _ => BrushTapOffsets[0]
                };
                Add(notes, barStart + tapOffset, 110, 37,
                    (byte)Math.Clamp(
                        29 + stageLift + interactionLift + arrangement.DynamicLift / 5,
                        23,
                        40),
                    TimeFeelRole.DrumComp, timing, segmentLength);
            }

            if (strongBoundary)
            {
                // A low open hi-hat on beat 4 marks the eight-bar breath without
                // turning every phrase ending into a tom fill.
                Add(notes, barStart + 3L * SessionConstants.Ppq, 220, 46,
                    (byte)Math.Clamp(31 + stageLift + interactionLift + handoffLift, 25, 44),
                    TimeFeelRole.HiHat, timing, segmentLength);
                carrySectionAccent = hasPhraseFill;
            }

            if (hasPhraseFill)
            {
                // Portable GM fill: closed hat plus side stick. Avoid acoustic
                // snare and tom substitutions, which immediately sound like a
                // stick kit rather than a brush ballad.
                Add(notes, barStart + 1600, 90, 42,
                    (byte)Math.Clamp(30 + stageLift + handoffLift, 24, 42),
                    TimeFeelRole.HiHat, timing, segmentLength);
                Add(notes, barStart + 1760, 120, 37,
                    (byte)Math.Clamp(34 + stageLift + handoffLift, 27, 46),
                    TimeFeelRole.DrumComp, timing, segmentLength);
                endedWithFill = true;
                lastFill = (lastFill + 1 + 4) % 4;
            }

            patterns[barIndex] = 900 + (int)stage * 4 + barIndex % 4;
        }

        return new DrumGenerationResult(
            notes,
            patterns[^1],
            patterns,
            lastFill,
            endedWithFill,
            LastRidePhraseIndex: stages[^1] == BalladChorusStage.FourFeel ? 901 : -1,
            LastCompPatternIndex: patterns[^1]);
    }

    private static void Add(
        List<ScheduledNote> notes,
        long gridTick,
        long duration,
        byte note,
        byte velocity,
        TimeFeelRole role,
        TimeFeelProfile timing,
        long segmentLength)
    {
        var start = timing.Place(gridTick, role);
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
