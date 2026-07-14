using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BalladPianoCompingGenerator
{
    private static readonly long[][] ThemePatterns =
    [
        [0],
        [960],
        [320, 1280],
        [800, 1760]
    ];

    private static readonly long[][] QuietPatterns =
    [
        [480],
        [1280],
        [320, 1120],
        [960, 1760]
    ];

    private static readonly long[][] MovingPatterns =
    [
        [320, 1280],
        [480, 1440],
        [800, 1760],
        [0, 1120]
    ];

    private static readonly long[][] FourFeelPatterns =
    [
        [320, 960],
        [480, 1280],
        [0, 800, 1760],
        [480, 1440]
    ];

    public static PianoGenerationResult Generate(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<BalladChorusStage> stages,
        IReadOnlyList<byte>? previousVoicing,
        int previousCellIndex,
        int seed,
        PerformanceGuidance? performanceGuidance = null)
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
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var notes = new List<ScheduledNote>(bars.Count * 12);
        var cells = new int[bars.Count];
        IReadOnlyList<byte> lastVoicing = previousVoicing ?? Array.Empty<byte>();

        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var stage = stages[barIndex];
            var nextBarChord = barIndex + 1 < bars.Count ? bars[barIndex + 1].Chord : followingChord;
            var offsets = BuildOffsets(bar, arrangements[barIndex], stage, guidance, seed, barIndex);
            cells[barIndex] = 800 + (int)stage * 10 + barIndex % 4;
            var barStart = (long)barIndex * SessionConstants.BarTicks;

            for (var hitIndex = 0; hitIndex < offsets.Count; hitIndex++)
            {
                var offset = offsets[hitIndex];
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
                var baseDuration = stage switch
                {
                    BalladChorusStage.Theme or BalladChorusStage.HeadOut => 1_280,
                    BalladChorusStage.QuietSolo => 1_060,
                    BalladChorusStage.MovingTwoFeel => 760,
                    BalladChorusStage.FourFeel => 560,
                    _ => 800
                };
                var nextOffset = hitIndex + 1 < offsets.Count ? offsets[hitIndex + 1] : SessionConstants.BarTicks;
                var duration = Math.Clamp(nextOffset - offset + 260, 360, baseDuration);
                if (stage == BalladChorusStage.FourFeel &&
                    DeterministicNoise.Unit(seed, barIndex, hitIndex, 7233) < 0.38)
                {
                    // A ballad can use an occasional concise answer in four-feel;
                    // it remains a phrase punctuation, never a double-time layer.
                    duration = Math.Min(duration, 320);
                }
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
            }
        }

        return new PianoGenerationResult(ScheduledNoteOverlapGuard.TrimSamePitchOverlaps(notes), lastVoicing, cells[^1], cells);
    }

    private static IReadOnlyList<long> BuildOffsets(
        TuneBar bar,
        BarArrangement arrangement,
        BalladChorusStage stage,
        PerformanceGuidance guidance,
        int seed,
        int barIndex)
    {
        var source = stage switch
        {
            BalladChorusStage.Theme or BalladChorusStage.HeadOut => ThemePatterns,
            BalladChorusStage.QuietSolo => QuietPatterns,
            BalladChorusStage.MovingTwoFeel => MovingPatterns,
            BalladChorusStage.FourFeel => FourFeelPatterns,
            _ => QuietPatterns
        };
        var offsets = source[barIndex % source.Length].ToList();

        foreach (var change in bar.ChordChanges.Skip(1))
        {
            var changeTick = (long)change.StartBeat * SessionConstants.Ppq;
            if (!offsets.Any(offset => Math.Abs(offset - changeTick) <= SessionConstants.Ppq / 2))
            {
                offsets.Add(changeTick);
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

        if (arrangement.Function is PhraseFunction.Build or PhraseFunction.Setup &&
            stage is BalladChorusStage.MovingTwoFeel or BalladChorusStage.FourFeel &&
            !offsets.Contains(1760) &&
            DeterministicNoise.Unit(seed, barIndex, 7211) < 0.45)
        {
            offsets.Add(1760);
        }

        var maximum = stage switch
        {
            BalladChorusStage.Theme or BalladChorusStage.HeadOut => 2,
            BalladChorusStage.QuietSolo => 2,
            BalladChorusStage.MovingTwoFeel => 3,
            BalladChorusStage.FourFeel => 3,
            _ => 2
        };
        return offsets.Distinct().Order().Take(maximum).ToArray();
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
