using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BossaDrumGrooveGenerator
{
    // Blue Bossa 2 keeps a stable 3-2 bossa/clave outline throughout:
    // three side = 1, 2&, 4; two side = 2, 3&.
    private static readonly long[][] SideStick32 =
    [
        [0, 720, 1440],
        [480, 1200]
    ];

    // The reference's continuous light time voice has two alternating dynamic
    // contours. GM cabasa (69) replaces the source-specific shaker mapping.
    private static readonly int[][] CabasaVelocityContours =
    [
        [47, 34, 41, 44, 48, 34, 40, 45],
        [46, 35, 40, 45, 49, 33, 40, 45]
    ];

    public static DrumGenerationResult Generate(
        IReadOnlyList<BarArrangement> arrangements,
        int previousPatternIndex,
        int previousFillVariant,
        bool previousSectionEndedWithFill,
        int previousCompPatternIndex,
        int seed,
        BossaChorusStage stage,
        PerformanceGuidance? performanceGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(arrangements);
        if (arrangements.Count == 0)
        {
            throw new ArgumentException("At least one bar is required.", nameof(arrangements));
        }

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var notes = new List<ScheduledNote>(arrangements.Count * 24);
        var patterns = new int[arrangements.Count];
        var segmentLength = (long)arrangements.Count * SessionConstants.BarTicks;
        var previousParity = previousCompPatternIndex is 520 or 521
            ? previousCompPatternIndex - 520
            : 1;
        var startingParity = (previousParity + 1) % 2;
        var endedWithFill = false;

        for (var barIndex = 0; barIndex < arrangements.Count; barIndex++)
        {
            var arrangement = arrangements[barIndex];
            var barStart = (long)barIndex * SessionConstants.BarTicks;
            var parity = (startingParity + barIndex) % 2;
            var chorusLift = stage == BossaChorusStage.Lifted
                ? 2
                : stage is BossaChorusStage.Opening or BossaChorusStage.HeadOut ? -2 : 0;
            var lift = chorusLift + (guidance.HighStage ? 2 : 0) +
                arrangement.DynamicLift / 4 -
                (arrangement.IsTransitionLeadIn ? 1 : 0);
            var strongBoundary = arrangement.IsSectionEnding &&
                arrangement.Boundary >= BoundaryStrength.Chorus;

            // A light cabasa/shaker carries all eight subdivisions. Density does
            // not change between head and solo; energy changes through dynamics
            // and the occasional boundary fill.
            for (var eighth = 0; eighth < 8; eighth++)
            {
                var offset = eighth * SessionConstants.Ppq / 2L;
                var velocity = CabasaVelocityContours[parity][eighth] + lift;
                Add(notes, barStart + offset, 45, 69,
                    (byte)Math.Clamp(velocity, 28, 58), 4, segmentLength);
            }

            // Reference Brazilian foot ostinato: 1, 2&, 3, 4&. Beat 3 is the
            // strongest floor; 4& is deliberately soft.
            Add(notes, barStart, 80, 36,
                (byte)Math.Clamp(42 + lift, 32, 56), 0, segmentLength);
            Add(notes, barStart + 3L * SessionConstants.Ppq / 2, 60, 36,
                (byte)Math.Clamp(46 + lift, 34, 60), 1, segmentLength);
            Add(notes, barStart + 2L * SessionConstants.Ppq, 80, 36,
                (byte)Math.Clamp(50 + lift, 36, 64), 0, segmentLength);
            Add(notes, barStart + 7L * SessionConstants.Ppq / 2, 60, 36,
                (byte)Math.Clamp(35 + lift, 27, 48), 1, segmentLength);

            // Foot hi-hat on 2 and 4.
            Add(notes, barStart + SessionConstants.Ppq, 55, 44,
                (byte)Math.Clamp(34 + lift / 2, 28, 44), 2, segmentLength);
            Add(notes, barStart + 3L * SessionConstants.Ppq, 55, 44,
                (byte)Math.Clamp(34 + lift / 2, 28, 44), 2, segmentLength);

            // Keep the measured 3-2 side-stick sentence stable. It is a quiet
            // structural layer rather than a freely changing snare comp pattern.
            foreach (var offset in SideStick32[parity])
            {
                Add(notes, barStart + offset, 65, 37,
                    (byte)Math.Clamp(47 + lift, 38, 60), 5, segmentLength);
            }

            // Blue Bossa 2 places tom answers at the end of each 16-bar chorus.
            // Restrict the fuller answer to chorus/ending boundaries so ordinary
            // four-bar sections do not acquire a mechanical fill.
            var shouldFill = strongBoundary ||
                arrangement.IsTransitionLeadIn && arrangement.IsSectionEnding;
            if (shouldFill && !previousSectionEndedWithFill && !endedWithFill)
            {
                Add(notes, barStart + SessionConstants.Ppq / 2, 70, 45,
                    (byte)Math.Clamp(43 + lift, 34, 58), 3, segmentLength);
                if (stage == BossaChorusStage.Lifted || arrangement.IsTransitionLeadIn)
                {
                    Add(notes, barStart + SessionConstants.Ppq, 70, 45,
                        (byte)Math.Clamp(47 + lift, 36, 62), 3, segmentLength);
                }
                Add(notes, barStart + 5L * SessionConstants.Ppq / 2, 70, 43,
                    (byte)Math.Clamp(48 + lift, 38, 64), 3, segmentLength);
                Add(notes, barStart + 3L * SessionConstants.Ppq, 110, 43,
                    (byte)Math.Clamp(51 + lift, 40, 68), 3, segmentLength);
                endedWithFill = true;
            }

            patterns[barIndex] = 520 + parity;
        }

        return new DrumGenerationResult(
            notes,
            patterns[^1],
            patterns,
            previousFillVariant,
            SectionEndedWithFill: endedWithFill,
            LastRidePhraseIndex: -1,
            LastCompPatternIndex: patterns[^1]);
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
