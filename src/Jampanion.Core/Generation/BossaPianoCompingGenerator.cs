using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BossaPianoCompingGenerator
{
    // Blue Bossa.mid was measured across the full performance. The important
    // information is not just attack count: each attack is a (bar offset,
    // sounding duration) pair. The reference repeatedly uses short 1/8-note
    // punctuations followed by a longer offbeat sustain, especially the
    // 0-480-1200-1680 and 720-1680 cells below. Keep those measured pairs intact
    // instead of deriving a new duration from the next generated attack.
    private static readonly BossaRhythmCell RefPrimary = Cell(501,
        H(0, 240), H(480, 240), H(1200, 480), H(1680, 960));
    private static readonly BossaRhythmCell RefTwoGesture = Cell(502,
        H(720, 960), H(1680, 960));
    private static readonly BossaRhythmCell RefMidAnswer = Cell(503,
        H(720, 240), H(1200, 480));
    private static readonly BossaRhythmCell RefCompactFive = Cell(504,
        H(0, 240), H(240, 240), H(720, 240), H(1200, 480), H(1680, 960));
    private static readonly BossaRhythmCell RefLongMid = Cell(505, H(720, 960));
    private static readonly BossaRhythmCell RefThreeAnd = Cell(506,
        H(720, 240), H(1200, 480), H(1680, 960));
    private static readonly BossaRhythmCell RefLongAnticipation = Cell(507,
        H(0, 1680), H(1680, 960));
    private static readonly BossaRhythmCell RefFourStab = Cell(508,
        H(0, 240), H(960, 240), H(1200, 480), H(1680, 960));
    private static readonly BossaRhythmCell RefTwoLong = Cell(509,
        H(0, 720), H(720, 960));
    private static readonly BossaRhythmCell RefReleasePair = Cell(510,
        H(0, 1200), H(1200, 480));
    private static readonly BossaRhythmCell RefAnticipationOnly = Cell(511,
        H(1680, 960));
    private static readonly BossaRhythmCell RefMidWithFour = Cell(512,
        H(0, 240), H(240, 240), H(720, 240), H(1200, 720), H(1680, 960));
    private static readonly BossaRhythmCell RefPickupThree = Cell(513,
        H(240, 480), H(960, 720), H(1680, 960));
    private static readonly BossaRhythmCell RefOneThreeFour = Cell(514,
        H(0, 240), H(960, 720), H(1680, 960));
    private static readonly BossaRhythmCell RefThreeStab = Cell(515,
        H(240, 240), H(480, 240), H(1200, 480), H(1680, 960));

    // Four-bar sentences preserve the measured cells while keeping the existing
    // stage arc. Sparse stages draw from the long, two-gesture reference cells;
    // later stages may use the denser cells, but never invent a non-reference
    // start/duration combination.
    private static readonly BossaRhythmCell[][] OpeningSentences =
    [
        [RefTwoGesture, RefLongMid, RefLongAnticipation, RefAnticipationOnly],
        [RefLongAnticipation, RefTwoGesture, RefReleasePair, RefThreeAnd],
        [RefLongMid, RefAnticipationOnly, RefTwoGesture, RefLongAnticipation]
    ];

    private static readonly BossaRhythmCell[][] FirstSoloSentences =
    [
        // The first solo keeps one or two of the head's broad gestures before
        // the standard bossa cells become more prominent.
        [RefTwoGesture, RefPrimary, RefLongMid, RefReleasePair],
        [RefLongAnticipation, RefPrimary, RefMidAnswer, RefAnticipationOnly],
        [RefOneThreeFour, RefLongMid, RefPrimary, RefThreeAnd]
    ];

    private static readonly BossaRhythmCell[][] StandardSentences =
    [
        [RefPrimary, RefMidAnswer, RefCompactFive, RefFourStab],
        [RefPickupThree, RefPrimary, RefThreeAnd, RefReleasePair],
        [RefTwoLong, RefCompactFive, RefPrimary, RefMidWithFour]
    ];

    private static readonly BossaRhythmCell[][] LiftedSentences =
    [
        [RefCompactFive, RefPrimary, RefMidWithFour, RefFourStab],
        [RefPickupThree, RefCompactFive, RefPrimary, RefThreeStab],
        [RefPrimary, RefMidWithFour, RefFourStab, RefCompactFive]
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
            var hits = BuildHits(
                bar, sentence[barIndex % sentence.Count], arrangements[barIndex], seed, barIndex);
            cells[barIndex] = 4000 + (int)stage * 100 + sentenceIndex * 10 + barIndex % 4;
            var barStart = (long)barIndex * SessionConstants.BarTicks;

            for (var hitIndex = 0; hitIndex < hits.Count; hitIndex++)
            {
                var hit = hits[hitIndex];
                var offset = hit.Offset;
                var chord = ResolveChord(bar, nextBarChord, offset);
                chord = ChordFactory.ApplyMinorTargetTensions(
                    chord,
                    ChordFactory.GetFollowingChord(bar, offset, nextBarChord));
                if (chord.IsNoChord)
                {
                    continue;
                }
                var voiceCount = SelectVoiceCount(chord, stage, seed, barIndex, hitIndex);
                var voicing = VoiceLead(chord, voiceCount, lastVoicing, seed, barIndex, hitIndex);
                // Bossa comping uses compact, guitar-like voicings. A random added
                // top octave makes the upper line jump without a melodic reason, so
                // later-stage lift comes from rhythm and articulation instead.
                var renderedVoicing = voicing;
                var start = barStart + offset + 6 + (long)Math.Round(DeterministicNoise.Unit(seed, barIndex, hitIndex, 2801) * 4 - 2);
                if (start >= segmentLength) continue;

                var duration = hit.DurationTicks;
                duration = Math.Min(duration, segmentLength - start);
                if (hitIndex + 1 < hits.Count)
                {
                    var nextStart = barStart + hits[hitIndex + 1].Offset + 4;
                    duration = Math.Min(duration, Math.Max(1, nextStart - start));
                }
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
                var velocity = (byte)Math.Clamp(
                    (syncopated ? 51 : 48) + lift + phrase + (hitIndex == 0 ? 1 : 0) -
                    (arrangement.IsTransitionLeadIn ? 2 : 0),
                    40,
                    62);
                foreach (var noteNumber in renderedVoicing)
                {
                    notes.Add(new ScheduledNote(start, duration, noteNumber, velocity, SessionConstants.PianoChannel));
                }
                lastVoicing = voicing;
            }
        }

        return new PianoGenerationResult(notes, lastVoicing, cells[^1], cells);
    }

    private static (IReadOnlyList<BossaRhythmCell> Sentence, int Index) SelectSentence(
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

    private static IReadOnlyList<BossaRhythmHit> BuildHits(
        TuneBar bar,
        BossaRhythmCell baseCell,
        BarArrangement arrangement,
        int seed,
        int barIndex)
    {
        var hits = baseCell.Hits.ToList();
        foreach (var change in bar.ChordChanges.Skip(1))
        {
            var changeTick = (long)change.StartBeat * SessionConstants.Ppq;
            if (!hits.Any(hit => hit.Offset >= changeTick && hit.Offset - changeTick <= SessionConstants.Ppq / 2))
            {
                hits.Add(new BossaRhythmHit(changeTick, ReferenceFallbackDuration(changeTick)));
            }
        }

        if (arrangement.Function == PhraseFunction.Space && hits.Count > 3)
        {
            var removable = hits.Where(hit => hit.Offset != 0 && hit.Offset != 1680).ToArray();
            if (removable.Length > 0)
            {
                var remove = removable[(int)(DeterministicNoise.Unit(seed, barIndex, 2805) * removable.Length) % removable.Length];
                hits.Remove(remove);
            }
        }

        if (arrangement.IsTransitionLeadIn && hits.Count > 2)
        {
            // The final two bars are a release. Keep a written 4& anticipation
            // when it already exists, but do not manufacture another pickup at
            // the exact point where the head is about to return.
            var removable = hits
                .Where(hit => hit.Offset != 0 && hit.Offset != 1680)
                .OrderBy(hit => DeterministicNoise.Unit(seed, barIndex, (int)hit.Offset, 2806))
                .FirstOrDefault();
            if (removable.Offset != 0)
            {
                hits.Remove(removable);
            }
        }

        return hits
            .GroupBy(hit => hit.Offset)
            .Select(group => group.First())
            .OrderBy(hit => hit.Offset)
            .Take(5)
            .ToArray();
    }

    private static long ReferenceFallbackDuration(long offset)
        => offset >= 1680 ? 960 : offset % SessionConstants.Ppq == 0 ? 480 : 240;

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

    private static BossaRhythmCell Cell(int index, params BossaRhythmHit[] hits)
        => new(index, hits);

    private static BossaRhythmHit H(long offset, long durationTicks)
        => new(offset, durationTicks);

    private sealed record BossaRhythmCell(int Index, IReadOnlyList<BossaRhythmHit> Hits);

    private readonly record struct BossaRhythmHit(long Offset, long DurationTicks);
}
