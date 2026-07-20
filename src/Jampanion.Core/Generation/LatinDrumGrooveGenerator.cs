using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class LatinDrumGrooveGenerator
{
    // The generated Latin chart is fixed in 2-3 son clave. The first bar is the
    // two side (2, 3); the second is the three side (1, 2&, 4). These timepoints
    // are intentionally present throughout the chorus, rather than being treated
    // as a decorative layer that appears only in a high-energy section.
    private static readonly long[][] Clave23 =
    [
        [480, 960],
        [0, 720, 1440]
    ];

    // A compact paila/cascara phrase which interlocks with that clave direction.
    // It is the main time voice at every dynamic, so the ensemble changes through
    // articulation and the bombo support rather than swapping instruments.
    private static readonly long[][] Cascara23 =
    [
        [0, 480, 720, 960, 1440, 1680],
        [0, 240, 720, 960, 1200, 1680]
    ];

    private static readonly long[][] MontunoBell23 =
    [
        [0, 960, 1440],
        [0, 720, 1440]
    ];

    private static readonly long[][] MamboBell23 =
    [
        [0, 480, 960, 1440],
        [0, 480, 720, 1440]
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
        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var segmentLength = (long)arrangements.Count * SessionConstants.BarTicks;
        var notes = new List<ScheduledNote>(arrangements.Count * 11);
        var patterns = new int[arrangements.Count];
        var stageLift = stage switch
        {
            LatinChorusStage.Opening or LatinChorusStage.HeadOut => -4,
            LatinChorusStage.Ponchando => -2,
            LatinChorusStage.Mambo => 5,
            _ => 0
        };
        var interactionLift = guidance.HighStage ? 3 : 0;
        var endedWithFill = false;

        for (var barIndex = 0; barIndex < arrangements.Count; barIndex++)
        {
            var arrangement = arrangements[barIndex];
            var barStart = (long)barIndex * SessionConstants.BarTicks;
            var parity = barIndex % 2;
            var strongBoundary = arrangement.IsSectionEnding && arrangement.Boundary >= BoundaryStrength.Section;

            AddClave(notes, Clave23[parity], barStart, stageLift, segmentLength);
            AddCascara(notes, Cascara23[parity], barStart, strongBoundary, stageLift, interactionLift, arrangement, segmentLength);
            AddMetalLayer(notes, barStart, parity, stage, stageLift, interactionLift, arrangement, segmentLength);
            // Keep the drum-set low voice sparse. The bass owns the tumbao; the
            // kick only reinforces the &2 anticipation when the band is moving,
            // never turns it into a four-on-the-floor or a backbeat groove.
            if (stage is LatinChorusStage.Montuno or LatinChorusStage.Mambo)
            {
                Add(notes, barStart + 3L * SessionConstants.Ppq / 2, 60, 36,
                    (byte)Math.Clamp(39 + stageLift / 2 + interactionLift + arrangement.DynamicLift / 4, 32, 56),
                    3, segmentLength);
            }

            if ((stage == LatinChorusStage.Mambo || arrangement.IsTransitionLeadIn) &&
                strongBoundary && !previousSectionEndedWithFill)
            {
                if (arrangement.IsTransitionLeadIn)
                {
                    // Make the solo-to-head handoff unmistakable with a short
                    // timbale fill, but keep it below a mambo climax.
                    Add(notes, barStart + 3L * SessionConstants.Ppq, 50, 45,
                        (byte)Math.Clamp(43 + stageLift + interactionLift, 36, 62), 3, segmentLength);
                    Add(notes, barStart + 1600, 50, 45,
                        (byte)Math.Clamp(48 + stageLift + interactionLift, 38, 66), 3, segmentLength);
                    Add(notes, barStart + 7L * SessionConstants.Ppq / 2, 80, 45,
                        (byte)Math.Clamp(52 + stageLift + interactionLift - 5, 40, 70), 3, segmentLength);
                }
                else
                {
                    Add(notes, barStart + 7L * SessionConstants.Ppq / 2, 50, 45,
                        (byte)Math.Clamp(54 + stageLift + interactionLift, 40, 70), 3, segmentLength);
                }
                endedWithFill = true;
            }

            patterns[barIndex] = 710 + parity;
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

    private static void AddMetalLayer(
        List<ScheduledNote> notes,
        long barStart,
        int parity,
        LatinChorusStage stage,
        int stageLift,
        int interactionLift,
        BarArrangement arrangement,
        long segmentLength)
    {
        if (stage is LatinChorusStage.Opening or LatinChorusStage.HeadOut or LatinChorusStage.Ponchando)
        {
            // A quiet closed-hat shimmer keeps the opening from sounding dry;
            // the cencerro waits until the true montuno section.
            foreach (var offset in new[] { 480L, 1440L })
            {
                Add(notes, barStart + offset, 44, 42,
                    (byte)Math.Clamp(34 + stageLift / 2 + arrangement.DynamicLift / 5, 29, 43),
                    4, segmentLength);
            }
            return;
        }

        var pattern = stage == LatinChorusStage.Mambo ? MamboBell23[parity] : MontunoBell23[parity];
        foreach (var offset in pattern)
        {
            var accent = offset is 720 or 1440 ? 4 : 0;
            Add(notes, barStart + offset, 54, 56,
                (byte)Math.Clamp(45 + stageLift + interactionLift + accent + arrangement.DynamicLift / 4, 40, 68),
                4, segmentLength);
        }
    }

    private static void AddClave(
        List<ScheduledNote> notes,
        IReadOnlyList<long> offsets,
        long barStart,
        int stageLift,
        long segmentLength)
    {
        foreach (var offset in offsets)
        {
            Add(notes, barStart + offset, 48, 75,
                (byte)Math.Clamp(48 + stageLift / 3, 42, 60), 0, segmentLength);
        }
    }

    private static void AddCascara(
        List<ScheduledNote> notes,
        IReadOnlyList<long> offsets,
        long barStart,
        bool strongBoundary,
        int stageLift,
        int interactionLift,
        BarArrangement arrangement,
        long segmentLength)
    {
        foreach (var offset in offsets)
        {
            // Preserve the phrase through section joins. Only the final pickup is
            // cleared when a one-note timbale answer takes its place.
            if (strongBoundary && offset == 1680)
            {
                continue;
            }

            if (arrangement.IsTransitionLeadIn && offset is 720 or 1680 &&
                DeterministicNoise.Unit((int)barStart, (int)offset, 7311) < 0.45)
            {
                continue;
            }

            var accent = offset is 720 or 1680 ? 4 : offset % SessionConstants.Ppq == 0 ? 1 : 0;
            Add(notes, barStart + offset, 52, 37,
                (byte)Math.Clamp(47 + stageLift + interactionLift + accent + arrangement.DynamicLift / 3, 37, 70),
                3, segmentLength);
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

        notes.Add(new ScheduledNote(start, Math.Min(duration, segmentLength - start), note, velocity, SessionConstants.DrumsChannel));
    }
}
