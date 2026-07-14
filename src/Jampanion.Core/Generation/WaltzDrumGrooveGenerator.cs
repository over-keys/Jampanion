using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class WaltzDrumGrooveGenerator
{
    // Two-bar ride sentences. Upbeat placement changes from one bar to the next,
    // which keeps the cymbal line flowing across the barline.
    private static readonly long[][][] RidePatterns =
    [
        [[0, 480, 800, 960], [0, 480, 960, 1280]],
        [[0, 320, 480, 960], [0, 480, 800, 960, 1280]],
        [[0, 480, 960, 1280], [0, 320, 480, 960]],
        [[0, 480, 800, 960, 1280], [0, 480, 960]],
        [[0, 320, 480, 960, 1280], [0, 480, 800, 960]]
    ];

    private static readonly long[][] CompPatterns =
    [
        [], [800], [1280], [320], [800, 1280], [320, 960]
    ];

    public static DrumGenerationResult Generate(
        IReadOnlyList<BarArrangement> arrangements,
        int previousPatternIndex,
        int previousFillVariant,
        bool previousSectionEndedWithFill,
        int previousRidePhraseIndex,
        int previousCompPatternIndex,
        int seed,
        WaltzChorusStage stage,
        WaltzHemiolaPlan hemiolaPlan,
        PerformanceGuidance? performanceGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(arrangements);
        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var barTicks = SessionConstants.GetBarTicks(3);
        var segmentLength = (long)arrangements.Count * barTicks;
        var notes = new List<ScheduledNote>(arrangements.Count * 12);
        var patterns = new int[arrangements.Count];
        var previousRide = previousRidePhraseIndex >= 800 ? previousRidePhraseIndex - 800 : -1;
        var rideIndex = (int)(DeterministicNoise.Unit(seed, 3301) * RidePatterns.Length) % RidePatterns.Length;
        if (rideIndex == previousRide) rideIndex = (rideIndex + 1) % RidePatterns.Length;
        var lastComp = previousCompPatternIndex;
        var lastFill = previousFillVariant;
        var endedWithFill = false;

        for (var barIndex = 0; barIndex < arrangements.Count; barIndex++)
        {
            var arrangement = arrangements[barIndex];
            var barStart = (long)barIndex * barTicks;
            var stageLift = stage switch
            {
                WaltzChorusStage.Lifted => 2,
                WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => -2,
                _ => 0
            };
            var interactionLift = guidance.HighStage ? 3 : 0;
            var lift = stageLift + interactionLift + arrangement.DynamicLift / 2;
            var strongBoundary = arrangement.IsSectionEnding && arrangement.Boundary >= BoundaryStrength.Section;

            var rideOffsets = RidePatterns[rideIndex][barIndex % 2].ToList();
            if (stage is WaltzChorusStage.Opening or WaltzChorusStage.HeadOut && rideOffsets.Count > 4)
            {
                rideOffsets.RemoveAt(rideOffsets.Count - 1);
            }
            else if (stage == WaltzChorusStage.Lifted && arrangement.Function != PhraseFunction.Space &&
                !rideOffsets.Contains(1280) && DeterministicNoise.Unit(seed, barIndex, 3303) < 0.42)
            {
                rideOffsets.Add(1280);
            }

            foreach (var offset in rideOffsets.Order())
            {
                if (strongBoundary && offset == 1280) continue;
                var downbeat = offset % SessionConstants.Ppq == 0;
                var velocity = (byte)Math.Clamp(46 + lift + (offset == 0 ? 3 : downbeat ? 1 : 0), 37, 59);
                Add(notes, barStart + offset, 105, 51, velocity, 2, segmentLength);
            }

            // The foot closes on beat 2 or alternates 2/3 across the two-bar phrase;
            // it is deliberately not a rigid classical waltz backbeat.
            var hiHatOffset = (rideIndex + barIndex) % 3 == 0 ? 960L : 480L;
            Add(notes, barStart + hiHatOffset, 80, 44, (byte)Math.Clamp(42 + lift, 34, 52), 4, segmentLength);

            Add(notes, barStart, 72, 36, (byte)Math.Clamp(31 + lift, 24, 43), 0, segmentLength);
            if (stage == WaltzChorusStage.Lifted && arrangement.Function is PhraseFunction.Build or PhraseFunction.Setup &&
                DeterministicNoise.Unit(seed, barIndex, 3305) < 0.36)
            {
                Add(notes, barStart + 960, 65, 36, (byte)Math.Clamp(28 + lift, 23, 39), 0, segmentLength);
            }

            var hemiolaBar = hemiolaPlan.ContainsBar(barIndex);
            var compIndex = SelectCompPattern(previousCompPatternIndex, seed, barIndex, stage, arrangement);
            var compOffsets = hemiolaPlan.IsFirstBar(barIndex)
                ? new[] { 0L, 960L }
                : hemiolaPlan.IsSecondBar(barIndex)
                    ? new[] { 480L }
                    : CompPatterns[compIndex];
            foreach (var offset in compOffsets)
            {
                if (!hemiolaBar && strongBoundary && offset >= 960) continue;
                var hemiolaLift = hemiolaPlan.IsAnchor(barIndex, offset) ? 6 : 0;
                var velocity = (byte)Math.Clamp(39 + lift + (arrangement.InvitesDrumStatement ? 4 : 0) + hemiolaLift, 31, 61);
                var note = hemiolaBar && guidance.HighStage ? (byte)38 : (byte)37;
                Add(notes, barStart + offset, 75, note, velocity, 6, segmentLength);
            }

            if (strongBoundary && !previousSectionEndedWithFill)
            {
                lastFill = (rideIndex + barIndex) % 3;
                var fillOffsets = lastFill switch
                {
                    0 => new[] { 1120L, 1280L },
                    1 => new[] { 960L, 1280L },
                    _ => new[] { 1120L, 1280L, 1360L }
                };
                for (var i = 0; i < fillOffsets.Length; i++)
                {
                    Add(notes, barStart + fillOffsets[i], 70, i == fillOffsets.Length - 1 ? (byte)38 : (byte)37,
                        (byte)Math.Clamp(43 + lift + i, 35, 58), 4, segmentLength);
                }
                endedWithFill = true;
            }
            else
            {
                endedWithFill = false;
            }

            lastComp = hemiolaBar ? 880 + (hemiolaPlan.IsSecondBar(barIndex) ? 1 : 0) : 850 + compIndex;
            patterns[barIndex] = 800 + rideIndex;
        }

        return new DrumGenerationResult(
            notes,
            800 + rideIndex,
            patterns,
            lastFill,
            endedWithFill,
            800 + rideIndex,
            lastComp);
    }

    private static int SelectCompPattern(
        int previousCompPatternIndex,
        int seed,
        int barIndex,
        WaltzChorusStage stage,
        BarArrangement arrangement)
    {
        var maxExclusive = stage is WaltzChorusStage.Opening or WaltzChorusStage.HeadOut ? 4 : CompPatterns.Length;
        var selected = (int)(DeterministicNoise.Unit(seed, barIndex, 3307) * maxExclusive) % maxExclusive;
        if (arrangement.Function == PhraseFunction.Space) selected = 0;
        var previous = previousCompPatternIndex >= 850 ? previousCompPatternIndex - 850 : -1;
        if (selected == previous && selected != 0) selected = (selected + 1) % maxExclusive;
        return selected;
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
        if (start >= segmentLength) return;
        notes.Add(new ScheduledNote(start, Math.Min(duration, segmentLength - start), note, velocity, SessionConstants.DrumsChannel));
    }
}
