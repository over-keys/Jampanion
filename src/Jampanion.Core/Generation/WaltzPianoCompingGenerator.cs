using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class WaltzPianoCompingGenerator
{
    private static readonly long[][][] OpeningSentences =
    [
        [[0], [480], [0], [960]],
        [[480], [0], [800], [0]],
        [[0], [800], [480], [0]]
    ];

    private static readonly long[][][] StandardSentences =
    [
        [[0, 960], [320], [480, 1280], [800, 1280]],
        [[320, 960], [0, 800], [480], [960, 1280]],
        [[0, 800], [480, 1280], [320], [0, 960]]
    ];

    // Before the bass walks, the piano carries the harmony with one broad
    // statement per bar and only occasional 2:3 punctuation.  These are not
    // silent-by-rule cells: the duration engine decides whether the statement
    // becomes a held block or a true rest after an ensemble response.
    private static readonly long[][][] NonWalkingStandardSentences =
    [
        [[0, 960], [0, 800], [480], [800, 1280]],
        [[320, 960], [0], [480, 1280], [960]],
        [[0, 800], [480, 1280], [320], [0, 960]]
    ];

    // Once the bass owns all three beats, the piano moves around it rather than
    // doubling the pulse.  1&/2&/3& are the waltz equivalents of a swung
    // syncopation; 3& is reserved as a next-bar anticipation.
    private static readonly long[][][] WalkingStandardSentences =
    [
        [[320, 800], [320], [800, 1280], [320, 960]],
        [[320, 800], [0, 800], [480, 1280], [800, 1280]],
        [[0, 800], [320, 960], [800, 1280], [320, 800]]
    ];

    private static readonly long[][][] DevelopingSentences =
    [
        [[0, 800], [320], [480, 1280], [0, 960]],
        [[320, 960], [0, 800, 1280], [480], [0, 1280]],
        [[0, 480], [800, 1280], [320], [0, 800]]
    ];

    private static readonly long[][][] LiftedSentences =
    [
        // At the ensemble peak, leave explicit air around the soloist and
        // walking bass.  Two of the four bars speak once; the other two use a
        // restrained answer/anticipation cell rather than filling all three
        // beats.
        [[320, 800], [480], [320, 960], [800]],
        [[800], [320, 960], [480], [320, 800]],
        [[320, 960], [800], [320, 1280], [480]]
    ];

    public static PianoGenerationResult Generate(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<byte>? previousVoicing,
        int previousCellIndex,
        int seed,
        WaltzChorusStage stage,
        WaltzHemiolaPlan hemiolaPlan,
        PerformanceGuidance? performanceGuidance = null,
        IReadOnlyList<bool>? bassWalkingByBar = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);
        if (bars.Count != arrangements.Count) throw new ArgumentException("Bars and arrangements must have the same length.");
        if (bars.Any(bar => bar.BeatsPerBar != 3)) throw new ArgumentException("Jazz waltz generation requires 3/4 bars.", nameof(bars));
        if (bassWalkingByBar is not null && bassWalkingByBar.Count != bars.Count)
            throw new ArgumentException("Bass walking state must have one value per bar.", nameof(bassWalkingByBar));

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var barTicks = SessionConstants.GetBarTicks(3);
        var segmentLength = (long)bars.Count * barTicks;
        var notes = new List<ScheduledNote>(bars.Count * 10);
        var cells = new int[bars.Count];
        IReadOnlyList<byte> lastVoicing = previousVoicing ?? Array.Empty<byte>();
        long occupiedUntil = -1;
        ChordSpec? heldHarmony = null;
        var (sentence, sentenceIndex) = SelectSentence(stage, seed, previousCellIndex);

        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var nextBarChord = barIndex + 1 < bars.Count ? bars[barIndex + 1].Chord : followingChord;
            var hemiolaBar = hemiolaPlan.ContainsBar(barIndex);
            var arrangement = arrangements[barIndex];
            var bassWalking = bassWalkingByBar?[barIndex] ?? stage is WaltzChorusStage.Developing or WaltzChorusStage.Lifted;
            var stateSentence = stage == WaltzChorusStage.Standard
                ? (bassWalking ? WalkingStandardSentences[sentenceIndex] : NonWalkingStandardSentences[sentenceIndex])
                : sentence;
            var baseOffsets = hemiolaPlan.IsFirstBar(barIndex)
                ? new[] { 0L, 960L }
                : hemiolaPlan.IsSecondBar(barIndex)
                    ? new[] { 480L, 1280L }
                    : stateSentence[barIndex % stateSentence.Length];
            var offsets = BuildOffsets(
                bar,
                baseOffsets,
                arrangement,
                stage,
                guidance,
                seed,
                barIndex,
                hemiolaBar,
                bassWalking);
            cells[barIndex] = hemiolaBar
                ? 780 + (hemiolaPlan.IsSecondBar(barIndex) ? 1 : 0)
                : 7000 + (int)stage * 100 + sentenceIndex * 10 + barIndex % 4;
            var barStart = (long)barIndex * barTicks;

            for (var hitIndex = 0; hitIndex < offsets.Count; hitIndex++)
            {
                var offset = offsets[hitIndex];
                var chord = ResolveChord(bar, nextBarChord, offset);
                chord = ChordFactory.ApplyMinorTargetTensions(
                    chord,
                    ChordFactory.GetFollowingChord(bar, offset, nextBarChord));
                var voicingPrevious = ShouldRefreshTopVoice(stage, seed, barIndex, hitIndex)
                    ? Array.Empty<byte>()
                    : lastVoicing;
                var voicing = VoiceLead(chord, voicingPrevious, stage, seed, barIndex, hitIndex);
                var start = barStart + offset + 5 + (long)Math.Round(DeterministicNoise.Unit(seed, barIndex, hitIndex, 3201) * 5 - 2);
                if (start >= segmentLength) continue;

                // A 3& anticipation already states the next bar.  Let it ring
                // across the barline instead of producing a mechanical second
                // attack on beat 1.  This only suppresses the same harmony; a
                // genuine written change still gets a new voicing.
                if (offset == 0 && !hemiolaPlan.IsFirstBar(barIndex) &&
                    start < occupiedUntil && heldHarmony is not null && SameHarmony(heldHarmony, chord))
                {
                    continue;
                }

                var nextOffset = hitIndex + 1 < offsets.Count ? offsets[hitIndex + 1] : barTicks;
                var duration = Math.Min(
                    GetDuration(stage, bassWalking, offset, nextOffset, hemiolaBar, seed, barIndex, hitIndex),
                    segmentLength - start);
                var stageLift = stage switch
                {
                    WaltzChorusStage.Lifted => 2,
                    WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => -2,
                    _ => 0
                };
                var interactionLift = guidance.HighStage ? 2 : 0;
                var phraseLift = arrangement.Function switch
                {
                    PhraseFunction.Build => 3,
                    PhraseFunction.Setup => 2,
                    PhraseFunction.Answer => 1,
                    PhraseFunction.Space => -4,
                    PhraseFunction.Release => -2,
                    _ => 0
                };
                var syncopationLift = offset % SessionConstants.Ppq == 0 ? 0 : 2;
                var hemiolaLift = hemiolaPlan.IsAnchor(barIndex, offset) ? 6 : 0;
                var velocity = (byte)Math.Clamp(49 + stageLift + interactionLift + phraseLift + syncopationLift + hemiolaLift, 39, 68);
                foreach (var noteNumber in voicing)
                {
                    notes.Add(new ScheduledNote(start, duration, noteNumber, velocity, SessionConstants.PianoChannel));
                }

                lastVoicing = voicing;
                if (offset >= 1280)
                {
                    occupiedUntil = Math.Max(occupiedUntil, start + duration);
                    heldHarmony = chord;
                }
                else if (start >= occupiedUntil)
                {
                    heldHarmony = null;
                }
            }
        }

        return new PianoGenerationResult(ScheduledNoteOverlapGuard.TrimSamePitchOverlaps(notes), lastVoicing, cells[^1], cells);
    }

    private static (IReadOnlyList<long>[] Sentence, int Index) SelectSentence(
        WaltzChorusStage stage,
        int seed,
        int previousCellIndex)
    {
        var source = stage switch
        {
            WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => OpeningSentences,
            WaltzChorusStage.Standard => StandardSentences,
            WaltzChorusStage.Developing => DevelopingSentences,
            _ => LiftedSentences
        };
        var index = (int)(DeterministicNoise.Unit(seed, (int)stage, 3197) * source.Length) % source.Length;
        var stageBase = 7000 + (int)stage * 100;
        var previousSentence = previousCellIndex >= stageBase && previousCellIndex < stageBase + 100
            ? (previousCellIndex - stageBase) / 10
            : -1;
        if (source.Length > 1 && index == previousSentence)
        {
            index = (index + 1) % source.Length;
        }

        return (source[index], index);
    }

    private static long GetDuration(
        WaltzChorusStage stage,
        bool bassWalking,
        long offset,
        long nextOffset,
        bool hemiolaBar,
        int seed,
        int barIndex,
        int hitIndex)
    {
        var available = nextOffset - offset;
        if (offset >= 1280)
        {
            available = Math.Max(available, 400);
        }

        var maximum = stage switch
        {
            WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => 1320,
            WaltzChorusStage.Standard when !bassWalking => 1120,
            WaltzChorusStage.Standard => 860,
            WaltzChorusStage.Developing when !bassWalking => 1040,
            WaltzChorusStage.Developing => 760,
            _ => 540
        };
        var sustainProbability = stage switch
        {
            WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => 0.82,
            WaltzChorusStage.Standard when !bassWalking => 0.80,
            WaltzChorusStage.Standard => 0.68,
            WaltzChorusStage.Developing when !bassWalking => 0.86,
            WaltzChorusStage.Developing => 0.64,
            _ => 0.34
        };
        var duration = Math.Min(maximum, Math.Max(140, available - 56));
        if (!hemiolaBar && DeterministicNoise.Unit(seed, barIndex, hitIndex, 3217) > sustainProbability)
        {
            duration = Math.Min(duration, stage == WaltzChorusStage.Lifted ? 260 : 360);
        }

        return duration;
    }

    private static IReadOnlyList<long> BuildOffsets(
        TuneBar bar,
        IReadOnlyList<long> baseOffsets,
        BarArrangement arrangement,
        WaltzChorusStage stage,
        PerformanceGuidance guidance,
        int seed,
        int barIndex,
        bool hemiolaBar,
        bool bassWalking)
    {
        var offsets = baseOffsets.ToList();
        var structural = bar.ChordChanges.Skip(1)
            .Select(change => (long)change.StartBeat * SessionConstants.Ppq)
            .ToHashSet();
        foreach (var changeTick in structural)
        {
            if (!offsets.Any(offset => Math.Abs(offset - changeTick) <= SessionConstants.Ppq / 3))
            {
                offsets.Add(changeTick);
            }
        }

        if (stage == WaltzChorusStage.Standard && !bassWalking)
        {
            var desired = arrangement.Function is PhraseFunction.Build or PhraseFunction.Setup ? 2 : 1;
            while (offsets.Count > desired)
            {
                var removable = offsets.Where(offset => !structural.Contains(offset)).ToArray();
                if (removable.Length == 0) break;
                offsets.Remove(removable
                    .OrderBy(offset => DeterministicNoise.Unit(seed, barIndex, (int)offset, 3205))
                    .First());
            }
        }
        else if (stage is WaltzChorusStage.Opening or WaltzChorusStage.HeadOut)
        {
            var desired = 1;
            while (offsets.Count > desired)
            {
                var removable = offsets.Where(offset => !structural.Contains(offset)).ToArray();
                if (removable.Length == 0) break;
                var remove = removable
                    .OrderBy(offset => DeterministicNoise.Unit(seed, barIndex, (int)offset, 3205))
                    .First();
                offsets.Remove(remove);
            }
        }
        else if (arrangement.Function == PhraseFunction.Space && offsets.Count > 1)
        {
            var removable = offsets.Where(offset => !structural.Contains(offset)).ToArray();
            if (removable.Length > 0)
            {
                offsets.Remove(removable
                    .OrderBy(offset => DeterministicNoise.Unit(seed, barIndex, (int)offset, 3207))
                    .First());
            }
        }
        else if (!hemiolaBar &&
            stage == WaltzChorusStage.Lifted &&
            arrangement.Function != PhraseFunction.Space &&
            offsets.Count < 3)
        {
            // The lifted chorus is the ensemble peak, not a cue to double every
            // beat.  Bass and ride already carry a continuous pulse here, so
            // keep piano to a two-hit conversational budget.  The old target of
            // three hits made 320/480-tick cells overlap and sounded busy even
            // when each individual voicing was reasonable.
            const int targetCount = 2;
            var additionProbability = arrangement.Function switch
            {
                PhraseFunction.Build or PhraseFunction.Setup => 0.30,
                PhraseFunction.Answer => 0.24,
                PhraseFunction.Comment => 0.18,
                PhraseFunction.Ground => 0.12,
                _ => 0.08
            };
            if (guidance.HighStage)
            {
                // High-stage guidance raises authority (velocity and accents),
                // but must not raise attack count.  Preserve a little chance of
                // a single response when a sentence only has one hit.
                additionProbability = Math.Max(additionProbability, 0.34);
            }
            // Prefer the offbeats and the final 3& anticipation. Beat-center
            // additions remain available, but are deliberately secondary.
            var candidates = new[] { 320L, 800L, 1280L, 480L, 960L }
                .Where(value => !offsets.Any(offset => Math.Abs(offset - value) < 120))
                .OrderBy(value => DeterministicNoise.Unit(seed, barIndex, (int)value, 3211))
                .ToArray();
            foreach (var candidate in candidates)
            {
                if (offsets.Count >= targetCount || offsets.Count >= 3)
                {
                    break;
                }

                if (DeterministicNoise.Unit(seed, barIndex, (int)candidate, 3209) < additionProbability)
                {
                    offsets.Add(candidate);
                }
            }
        }

        if (!hemiolaBar && arrangement.IsTransitionLeadIn &&
            !offsets.Any(offset => Math.Abs(offset - 1280) < 120) && offsets.Count < 2)
        {
            // A single swung 3& anticipation is enough to carry a waltz phrase
            // into the next chorus; it is never a replacement for the bass pulse.
            offsets.Add(1280);
        }

        return offsets.Distinct().Where(offset => offset is >= 0 and < 1440).Order().Take(4).ToArray();
    }

    private static ChordSpec ResolveChord(TuneBar bar, ChordSpec nextBarChord, long offset)
    {
        // The final swung eighth is a conventional harmonic anticipation.
        if (offset >= 1280)
        {
            return nextBarChord;
        }

        var imminentChange = bar.ChordChanges.FirstOrDefault(change =>
            change.StartBeat * SessionConstants.Ppq > offset &&
            change.StartBeat * SessionConstants.Ppq - offset <= 160);
        return imminentChange?.Chord ?? bar.GetChordAtTick(Math.Min(offset, bar.BarTicks - 1));
    }

    private static bool SameHarmony(ChordSpec first, ChordSpec second) =>
        first.RootPitchClass == second.RootPitchClass && first.Symbol == second.Symbol;

    private static IReadOnlyList<byte> VoiceLead(
        ChordSpec chord,
        IReadOnlyList<byte> previous,
        WaltzChorusStage stage,
        int seed,
        int barIndex,
        int hitIndex)
    {
        var available = chord.PianoPitchClasses.Distinct().Count();
        if (available == 0)
        {
            return new[] { (byte)60, (byte)64, (byte)67 };
        }

        var selector = DeterministicNoise.Unit(seed, barIndex, hitIndex, 3231);
        var voiceCount = stage switch
        {
            WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => selector < 0.18 ? 4 : 3,
            WaltzChorusStage.Standard => selector < 0.24 ? 4 : 3,
            WaltzChorusStage.Developing => selector < 0.30 ? 4 : 3,
            // Keep the peak transparent: three-note rootless shells are enough
            // when bass, ride and the soloist are all active.  A four-note hit
            // remains available only as a rare deterministic colour.
            _ => selector < 0.08 ? 4 : 3
        };
        voiceCount = Math.Min(voiceCount, available);
        return PianoVoicingVocabulary.Choose(
            chord.PianoPitchClasses,
            previous,
            voiceCount,
            lower: 48,
            upper: 76,
            targetCenter: 62.5,
            PianoVoicingStyle.Waltz,
            chord.RootPitchClass,
            seed,
            barIndex,
            hitIndex);
    }

    private static bool ShouldRefreshTopVoice(
        WaltzChorusStage stage,
        int seed,
        int barIndex,
        int hitIndex) =>
        stage switch
        {
            WaltzChorusStage.Opening or WaltzChorusStage.HeadOut =>
                DeterministicNoise.Unit(seed, barIndex, hitIndex, 3241) < 0.05,
            WaltzChorusStage.Standard =>
                DeterministicNoise.Unit(seed, barIndex, hitIndex, 3241) < 0.14,
            WaltzChorusStage.Developing =>
                DeterministicNoise.Unit(seed, barIndex, hitIndex, 3241) < 0.34,
            _ => DeterministicNoise.Unit(seed, barIndex, hitIndex, 3241) < 0.42
        };
}
