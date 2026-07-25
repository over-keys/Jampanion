using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BalladPianoCompingGenerator
{
    // Ballad vocabulary is weighted toward long harmonic statements. Delayed
    // entries and short answers are separate musical roles, not interchangeable
    // filler; the later stages widen their choice without becoming medium swing.
    private static readonly BalladPattern[] ThemePatterns =
    [
        P(8101, 3.40, 0),
        P(8102, 1.80, 0, 800),
        P(8103, 0.70, 480),
        P(8104, 0.55, 960),
        P(8105, 0.75, 320),
        P(8106, 0.80, 0, 960),
        P(8107, 0.55, 0, 1280),
        P(8108, 0.60, 0, 1440),
        P(8109, 0.50, 320, 960),
        P(8110, 0.35, 480, 1280),
        // A ballad may lean into the next bar from 4&, but this remains a
        // phrase-level colour rather than the default opening gesture.
        P(8111, 0.45, 0, 1760),
        P(8112, 0.30, 1760),
        P(8113, 0.20, 960, 1760)
    ];

    private static readonly BalladPattern[] QuietSoloPatterns =
    [
        P(8201, 2.20, 0),
        P(8202, 1.35, 0, 800),
        P(8203, 0.95, 320),
        P(8204, 0.85, 480),
        P(8205, 0.75, 960),
        P(8206, 0.65, 800),
        P(8207, 0.85, 0, 960),
        P(8208, 0.80, 320, 960),
        P(8209, 0.55, 480, 1280),
        P(8210, 0.30, 0, 320, 1440),
        P(8211, 0.80, 0, 1760),
        P(8212, 0.50, 1760)
    ];

    private static readonly BalladPattern[] MovingTwoFeelPatterns =
    [
        // The reference performance averages about one or two harmonic
        // statements per bar. Keep the middle stage alive through sustain and
        // voice leading, not through repeated full-block chord attacks.
        P(8301, 3.50, 0),
        P(8302, 1.45, 0, 800),
        P(8303, 1.00, 0, 960),
        P(8304, 0.95, 320, 960),
        P(8305, 0.80, 0, 1280),
        P(8306, 0.70, 0, 1440),
        P(8307, 0.80, 480, 1280),
        P(8308, 0.70, 800, 1440),
        P(8309, 0.15, 0, 320, 960),
        P(8310, 0.15, 0, 800, 1440),
        P(8311, 0.60, 0, 1760),
        P(8312, 0.70, 1760)
    ];

    // Four-feel remains a ballad texture. Long statements and delayed entries
    // still dominate; the three-hit shapes are occasional phrase-level lifts.
    private static readonly BalladPattern[] FourFeelPatterns =
    [
        P(8401, 2.20, 0),
        P(8402, 1.40, 0, 800),
        P(8403, 1.00, 0, 960),
        P(8404, 0.80, 480),
        P(8405, 0.70, 960),
        P(8406, 0.70, 320),
        P(8407, 0.80, 320, 960),
        P(8408, 0.65, 480, 1280),
        P(8409, 0.60, 800, 1440),
        P(8410, 0.80, 0, 1280),
        P(8411, 0.70, 0, 1440),
        P(8412, 0.35, 0, 800, 1440),
        P(8413, 0.45, 0, 1760),
        P(8414, 0.30, 1760)
    ];

    public static PianoGenerationResult Generate(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<BalladChorusStage> stages,
        IReadOnlyList<byte>? previousVoicing,
        int previousCellIndex,
        int seed,
        PerformanceGuidance? performanceGuidance = null,
        bool previousSegmentEndedOnFourAnd = false,
        TimeFeelProfile? timeFeel = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);
        ArgumentNullException.ThrowIfNull(stages);
        if (bars.Count != arrangements.Count || bars.Count != stages.Count)
        {
            throw new ArgumentException("Bars, arrangements, and ballad stages must have the same length.");
        }

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var timing = timeFeel ?? TimeFeelProfile.Resolve(AccompanimentStyle.JazzBallad, 64);

        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var notes = new List<ScheduledNote>(bars.Count * 12);
        var cells = new int[bars.Count];
        IReadOnlyList<byte> lastVoicing = previousVoicing ?? Array.Empty<byte>();
        var previousBarEndedOnFourAnd = previousSegmentEndedOnFourAnd;
        var segmentEndedOnFourAnd = false;

        // Choose the complete rhythmic plan before rendering any notes.  This
        // lets a 4& anticipation know which attack follows it across the
        // barline, so the held chord can actually ring into the next phrase.
        var selections = new BalladPatternSelection[bars.Count];
        var previousPatternIndex = previousCellIndex;
        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var nextBarChord = barIndex + 1 < bars.Count ? bars[barIndex + 1].Chord : followingChord;
            var selection = BuildOffsets(
                bar,
                arrangements[barIndex],
                stages[barIndex],
                guidance,
                seed,
                barIndex,
                previousPatternIndex);
            selections[barIndex] = selection;
            cells[barIndex] = selection.PatternIndex;
            previousPatternIndex = selection.PatternIndex;
        }

        var playableHits = BuildPlayableHits(
            bars,
            selections,
            followingChord,
            previousSegmentEndedOnFourAnd);
        var playableHitIndices = playableHits
            .Select((hit, index) => (hit, index))
            .ToDictionary(item => (item.hit.BarIndex, item.hit.HitIndex), item => item.index);

        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var currentBarEndedOnFourAnd = false;
            var bar = bars[barIndex];
            var stage = stages[barIndex];
            var nextBarChord = barIndex + 1 < bars.Count ? bars[barIndex + 1].Chord : followingChord;
            var offsets = selections[barIndex].Offsets;
            var barStart = (long)barIndex * SessionConstants.BarTicks;

            for (var hitIndex = 0; hitIndex < offsets.Count; hitIndex++)
            {
                var offset = offsets[hitIndex];
                if (PianoBarlineRhythmGuard.SuppressDownbeatAfterFourAnd(
                        previousBarEndedOnFourAnd,
                        offset))
                {
                    continue;
                }

                var chord = ResolveChord(bar, nextBarChord, offset);
                chord = ChordFactory.ApplyMinorTargetTensions(
                    chord,
                    ChordFactory.GetFollowingChord(bar, offset, nextBarChord));
                if (chord.IsNoChord)
                {
                    continue;
                }
                var voiceCount = SelectVoiceCount(chord, stage, seed, barIndex, hitIndex);
                var voicing = VoiceLead(chord, voiceCount, lastVoicing, stage, guidance, seed, barIndex, hitIndex);
                // Ballad chords should read as one pianist's gesture.  Keep
                // the voicing simultaneous; the shared human nudge below is
                // enough life without making the chord sound arpeggiated.
                const bool rolled = false;
                var stageLift = stage switch
                {
                    BalladChorusStage.Theme or BalladChorusStage.HeadOut => -5,
                    BalladChorusStage.QuietSolo => -3,
                    BalladChorusStage.MovingTwoFeel => 0,
                    BalladChorusStage.FourFeel => 2,
                    _ => 0
                };
                var interactionLift = guidance.HighStage ? 2 : 0;
                var velocity = Math.Clamp(
                    55 + stageLift + interactionLift + arrangements[barIndex].DynamicLift / 3 -
                    (arrangements[barIndex].IsTransitionLeadIn ? 2 : 0),
                    49,
                    72);
                var nextOffset = hitIndex + 1 < offsets.Count ? offsets[hitIndex + 1] : SessionConstants.BarTicks;
                // ResolveDuration already expresses the desired release gap;
                // scaling it again can erase that gap at a slow tempo.
                var duration = ResolveDuration(
                    stage, offset, nextOffset, seed, barIndex, hitIndex);
                var humanNudge = timing.MillisecondsToTicks(
                    (DeterministicNoise.Unit(seed, barIndex, hitIndex, 7203) - 0.5) * 4.0);
                var nextAttackStart = segmentLength;
                if (playableHitIndices.TryGetValue((barIndex, hitIndex), out var playableHitIndex) &&
                    playableHitIndex + 1 < playableHits.Count)
                {
                    var nextHit = playableHits[playableHitIndex + 1];
                    var nextNudge = timing.MillisecondsToTicks(
                        (DeterministicNoise.Unit(seed, nextHit.BarIndex, nextHit.HitIndex, 7203) - 0.5) * 4.0);
                    nextAttackStart = timing.Place(
                        (long)nextHit.BarIndex * SessionConstants.BarTicks + nextHit.Offset,
                        TimeFeelRole.Piano) + nextNudge;
                }

                if (PianoBarlineRhythmGuard.IsFourAnd(offset) &&
                    nextAttackStart > barStart + SessionConstants.BarTicks)
                {
                    // Leave only a small release gap before the next planned
                    // attack.  This is the actual sustained 4& gesture; the
                    // previous per-bar cap made every anticipation staccato at
                    // the barline.
                    var releaseGap = 24L + (long)Math.Round(
                        DeterministicNoise.Unit(seed, barIndex, hitIndex, 7212) * 40.0);
                    var heldDuration = nextAttackStart -
                        (barStart + offset) - releaseGap;
                    duration = Math.Max(duration, heldDuration);
                }

                for (var voiceIndex = 0; voiceIndex < voicing.Count; voiceIndex++)
                {
                    var rollDelay = rolled
                        ? voiceIndex * timing.MillisecondsToTicks(11.0)
                        : 0L;
                    var start = timing.Place(barStart + offset, TimeFeelRole.Piano) + humanNudge + rollDelay;
                    if (start >= segmentLength)
                    {
                        continue;
                    }

                    var noteDuration = Math.Min(
                        Math.Min(duration, segmentLength - start),
                        Math.Max(1, nextAttackStart - start));
                    notes.Add(new ScheduledNote(
                        start,
                        noteDuration,
                        voicing[voiceIndex],
                        (byte)Math.Clamp(velocity - (rolled ? Math.Min(voiceIndex, 2) : 0), 42, 70),
                        SessionConstants.PianoChannel));
                }

                lastVoicing = voicing;
                currentBarEndedOnFourAnd = PianoBarlineRhythmGuard.IsFourAnd(offset) &&
                    !bar.GetChordAtTick(Math.Min(offset, bar.BarTicks - 1)).IsNoChord;
            }

            previousBarEndedOnFourAnd = currentBarEndedOnFourAnd;
            segmentEndedOnFourAnd = currentBarEndedOnFourAnd;
        }

        return new PianoGenerationResult(
            notes,
            lastVoicing,
            cells[^1],
            cells,
            segmentEndedOnFourAnd);
    }

    private static BalladPatternSelection BuildOffsets(
        TuneBar bar,
        BarArrangement arrangement,
        BalladChorusStage stage,
        PerformanceGuidance guidance,
        int seed,
        int barIndex,
        int previousPatternIndex)
    {
        _ = guidance;
        var source = stage switch
        {
            BalladChorusStage.Theme or BalladChorusStage.HeadOut => ThemePatterns,
            BalladChorusStage.QuietSolo => QuietSoloPatterns,
            BalladChorusStage.MovingTwoFeel => MovingTwoFeelPatterns,
            _ => FourFeelPatterns
        };
        var pattern = SelectPattern(source, previousPatternIndex,
            DeterministicNoise.Unit(seed, barIndex, (int)stage, 7205));
        var offsets = pattern.Offsets.ToList();

        foreach (var change in bar.ChordChanges.Skip(1))
        {
            var changeTick = (long)change.StartBeat * SessionConstants.Ppq;
            if (!offsets.Any(offset => Math.Abs(offset - changeTick) <= SessionConstants.Ppq / 2))
            {
                // Ballad.mid places an upcoming harmony on the preceding
                // triplet subdivision rather than dividing the bar into two
                // mechanically equal half-note blocks.
                offsets.Add(Math.Max(0, changeTick - SessionConstants.Ppq / 3));
            }
        }

        if (arrangement.Function == PhraseFunction.Space)
        {
            var structural = bar.ChordChanges.Skip(1)
                .Select(change => (long)change.StartBeat * SessionConstants.Ppq)
                .ToHashSet();
            offsets = offsets
                .Where((offset, index) => index == 0 || structural.Contains(offset))
                .Take(1)
                .ToList();
        }
        else if (arrangement.IsTransitionLeadIn && offsets.Count > 1)
        {
            // Ballad handoff remains legato: retain the first statement and any
            // written harmony arrival, while removing one secondary punctuation.
            var structural = bar.ChordChanges.Skip(1)
                .Select(change => Math.Max(0L, (long)change.StartBeat * SessionConstants.Ppq - SessionConstants.Ppq / 3))
                .ToHashSet();
            var removable = offsets
                .Where(offset => offset != 0 && offset < 1760 && !structural.Contains(offset))
                .OrderBy(offset => DeterministicNoise.Unit(seed, barIndex, (int)offset, 7210))
                .FirstOrDefault(-1);
            if (removable >= 0)
            {
                offsets.Remove(removable);
            }
        }

        var maximum = stage == BalladChorusStage.FourFeel ? 3 : 4;
        return new BalladPatternSelection(
            pattern.Index,
            offsets.Distinct().Order().Take(maximum).ToArray());
    }

    private static IReadOnlyList<BalladHit> BuildPlayableHits(
        IReadOnlyList<TuneBar> bars,
        IReadOnlyList<BalladPatternSelection> selections,
        ChordSpec followingChord,
        bool previousSegmentEndedOnFourAnd)
    {
        var hits = new List<BalladHit>();
        var previousBarEndedOnFourAnd = previousSegmentEndedOnFourAnd;

        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var nextBarChord = barIndex + 1 < bars.Count ? bars[barIndex + 1].Chord : followingChord;
            var endedOnFourAnd = false;
            var offsets = selections[barIndex].Offsets;
            for (var hitIndex = 0; hitIndex < offsets.Count; hitIndex++)
            {
                var offset = offsets[hitIndex];
                if (PianoBarlineRhythmGuard.SuppressDownbeatAfterFourAnd(
                        previousBarEndedOnFourAnd,
                        offset))
                {
                    continue;
                }

                var chord = ResolveChord(bar, nextBarChord, offset);
                chord = ChordFactory.ApplyMinorTargetTensions(
                    chord,
                    ChordFactory.GetFollowingChord(bar, offset, nextBarChord));
                if (chord.IsNoChord)
                {
                    continue;
                }

                hits.Add(new BalladHit(barIndex, hitIndex, offset));
                endedOnFourAnd = PianoBarlineRhythmGuard.IsFourAnd(offset);
            }

            previousBarEndedOnFourAnd = endedOnFourAnd;
        }

        return hits;
    }

    private static BalladPattern SelectPattern(
        IReadOnlyList<BalladPattern> source,
        int previousPatternIndex,
        double selector)
    {
        // Repetition is part of ballad phrasing, so do not ban it outright.
        // Penalize an immediate repeat enough that it reads as an intentional
        // sustain/restatement rather than the generator getting stuck.
        var candidates = source.ToArray();
        double EffectiveWeight(BalladPattern pattern) =>
            pattern.Weight * (pattern.Index == previousPatternIndex ? 0.18 : 1.0);
        var totalWeight = candidates.Sum(EffectiveWeight);
        var target = selector * totalWeight;
        var cumulative = 0.0;
        foreach (var candidate in candidates)
        {
            cumulative += EffectiveWeight(candidate);
            if (target <= cumulative)
            {
                return candidate;
            }
        }

        return candidates[^1];
    }

    private static long ResolveDuration(
        BalladChorusStage stage,
        long offset,
        long nextOffset,
        int seed,
        int barIndex,
        int hitIndex)
    {
        // The reference releases only a few ticks before the following attack.
        // Long sound, rather than silence, creates the calm harmonic floor.
        var releaseGaps = stage switch
        {
            BalladChorusStage.Theme => new[] { 8L, 16L, 28L },
            BalladChorusStage.HeadOut => new[] { 4L, 10L, 20L },
            BalladChorusStage.QuietSolo => new[] { 8L, 18L, 32L },
            BalladChorusStage.MovingTwoFeel => new[] { 10L, 24L, 42L },
            BalladChorusStage.FourFeel => new[] { 14L, 30L, 52L },
            _ => new[] { 10L, 24L }
        };
        var selector = DeterministicNoise.Unit(seed, barIndex, hitIndex, 7211);
        var releaseGap = releaseGaps[Math.Min(
            releaseGaps.Length - 1,
            (int)Math.Floor(selector * releaseGaps.Length))];
        return Math.Max(120L, nextOffset - offset - releaseGap);
    }

    private static ChordSpec ResolveChord(TuneBar bar, ChordSpec nextBarChord, long offset)
    {
        if (offset >= 1760)
        {
            return nextBarChord;
        }

        var anticipated = bar.ChordChanges.FirstOrDefault(change =>
            change.StartBeat * SessionConstants.Ppq > offset &&
            change.StartBeat * SessionConstants.Ppq - offset <= SessionConstants.Ppq / 2);
        return anticipated?.Chord ?? bar.GetChordAtTick(Math.Min(offset, bar.BarTicks - 1));
    }

    private static int SelectVoiceCount(
        ChordSpec chord,
        BalladChorusStage stage,
        int seed,
        int barIndex,
        int hitIndex)
    {
        var available = chord.PianoPitchClasses.Distinct().Count();
        if (available < 3)
        {
            return Math.Max(2, available);
        }

        var selector = DeterministicNoise.Unit(seed, barIndex, hitIndex, 7231);
        var count = stage switch
        {
            BalladChorusStage.Theme or BalladChorusStage.HeadOut => selector < 0.24 ? 3 : 4,
            BalladChorusStage.QuietSolo => selector < 0.20 ? 3 : 4,
            BalladChorusStage.MovingTwoFeel => selector < 0.14 ? 3 : 4,
            BalladChorusStage.FourFeel => selector < 0.10 ? 3 : 4,
            _ => selector < 0.16 ? 3 : 4
        };
        return Math.Min(count, available);
    }

    private static IReadOnlyList<byte> VoiceLead(
        ChordSpec chord,
        int voiceCount,
        IReadOnlyList<byte> previous,
        BalladChorusStage stage,
        PerformanceGuidance guidance,
        int seed,
        int barIndex,
        int hitIndex)
    {
        var targetCenter = stage switch
        {
            BalladChorusStage.Theme or BalladChorusStage.HeadOut => 62.5,
            BalladChorusStage.QuietSolo => 63.5,
            BalladChorusStage.MovingTwoFeel => 64.5,
            BalladChorusStage.FourFeel => 65.0,
            _ => 66.0
        };
        var upper = stage switch
        {
            BalladChorusStage.Theme or BalladChorusStage.QuietSolo or BalladChorusStage.HeadOut => 76,
            BalladChorusStage.MovingTwoFeel or BalladChorusStage.FourFeel => 79,
            _ => 82
        };
        return PianoVoicingVocabulary.Choose(
            chord.PianoPitchClasses,
            previous,
            voiceCount,
            lower: 50,
            upper,
            targetCenter,
            PianoVoicingStyle.Ballad,
            chord.RootPitchClass,
            seed,
            barIndex,
            hitIndex);
    }

    private static BalladPattern P(int index, double weight, params long[] offsets) =>
        new(index, weight, offsets);

    private sealed record BalladPattern(int Index, double Weight, IReadOnlyList<long> Offsets);

    private sealed record BalladPatternSelection(int PatternIndex, IReadOnlyList<long> Offsets);

    private sealed record BalladHit(int BarIndex, int HitIndex, long Offset);
}
