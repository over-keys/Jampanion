using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BossaDrumGrooveGenerator
{
    private static readonly long[][][] CrossStickPatterns =
    [
        [[480, 1200, 1680], [240, 960, 1440]],
        [[240, 720, 1440], [480, 1200, 1680]],
        [[480, 960, 1680], [240, 720, 1440]]
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
        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var notes = new List<ScheduledNote>(arrangements.Count * 30);
        var patterns = new int[arrangements.Count];
        var segmentLength = (long)arrangements.Count * SessionConstants.BarTicks;
        var previous = previousCompPatternIndex >= 500 ? previousCompPatternIndex - 500 : -1;
        var patternIndex = (int)(DeterministicNoise.Unit(seed, 2901) * CrossStickPatterns.Length) % CrossStickPatterns.Length;
        if (patternIndex == previous) patternIndex = (patternIndex + 1) % CrossStickPatterns.Length;

        for (var barIndex = 0; barIndex < arrangements.Count; barIndex++)
        {
            var arrangement = arrangements[barIndex];
            var barStart = (long)barIndex * SessionConstants.BarTicks;
            var chorusLift = stage == BossaChorusStage.Lifted ? 1 : stage is BossaChorusStage.Opening or BossaChorusStage.HeadOut ? -1 : 0;
            var lift = chorusLift + (guidance.HighStage ? 3 : 0) -
                (arrangement.IsTransitionLeadIn ? 2 : 0);
            var strongBoundary = arrangement.IsSectionEnding && arrangement.Boundary >= BoundaryStrength.Section;
            var addSixteenthCabasa = stage == BossaChorusStage.Lifted ||
                (stage == BossaChorusStage.Standard && guidance.HighStage);

            if (barIndex == 0 && arrangement.IsHeadOutEntry)
            {
                // The bossa head returns on a low, dry cymbal cue rather than a
                // bright accent that would contradict the release in energy.
                Add(notes, barStart, 105, 49, 42, 3, segmentLength);
            }

            for (var eighth = 0; eighth < 8; eighth++)
            {
                var offset = eighth * SessionConstants.Ppq / 2L;
                if (strongBoundary && eighth == 7) continue;
                var accent = eighth is 2 or 6 ? 3 : eighth % 2 == 0 ? 1 : 0;
                Add(notes, barStart + offset, 45, 42, (byte)(38 + accent + lift), 3, segmentLength);
            }

            if (addSixteenthCabasa)
            {
                for (var sixteenth = 0; sixteenth < 16; sixteenth++)
                {
                    if ((strongBoundary || arrangement.IsTransitionLeadIn) && sixteenth >= 14) continue;
                    var offset = sixteenth * SessionConstants.Ppq / 4L;
                    var accent = sixteenth % 4 == 2 ? 3 : sixteenth % 2 == 0 ? 1 : 0;
                    var velocity = 25 + accent + Math.Clamp(lift, -1, 3);
                    Add(notes, barStart + offset, 35, 69, (byte)Math.Clamp(velocity, 21, 36), 1, segmentLength);
                }
            }

            // The Brazilian foot ostinato outlines 1, 2&, 3, 4&. The head keeps
            // only the broad 1/3 foundation; once the solo opens, the quieter
            // anticipations lock with the bass without becoming a rock backbeat.
            Add(notes, barStart, 80, 36, (byte)(33 + lift), 0, segmentLength);
            Add(notes, barStart + 2L * SessionConstants.Ppq, 80, 36, (byte)(36 + lift), 0, segmentLength);
            if (stage is not (BossaChorusStage.Opening or BossaChorusStage.HeadOut))
            {
                Add(notes, barStart + 3L * SessionConstants.Ppq / 2, 55, 36,
                    (byte)Math.Clamp(27 + lift, 23, 40), 1, segmentLength);
                if (!strongBoundary)
                {
                    Add(notes, barStart + 7L * SessionConstants.Ppq / 2, 55, 36,
                        (byte)Math.Clamp(29 + lift, 24, 42), 1, segmentLength);
                }
            }

            var stickOffsets = CrossStickPatterns[patternIndex][barIndex % 2].ToList();
            if (stage is BossaChorusStage.Opening or BossaChorusStage.HeadOut &&
                barIndex % 2 == ((int)(DeterministicNoise.Unit(seed, 2903) * 2) % 2) &&
                stickOffsets.Count > 2)
            {
                stickOffsets.RemoveAt(1);
            }
            else if (stage == BossaChorusStage.Lifted &&
                arrangement.Function != PhraseFunction.Space &&
                DeterministicNoise.Unit(seed, barIndex, 2905) < 0.36)
            {
                var extra = new[] { 720L, 960L, 1200L, 1440L }
                    .Where(candidate => !stickOffsets.Contains(candidate))
                    .OrderBy(candidate => DeterministicNoise.Unit(seed, barIndex, (int)candidate, 2907))
                    .FirstOrDefault(-1);
                if (extra >= 0) stickOffsets.Add(extra);
            }

            foreach (var offset in stickOffsets.Order())
            {
                if (arrangement.IsTransitionLeadIn && offset != 480 &&
                    DeterministicNoise.Unit(seed, barIndex, (int)offset, 2910) < 0.38)
                {
                    continue;
                }
                var velocity = 47 + lift + (offset % SessionConstants.Ppq == 0 ? 2 : 0);
                Add(notes, barStart + offset, 65, 37, (byte)Math.Clamp(velocity, 40, 58), 5, segmentLength);
            }

            if (strongBoundary)
            {
                Add(notes, barStart + 7L * SessionConstants.Ppq / 2, 180, 46, (byte)Math.Clamp(44 + lift, 30, 60), 3, segmentLength);
            }

            patterns[barIndex] = 500 + patternIndex;
        }

        return new DrumGenerationResult(
            notes,
            500 + patternIndex,
            patterns,
            previousFillVariant,
            SectionEndedWithFill: false,
            LastRidePhraseIndex: -1,
            LastCompPatternIndex: 500 + patternIndex);
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
