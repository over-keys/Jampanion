using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BalladPianoCompingGenerator
{
    // The reference ballad keeps a calm two-attack average without leaving the
    // harmony exposed. Duplicate entries are intentional weights: downbeat-led
    // statements dominate, while the denser triplet answers stay occasional.
    private static readonly long[][] CalmPatterns =
    [
        [0], [0], [0], [0], [0],
        [0, 800], [0, 800], [0, 800],
        [320], [320],
        [0, 1280],
        [0, 1440],
        [0, 320, 1440], [0, 320, 1440],
        [0, 960, 1280],
        [0, 320, 960]
    ];

    // Four-feel does not make the ballad piano busier. These weights combine
    // the long single attacks of Ballad.mid with the restrained, held replies
    // in Swing.mid. The bass and ride carry the increased pulse.
    private static readonly long[][] FourFeelPatterns =
    [
        [0], [0], [0], [0], [0], [0],
        [320], [320],
        [0, 800], [0, 800], [0, 800],
        [0, 1280], [0, 1280],
        [0, 1440], [0, 1440],
        [0, 320, 1440]
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
        bool previousSegmentEndedOnFourAnd = false)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);
        ArgumentNullException.ThrowIfNull(stages);
        if (bars.Count != arrangements.Count || bars.Count != stages.Count)
        {
            throw new ArgumentException("Bars, arrangements, and ballad stages must have the same length.");
        }

        if (stages.All(stage => stage == BalladChorusStage.FourFeel))
        {
            // Reuse the Swing.mid model directly instead of maintaining a
            // ballad-specific approximation of it. Restrained opening selects
            // the measured, sustained four-beat sentences and excludes the
            // short high-stage vocabulary; bass and ride supply the extra drive.
            return PianoCompingGenerator.Generate(
                bars,
                followingChord,
                RhythmFeel.FourBeat,
                arrangements,
                previousVoicing,
                previousCellIndex,
                seed,
                PerformanceGuidance.Neutral,
                restrainedOpening: true,
                previousSegmentEndedOnFourAnd: previousSegmentEndedOnFourAnd);
        }

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var notes = new List<ScheduledNote>(bars.Count * 12);
        var cells = new int[bars.Count];
        IReadOnlyList<byte> lastVoicing = previousVoicing ?? Array.Empty<byte>();
        var previousBarEndedOnFourAnd = previousSegmentEndedOnFourAnd;
        var segmentEndedOnFourAnd = false;

        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var currentBarEndedOnFourAnd = false;
            var bar = bars[barIndex];
            var stage = stages[barIndex];
            var nextBarChord = barIndex + 1 < bars.Count ? bars[barIndex + 1].Chord : followingChord;
            var offsets = BuildOffsets(bar, arrangements[barIndex], stage, guidance, seed, barIndex);
            cells[barIndex] = 800 + (int)stage * 10 + barIndex % 4;
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
                var voiceCount = SelectVoiceCount(chord, stage, seed, barIndex, hitIndex);
                var voicing = VoiceLead(chord, voiceCount, lastVoicing, stage, guidance, seed, barIndex, hitIndex);
                var rolled = stage is BalladChorusStage.Theme or BalladChorusStage.QuietSolo or BalladChorusStage.HeadOut &&
                    DeterministicNoise.Unit(seed, barIndex, hitIndex, 7201) < 0.54;
                var stageLift = stage switch
                {
                    BalladChorusStage.Theme or BalladChorusStage.HeadOut => -5,
                    BalladChorusStage.QuietSolo => -3,
                    BalladChorusStage.MovingTwoFeel => 0,
                    BalladChorusStage.FourFeel => 2,
                    _ => 0
                };
                var interactionLift = guidance.HighStage ? 2 : 0;
                var velocity = Math.Clamp(50 + stageLift + interactionLift + arrangements[barIndex].DynamicLift / 3, 40, 68);
                var nextOffset = hitIndex + 1 < offsets.Count ? offsets[hitIndex + 1] : SessionConstants.BarTicks;
                var duration = ResolveDuration(stage, offset, nextOffset);
                var humanDelay = 7L + (long)Math.Round(DeterministicNoise.Unit(seed, barIndex, hitIndex, 7203) * 6);

                for (var voiceIndex = 0; voiceIndex < voicing.Count; voiceIndex++)
                {
                    var rollDelay = rolled ? voiceIndex * 7L : 0L;
                    var start = barStart + offset + humanDelay + rollDelay;
                    if (start >= segmentLength)
                    {
                        continue;
                    }

                    notes.Add(new ScheduledNote(
                        start,
                        Math.Min(duration, segmentLength - start),
                        voicing[voiceIndex],
                        (byte)Math.Clamp(velocity - (rolled ? voiceIndex : 0), 38, 70),
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
            ScheduledNoteOverlapGuard.TrimSamePitchOverlaps(notes),
            lastVoicing,
            cells[^1],
            cells,
            segmentEndedOnFourAnd);
    }

    private static IReadOnlyList<long> BuildOffsets(
        TuneBar bar,
        BarArrangement arrangement,
        BalladChorusStage stage,
        PerformanceGuidance guidance,
        int seed,
        int barIndex)
    {
        var source = stage == BalladChorusStage.FourFeel ? FourFeelPatterns : CalmPatterns;
        var patternIndex = (int)(DeterministicNoise.Unit(seed, barIndex, (int)stage, 7205) * source.Length) %
            source.Length;
        var offsets = source[patternIndex].ToList();

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

        var maximum = stage == BalladChorusStage.FourFeel ? 3 : 4;
        return offsets.Distinct().Order().Take(maximum).ToArray();
    }

    private static long ResolveDuration(BalladChorusStage stage, long offset, long nextOffset)
    {
        // The model performance releases almost exactly at the following attack.
        // A single hit therefore fills the bar, while a dense answer naturally
        // produces short notes without applying a separate staccato rule.
        var releaseGap = stage switch
        {
            // Leave a small tail of air after a long theme chord. The chord
            // still carries almost the whole bar, but it no longer runs
            // continuously into the next statement.
            BalladChorusStage.Theme => 180L,
            BalladChorusStage.HeadOut => 8L,
            BalladChorusStage.QuietSolo => 24L,
            BalladChorusStage.MovingTwoFeel => 20L,
            BalladChorusStage.FourFeel => 28L,
            _ => 16L
        };
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
            BalladChorusStage.Theme or BalladChorusStage.HeadOut => selector < 0.16 ? 2 : 3,
            BalladChorusStage.QuietSolo => selector < 0.12 ? 2 : 3,
            BalladChorusStage.MovingTwoFeel => selector < 0.20 ? 2 : 3,
            BalladChorusStage.FourFeel => selector < 0.18 ? 3 : 4,
            _ => 3
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
}
