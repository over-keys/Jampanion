using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BossaPianoCompingGenerator
{
    // Four-bar sentences retain the two-bar Brazilian syncopation while varying
    // where the pianist breathes, answers, and anticipates the next harmony.
    private static readonly long[][][] OpeningSentences =
    [
        [[0], [720], [0, 1200], [240]],
        [[0, 1200], [240], [0], [720]],
        [[720], [0], [240, 1440], [0]]
    ];

    private static readonly long[][][] FirstSoloSentences =
    [
        [[0, 1200], [720], [240, 1440], [960, 1680]],
        [[240, 960], [0, 1440], [720], [240, 1200]],
        [[0, 720], [240, 1440], [960], [0, 1680]]
    ];

    private static readonly long[][][] StandardSentences =
    [
        [[0, 720, 1440], [240, 1200], [0, 960, 1680], [480, 1200]],
        [[240, 960], [0, 720, 1440], [480, 1200, 1680], [0, 1200]],
        [[0, 1200], [240, 720, 1440], [0, 960], [480, 1200, 1680]]
    ];

    private static readonly long[][][] LiftedSentences =
    [
        [[0, 720, 1200, 1680], [240, 960, 1440], [0, 480, 1200, 1680], [720, 1200, 1680]],
        [[240, 720, 1440], [0, 480, 960, 1680], [240, 960, 1440], [0, 720, 1200, 1680]],
        [[0, 480, 1200], [240, 720, 1440, 1680], [0, 960, 1440], [480, 1200, 1680]]
    ];

    public static PianoGenerationResult Generate(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<byte>? previousVoicing,
        int previousCellIndex,
        int seed,
        BossaChorusStage stage,
        PerformanceGuidance? performanceGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);
        if (bars.Count != arrangements.Count) throw new ArgumentException("Bars and arrangements must have the same length.");

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var notes = new List<ScheduledNote>(bars.Count * 14);
        var cells = new int[bars.Count];
        IReadOnlyList<byte> lastVoicing = previousVoicing ?? Array.Empty<byte>();
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var (sentence, sentenceIndex) = SelectSentence(stage, seed, previousCellIndex);

        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var nextBarChord = barIndex + 1 < bars.Count ? bars[barIndex + 1].Chord : followingChord;
            var offsets = BuildOffsets(
                bar, sentence[barIndex % sentence.Length], arrangements[barIndex], stage, guidance, seed, barIndex);
            cells[barIndex] = 4000 + (int)stage * 100 + sentenceIndex * 10 + barIndex % 4;
            var barStart = (long)barIndex * SessionConstants.BarTicks;

            for (var hitIndex = 0; hitIndex < offsets.Count; hitIndex++)
            {
                var offset = offsets[hitIndex];
                var chord = ResolveChord(bar, nextBarChord, offset);
                chord = ChordFactory.ApplyMinorTargetTensions(
                    chord,
                    ChordFactory.GetFollowingChord(bar, offset, nextBarChord));
                var voiceCount = SelectVoiceCount(chord, stage, seed, barIndex, hitIndex);
                var voicing = VoiceLead(chord, voiceCount, lastVoicing, seed, barIndex, hitIndex);
                // Bossa comping uses compact, guitar-like voicings. A random added
                // top octave makes the upper line jump without a melodic reason, so
                // later-stage lift comes from rhythm and articulation instead.
                var renderedVoicing = voicing;
                var start = barStart + offset + 6 + (long)Math.Round(DeterministicNoise.Unit(seed, barIndex, hitIndex, 2801) * 4 - 2);
                if (start >= segmentLength) continue;

                var nextOffset = hitIndex + 1 < offsets.Count ? offsets[hitIndex + 1] : SessionConstants.BarTicks;
                var duration = GetDuration(stage, offset, nextOffset, seed, barIndex, hitIndex);
                duration = Math.Min(duration, segmentLength - start);
                var arrangement = arrangements[barIndex];
                var chorusLift = stage == BossaChorusStage.Lifted ? 1 : stage is BossaChorusStage.Opening or BossaChorusStage.HeadOut ? -1 : 0;
                var lift = chorusLift;
                var phrase = arrangement.Function switch
                {
                    PhraseFunction.Build => 2,
                    PhraseFunction.Space => -3,
                    PhraseFunction.Release => -1,
                    _ => 0
                };
                var syncopated = offset % SessionConstants.Ppq != 0;
                var velocity = (byte)Math.Clamp((syncopated ? 51 : 48) + lift + phrase + (hitIndex == 0 ? 1 : 0), 40, 62);
                foreach (var noteNumber in renderedVoicing)
                {
                    notes.Add(new ScheduledNote(start, duration, noteNumber, velocity, SessionConstants.PianoChannel));
                }
                lastVoicing = voicing;
            }
        }

        return new PianoGenerationResult(ScheduledNoteOverlapGuard.TrimSamePitchOverlaps(notes), lastVoicing, cells[^1], cells);
    }

    private static (IReadOnlyList<long>[] Sentence, int Index) SelectSentence(
        BossaChorusStage stage,
        int seed,
        int previousCellIndex)
    {
        var source = stage switch
        {
            BossaChorusStage.Opening or BossaChorusStage.HeadOut => OpeningSentences,
            BossaChorusStage.FirstSolo => FirstSoloSentences,
            BossaChorusStage.Lifted => LiftedSentences,
            _ => StandardSentences
        };
        var index = (int)(DeterministicNoise.Unit(seed, (int)stage, 2800) * source.Length) % source.Length;
        var stageBase = 4000 + (int)stage * 100;
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
        BossaChorusStage stage,
        long offset,
        long nextOffset,
        int seed,
        int barIndex,
        int hitIndex)
    {
        var available = nextOffset - offset;
        if (offset >= 1680)
        {
            // The &4 anticipation belongs to the following harmony and may ring
            // across the barline until that harmony is restated.
            available = Math.Max(available, 540);
        }

        var maximum = stage switch
        {
            BossaChorusStage.Opening or BossaChorusStage.HeadOut => 960,
            BossaChorusStage.FirstSolo => 840,
            BossaChorusStage.Standard => 960,
            _ => 480
        };
        var sustainProbability = stage switch
        {
            BossaChorusStage.Opening => 0.48,
            BossaChorusStage.HeadOut => 0.56,
            BossaChorusStage.FirstSolo => 0.58,
            BossaChorusStage.Standard => 0.92,
            _ => 0.24
        };
        var duration = Math.Min(maximum, Math.Max(120, available - 48));
        if (DeterministicNoise.Unit(seed, barIndex, hitIndex, 2821) > sustainProbability)
        {
            var shortMaximum = stage == BossaChorusStage.Lifted ? 280 : 360;
            duration = Math.Min(duration, shortMaximum);
        }

        return duration;
    }

    private static IReadOnlyList<long> BuildOffsets(
        TuneBar bar,
        IReadOnlyList<long> baseOffsets,
        BarArrangement arrangement,
        BossaChorusStage stage,
        PerformanceGuidance guidance,
        int seed,
        int barIndex)
    {
        var offsets = baseOffsets.ToList();
        foreach (var change in bar.ChordChanges.Skip(1))
        {
            var changeTick = (long)change.StartBeat * SessionConstants.Ppq;
            if (!offsets.Any(offset => offset >= changeTick && offset - changeTick <= SessionConstants.Ppq / 2))
            {
                offsets.Add(changeTick);
            }
        }

        if (arrangement.Function == PhraseFunction.Space && offsets.Count > 3)
        {
            var removable = offsets.Where(offset => offset != 0 && offset != 1680).ToArray();
            if (removable.Length > 0)
            {
                var remove = removable[(int)(DeterministicNoise.Unit(seed, barIndex, 2805) * removable.Length) % removable.Length];
                offsets.Remove(remove);
            }
        }

        var structuralOffsets = bar.ChordChanges.Skip(1)
            .Select(change => (long)change.StartBeat * SessionConstants.Ppq)
            .ToHashSet();
        if (stage is BossaChorusStage.Opening or BossaChorusStage.HeadOut)
        {
            // The theme's guitar-like comp is a pair of gestures, not the full
            // solo ostinato softened by velocity. A written mid-bar harmony can
            // add one structural statement, but never turns every bar into a
            // continuous keyboard pattern.
            var desiredCount = structuralOffsets.Count == 0 ? 2 : 3;
            while (offsets.Count > desiredCount)
            {
                var removable = offsets
                    .Where(offset => !structuralOffsets.Contains(offset))
                    .OrderBy(offset => offset == 0 ? 1 : 0)
                    .ThenBy(offset => DeterministicNoise.Unit(seed, barIndex, (int)offset, 2809))
                    .ToArray();
                if (removable.Length == 0)
                {
                    break;
                }

                offsets.Remove(removable[0]);
            }
        }
        else if (stage == BossaChorusStage.Lifted &&
            arrangement.Function is PhraseFunction.Build or PhraseFunction.Setup or PhraseFunction.Answer &&
            offsets.Count < 5)
        {
            var candidates = new[] { 720L, 960L, 1440L, 1680L };
            var candidate = candidates
                .Where(value => !offsets.Any(offset => Math.Abs(offset - value) < SessionConstants.Ppq / 4))
                .OrderBy(value => DeterministicNoise.Unit(seed, barIndex, (int)value, 2813))
                .FirstOrDefault(-1);
            if (candidate >= 0)
            {
                offsets.Add(candidate);
            }
        }

        if (arrangement.IsTransitionLeadIn &&
            !offsets.Any(offset => Math.Abs(offset - 1680) < SessionConstants.Ppq / 4) &&
            offsets.Count < 5)
        {
            // A light &4 anticipation connects the two-bar bossa cell over the
            // chorus boundary without turning the opening texture into a montuno.
            offsets.Add(1680);
        }

        return offsets.Distinct().Order().Take(5).ToArray();
    }

    private static ChordSpec ResolveChord(TuneBar bar, ChordSpec nextBarChord, long offset)
    {
        if (offset >= 1680)
        {
            return nextBarChord;
        }

        var nextChange = bar.ChordChanges.FirstOrDefault(change =>
            change.StartBeat * SessionConstants.Ppq > offset &&
            change.StartBeat * SessionConstants.Ppq - offset <= SessionConstants.Ppq / 2);
        return nextChange?.Chord ?? bar.GetChordAtTick(Math.Min(offset, SessionConstants.BarTicks - 1));
    }

    private static int SelectVoiceCount(
        ChordSpec chord,
        BossaChorusStage stage,
        int seed,
        int barIndex,
        int hitIndex)
    {
        var available = chord.PianoPitchClasses.Distinct().Count();
        if (available < 4)
        {
            return Math.Max(3, available);
        }

        var threeNoteProbability = stage switch
        {
            BossaChorusStage.Opening or BossaChorusStage.HeadOut => 0.68,
            BossaChorusStage.FirstSolo => 0.56,
            BossaChorusStage.Standard => 0.44,
            _ => 0.30
        };
        return DeterministicNoise.Unit(seed, barIndex, hitIndex, 2831) < threeNoteProbability ? 3 : 4;
    }

    private static IReadOnlyList<byte> VoiceLead(
        ChordSpec chord,
        int voiceCount,
        IReadOnlyList<byte> previous,
        int seed,
        int barIndex,
        int hitIndex)
    {
        return PianoVoicingVocabulary.Choose(
            chord.PianoPitchClasses,
            previous,
            voiceCount,
            lower: 48,
            upper: 76,
            targetCenter: 62.5,
            PianoVoicingStyle.Bossa,
            chord.RootPitchClass,
            seed,
            barIndex,
            hitIndex);
    }
}
