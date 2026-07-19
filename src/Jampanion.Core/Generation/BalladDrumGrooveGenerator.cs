using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BalladDrumGrooveGenerator
{
    private static readonly long[] NormalRide = [0, 480, 800, 960, 1440, 1760];
    private static readonly long[] MovingRide = [0, 800, 960, 1440];
    private static readonly long[] SoftRide = [0, 480, 800, 960, 1440, 1760];

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
        var notes = new List<ScheduledNote>(arrangements.Count * 22);
        var patterns = new int[arrangements.Count];
        var endedWithFill = false;
        var lastFill = previousFillVariant;

        for (var barIndex = 0; barIndex < arrangements.Count; barIndex++)
        {
            var arrangement = arrangements[barIndex];
            var stage = stages[barIndex];
            var barStart = (long)barIndex * SessionConstants.BarTicks;
            var stageLift = stage switch
            {
                BalladChorusStage.Theme or BalladChorusStage.HeadOut => -4,
                BalladChorusStage.QuietSolo => -2,
                BalladChorusStage.MovingTwoFeel => 0,
                BalladChorusStage.FourFeel => 2,
                _ => 0
            };
            var interactionLift = guidance.HighStage ? 2 : 0;

            Add(notes, barStart + SessionConstants.Ppq, 65, 44,
                (byte)Math.Clamp(37 + stageLift + interactionLift, 30, 52), TimeFeelRole.HiHat, timing, segmentLength);
            Add(notes, barStart + 3L * SessionConstants.Ppq, 65, 44,
                (byte)Math.Clamp(39 + stageLift + interactionLift, 31, 54), TimeFeelRole.HiHat, timing, segmentLength);

            if (stage is BalladChorusStage.Theme or BalladChorusStage.QuietSolo or BalladChorusStage.HeadOut)
            {
                // GM has no portable brush-sweep articulation. A soft ride is a
                // musical fallback on both the built-in synth and external gear;
                // it avoids turning fake brush notes into rimshots or backbeats.
                foreach (var offset in SoftRide)
                {
                    if (arrangement.IsSectionEnding && offset >= 1760)
                    {
                        continue;
                    }

                    var accent = offset is 800 or 1760 ? 3 : offset % SessionConstants.Ppq == 0 ? 1 : 0;
                    Add(notes, barStart + offset, 72, 51,
                        (byte)Math.Clamp(32 + stageLift + interactionLift + accent, 25, 44), TimeFeelRole.Ride, timing, segmentLength);
                }

                // Keep the portable soft-ballad substitute entirely cymbal-based.
                // GM snare taps can become audible rimshots on external devices;
                // phrase responses are carried by ride articulation and the
                // existing 2/4 pedal hat instead.
            }
            else
            {
                var rideOffsets = stage switch
                {
                    BalladChorusStage.MovingTwoFeel => MovingRide,
                    _ => NormalRide
                };
                foreach (var offset in rideOffsets)
                {
                    if (arrangement.IsSectionEnding && offset >= 1680)
                    {
                        continue;
                    }

                    var accent = offset % SessionConstants.Ppq == 0 ? 3 : offset % (SessionConstants.Ppq / 2) == 0 ? 1 : 0;
                    Add(notes, barStart + offset, 62, 51,
                        (byte)Math.Clamp(39 + stageLift + interactionLift + accent + arrangement.DynamicLift / 4, 34, 62), TimeFeelRole.Ride, timing, segmentLength);
                }

                if (arrangement.Function is PhraseFunction.Comment or PhraseFunction.Build or PhraseFunction.Setup)
                {
                    var compOffset = barIndex % 2 == 0 ? 800L : 1280L;
                    Add(notes, barStart + compOffset, 70, 38,
                        (byte)Math.Clamp(42 + stageLift + interactionLift, 36, 58), TimeFeelRole.DrumComp, timing, segmentLength);
                }
            }

            var kickOffsets = stage == BalladChorusStage.FourFeel
                ? new[] { 0L, 480L, 960L, 1440L }
                : new[] { 0L, 960L };
            foreach (var offset in kickOffsets)
            {
                Add(notes, barStart + offset, 80, 36,
                    (byte)Math.Clamp(22 + stageLift / 2 + interactionLift, 17, 31), TimeFeelRole.Kick, timing, segmentLength);
            }

            var strongBoundary = arrangement.IsSectionEnding && arrangement.Boundary >= BoundaryStrength.Section;
            var fill = strongBoundary &&
                stage is BalladChorusStage.MovingTwoFeel or BalladChorusStage.FourFeel &&
                !previousSectionEndedWithFill;
            if (fill)
            {
                var fillOffsets = new[] { 1440L, 1600L, 1760L };
                for (var index = 0; index < fillOffsets.Length; index++)
                {
                    Add(notes, barStart + fillOffsets[index], 60, index % 2 == 0 ? (byte)38 : (byte)40,
                        (byte)Math.Clamp(43 + stageLift + index * 2, 36, 62), TimeFeelRole.DrumComp, timing, segmentLength);
                }
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
