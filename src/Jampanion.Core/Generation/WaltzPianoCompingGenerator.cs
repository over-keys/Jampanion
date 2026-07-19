using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class WaltzPianoCompingGenerator
{
    private static readonly long[][][] OpeningSentences =
    [
        [[0], [320, 800], [0, 800], [320]],
        [[0, 800], [0], [320, 800], [0, 480]],
        [[320, 800], [0, 800], [0, 1280], []],
        [[480], [0, 960], [320, 800], [0]],
        [[0, 960], [800], [0, 480], [320]]
    ];

    private static readonly long[][][] StandardSentences =
    [
        [[0, 800], [320, 800], [0, 800], [320]],
        [[0, 480], [0, 800], [0, 1280], [320]],
        [[320, 800], [0, 800], [320, 800], [0]],
        [[480], [0, 960], [320, 800], [0, 1280]],
        [[0, 960], [800], [480, 1280], [320]]
    ];

    // Before the bass walks, the piano carries the harmony with one broad
    // statement per bar and only occasional 2:3 punctuation.  These are not
    // silent-by-rule cells: the duration engine decides whether the statement
    // becomes a held block or a true rest after an ensemble response.
    private static readonly long[][][] NonWalkingStandardSentences =
    [
        [[0, 800], [320, 800], [0, 800], [320]],
        [[0, 480], [0, 800], [0, 1280], [320]],
        [[320, 800], [0, 800], [320, 800], [0]],
        [[480], [0, 960], [0, 800], [320]],
        [[0], [800], [0, 1280], [480]]
    ];

    // Once the bass owns all three beats, the piano moves around it rather than
    // doubling the pulse.  1&/2&/3& are the waltz equivalents of a swung
    // syncopation; 3& is reserved as a next-bar anticipation.
    private static readonly long[][][] WalkingStandardSentences =
    [
        [[320, 800], [0], [0, 1280], []],
        [[0, 800], [320, 800], [0, 480], [320]],
        [[320], [0, 800], [320, 800], [0]],
        [[480], [320, 960], [800], [0, 1280]],
        [[0, 960], [800], [480], [320, 800]]
    ];

    private static readonly long[][][] DevelopingSentences =
    [
        [[320, 800], [0, 800], [320, 800], [0]],
        [[0, 800], [320], [0, 1280], [320]],
        [[320, 800], [320, 800], [0], [0, 800]],
        [[480], [0, 960], [320, 800], [0, 1280]],
        [[0, 960], [800], [0, 480], [320]]
    ];

    private static readonly long[][][] LiftedSentences =
    [
        // At the ensemble peak, leave explicit air around the soloist and
        // walking bass.  Two of the four bars speak once; the other two use a
        // restrained answer/anticipation cell rather than filling all three
        // beats.
        [[320, 800], [320], [0, 800], [0]],
        [[0, 800], [320], [0, 480], [320, 800]],
        [[320], [0, 800], [0, 1280], []],
        [[480], [320, 960], [800], [0, 1280]],
        [[0, 960], [800], [480, 1280], [320]],
        [[960], [0], [320, 800], [480]]
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
        IReadOnlyList<bool>? bassWalkingByBar = null,
        TimeFeelProfile? timeFeel = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);
        if (bars.Count != arrangements.Count) throw new ArgumentException("Bars and arrangements must have the same length.");
        if (bars.Any(bar => bar.BeatsPerBar != 3)) throw new ArgumentException("Jazz waltz generation requires 3/4 bars.", nameof(bars));
        if (bassWalkingByBar is not null && bassWalkingByBar.Count != bars.Count)
            throw new ArgumentException("Bass walking state must have one value per bar.", nameof(bassWalkingByBar));

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var timing = timeFeel ?? TimeFeelProfile.Resolve(AccompanimentStyle.JazzWaltz, 140);
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
            // The model piano does not double the rhythm section's 2:3
            // hemiola. Preserve its measured one-bar comping sentence while
            // bass and drums may articulate the cross-bar figure.
            var baseOffsets = stateSentence[barIndex % stateSentence.Length];
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
                if (chord.IsNoChord)
                {
                    continue;
                }
                var voicingPrevious = ShouldRefreshTopVoice(stage, seed, barIndex, hitIndex)
                    ? Array.Empty<byte>()
                    : lastVoicing;
                var voicing = VoiceLead(chord, voicingPrevious, stage, seed, barIndex, hitIndex);
                var start = timing.Place(barStart + offset, TimeFeelRole.Piano) +
                    timing.MillisecondsToTicks(
                        (DeterministicNoise.Unit(seed, barIndex, hitIndex, 3201) - 0.5) * 2.0);
                if (start >= segmentLength) continue;

                // A 3& anticipation already states the next bar.  Let it ring
                // across the barline instead of producing a mechanical second
                // attack on beat 1.  This only suppresses the same harmony; a
                // genuine written change still gets a new voicing.
                if (offset == 0 &&
                    start < occupiedUntil && heldHarmony is not null && SameHarmony(heldHarmony, chord))
                {
                    continue;
                }

                var nextOffset = hitIndex + 1 < offsets.Count ? offsets[hitIndex + 1] : barTicks;
                var duration = Math.Min(
                    timing.ScaleGate(
                        GetDuration(stage, bassWalking, offset, nextOffset, hemiolaBar, seed, barIndex, hitIndex),
                    TimeFeelRole.Piano),
                    segmentLength - start);
                var nextAttackStart = segmentLength;
                if (hitIndex + 1 < offsets.Count)
                {
                    nextAttackStart = timing.Place(
                        barStart + offsets[hitIndex + 1],
                        TimeFeelRole.Piano) +
                        timing.MillisecondsToTicks(
                            (DeterministicNoise.Unit(seed, barIndex, hitIndex + 1, 3201) - 0.5) * 2.0);
                }
                duration = Math.Min(duration, Math.Max(1, nextAttackStart - start));
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
                var velocity = (byte)Math.Clamp(
                    54 + stageLift + interactionLift + phraseLift + syncopationLift + hemiolaLift -
                    (arrangement.IsTransitionLeadIn ? 2 : 0),
                    48,
                    72);
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

        return new PianoGenerationResult(notes, lastVoicing, cells[^1], cells);
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
        _ = bassWalking;
        _ = hemiolaBar;
        var selector = DeterministicNoise.Unit(seed, barIndex, hitIndex, 3217);

        if (offset >= 1280)
        {
            var anticipationValues = stage switch
            {
                WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => new[] { 960L, 1040L, 1120L },
                WaltzChorusStage.Standard => new[] { 880L, 960L, 1040L },
                _ => new[] { 760L, 880L, 1000L }
            };
            return PickDuration(anticipationValues, selector);
        }

        if (offset == 480)
        {
            var beatTwoValues = stage switch
            {
                WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => new[] { 720L, 800L, 880L },
                WaltzChorusStage.Standard => new[] { 680L, 760L, 840L },
                _ => new[] { 600L, 680L, 760L }
            };
            return PickDuration(beatTwoValues, selector);
        }

        if (offset == 320)
        {
            if (nextOffset <= 800)
            {
                return selector < 0.55 ? 80 : 120;
            }

            // In the 1& -> 3 answer, the first chord is a connected middle
            // value, not the long single 1& statement. Release it before beat 3
            // so the reply speaks clearly rather than sounding like a re-roll.
            if (nextOffset <= 960)
            {
                return PickDuration(
                    stage is WaltzChorusStage.Opening or WaltzChorusStage.HeadOut
                        ? new[] { 520L, 560L, 600L }
                        : new[] { 480L, 540L, 600L },
                    selector);
            }

            return PickDuration(
                    stage is WaltzChorusStage.Opening or WaltzChorusStage.HeadOut
                        ? new[] { 760L, 840L, 920L }
                        : new[] { 680L, 760L, 840L },
                    selector);
        }

        if (offset == 800)
        {
            if (nextOffset >= SessionConstants.GetBarTicks(3))
            {
                var heldOffbeatValues = stage switch
                {
                    WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => new[] { 440L, 520L, 600L },
                    WaltzChorusStage.Standard => new[] { 360L, 440L, 520L },
                    _ => new[] { 280L, 360L, 440L, 520L }
                };
                return PickDuration(heldOffbeatValues, selector);
            }

            if (stage is WaltzChorusStage.Opening or WaltzChorusStage.HeadOut)
            {
                return selector switch
                {
                    < 0.08 => 160,
                    < 0.22 => 240,
                    < 0.42 => 320,
                    < 0.68 => 360,
                    < 0.86 => 440,
                    _ => 480
                };
            }

            if (stage is WaltzChorusStage.Developing or WaltzChorusStage.Lifted)
            {
                return selector switch
                {
                    < 0.22 => 120,
                    < 0.48 => 160,
                    < 0.68 => 240,
                    < 0.84 => 320,
                    < 0.94 => 360,
                    _ => 440
                };
            }

            return selector switch
            {
                < 0.14 => 160,
                < 0.36 => 240,
                < 0.64 => 320,
                < 0.86 => 360,
                _ => 440
            };
        }

        if (offset == 960)
        {
            var beatThreeValues = stage switch
            {
                WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => new[] { 320L, 400L, 440L },
                WaltzChorusStage.Standard => new[] { 280L, 360L, 440L },
                _ => new[] { 200L, 280L, 360L, 440L }
            };
            return PickDuration(beatThreeValues, selector);
        }

        if (offset == 0 && nextOffset == 480)
        {
            return 160;
        }

        if (offset == 0 && nextOffset == 960)
        {
            var beatThreeAnswerValues = stage switch
            {
                WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => new[] { 760L, 840L, 900L },
                WaltzChorusStage.Standard => new[] { 680L, 760L, 840L },
                _ => new[] { 560L, 680L, 800L }
            };
            return PickDuration(beatThreeAnswerValues, selector);
        }

        if (offset == 0 && nextOffset >= 1280 && nextOffset < 1440)
        {
            return 1040;
        }

        if (offset == 0 && nextOffset <= 800)
        {
            var shortProbability = stage switch
            {
                WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => 0.08,
                WaltzChorusStage.Standard => 0.18,
                WaltzChorusStage.Developing => 0.32,
                _ => 0.42
            };
            if (selector < shortProbability)
            {
                var shortValues = new[] { 160L, 240L, 320L };
                return shortValues[Math.Min(
                    (int)(selector / shortProbability * shortValues.Length),
                    shortValues.Length - 1)];
            }

            var connectedValues = new[] { 560L, 640L, 680L };
            var normalized = (selector - shortProbability) / (1.0 - shortProbability);
            return connectedValues[Math.Min(
                (int)(normalized * connectedValues.Length),
                connectedValues.Length - 1)];
        }

        if (offset == 0)
        {
            var shortProbability = stage switch
            {
                WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => 0.08,
                WaltzChorusStage.Standard => 0.16,
                WaltzChorusStage.Developing => 0.28,
                _ => 0.36
            };
            if (selector < shortProbability)
            {
                return 320;
            }

            var longValues = new[] { 1000L, 1080L, 1120L, 1200L };
            var normalized = (selector - shortProbability) / (1.0 - shortProbability);
            return longValues[Math.Min((int)(normalized * longValues.Length), longValues.Length - 1)];
        }

        return Math.Max(120, Math.Min(480, nextOffset - offset - 40));
    }

    private static long PickDuration(IReadOnlyList<long> values, double selector) =>
        values[Math.Min(values.Count - 1, (int)Math.Floor(selector * values.Count))];

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
            .Select(change => Math.Max(0L,
                (long)change.StartBeat * SessionConstants.Ppq - SessionConstants.Ppq / 3))
            .ToHashSet();
        foreach (var changeTick in structural)
        {
            if (!offsets.Any(offset => Math.Abs(offset - changeTick) <= SessionConstants.Ppq / 3))
            {
                offsets.Add(changeTick);
            }
        }

        // Start/duration pairs are indivisible vocabulary. Removing one member
        // of a measured two-hit gesture changes the other member's role and
        // duration, so density reduction is limited to explicit space bars.
        if (arrangement.Function == PhraseFunction.Space && offsets.Count > 1)
        {
            var removable = offsets.Where(offset => !structural.Contains(offset)).ToArray();
            if (removable.Length > 0)
            {
                offsets.Remove(removable
                    .OrderBy(offset => DeterministicNoise.Unit(seed, barIndex, (int)offset, 3207))
                    .First());
            }
        }
        else if (arrangement.IsTransitionLeadIn && offsets.Count > 1)
        {
            // Keep the 1/2/3 pulse and any written harmony arrival, but remove
            // one secondary answer so the handoff has air.
            var removable = offsets
                .Where(offset => !structural.Contains(offset) && offset is not 0 and not 1280)
                .OrderBy(offset => DeterministicNoise.Unit(seed, barIndex, (int)offset, 3208))
                .FirstOrDefault(-1);
            if (removable >= 0)
            {
                offsets.Remove(removable);
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
            var candidates = new[] { 320L, 800L, 1280L, 480L }
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
            WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => selector < 0.28 ? 3 : 4,
            WaltzChorusStage.Standard => selector < 0.20 ? 3 : 4,
            WaltzChorusStage.Developing => selector < 0.16 ? 3 : 4,
            // Peak density comes from the rhythm section. Keep occasional
            // three-note transparency while retaining a compact harmonic body.
            _ => selector < 0.30 ? 3 : 4
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
