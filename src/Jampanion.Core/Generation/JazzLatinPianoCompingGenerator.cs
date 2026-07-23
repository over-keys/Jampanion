using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

/// <summary>
/// Straight-eighth jazz-Latin piano comping.
///
/// This deliberately avoids a continuous salsa montuno.  It uses spacious,
/// voice-led jazz voicings, varied note lengths, and anticipations centred on
/// 2&, 3&, and 4&, as measured in the supplied reference MIDI.
/// </summary>
internal static class JazzLatinPianoCompingGenerator
{
    private static readonly JazzLatinPianoCell BroadOne =
        Cell(1, H(0, 1680));
    private static readonly JazzLatinPianoCell OneAndLong =
        Cell(2, H(240, 1440));
    private static readonly JazzLatinPianoCell TwoAndLong =
        Cell(3, H(720, 960));
    private static readonly JazzLatinPianoCell FourAndPickup =
        Cell(4, H(1680, 840));
    private static readonly JazzLatinPianoCell AnchorAndAnswer =
        Cell(5, H(0, 240), H(720, 600));
    private static readonly JazzLatinPianoCell TwoAndThreeAnd =
        Cell(6, H(720, 120), H(1200, 480));
    private static readonly JazzLatinPianoCell OneTwoAndThreeAnd =
        Cell(7, H(0, 360), H(720, 120), H(1200, 240));
    private static readonly JazzLatinPianoCell LateAnswer =
        Cell(8, H(1200, 480), H(1680, 720));
    private static readonly JazzLatinPianoCell OneAndFourAnd =
        Cell(9, H(240, 720), H(1680, 840));
    private static readonly JazzLatinPianoCell CompactSyncopation =
        Cell(10, H(0, 240), H(720, 120), H(1200, 360), H(1680, 360));
    private static readonly JazzLatinPianoCell TwoAndRelease =
        Cell(11, H(720, 120), H(1200, 120));
    private static readonly JazzLatinPianoCell SplitAnswer =
        Cell(12, H(0, 240), H(480, 240, shell: true), H(720, 480), H(1200, 480, shell: true));

    private static readonly JazzLatinPianoCell[][] OpeningSentences =
    [
        [AnchorAndAnswer, TwoAndThreeAnd, BroadOne, FourAndPickup],
        [TwoAndLong, OneAndLong, AnchorAndAnswer, LateAnswer],
        [BroadOne, FourAndPickup, TwoAndLong, OneAndFourAnd]
    ];

    private static readonly JazzLatinPianoCell[][] PonchandoSentences =
    [
        [AnchorAndAnswer, TwoAndThreeAnd, OneAndLong, LateAnswer],
        [OneTwoAndThreeAnd, TwoAndLong, OneAndFourAnd, TwoAndRelease],
        [TwoAndThreeAnd, AnchorAndAnswer, FourAndPickup, OneTwoAndThreeAnd]
    ];

    private static readonly JazzLatinPianoCell[][] MontunoSentences =
    [
        // "Montuno" remains the historical stage name in the arrangement arc,
        // but the texture is jazz comping rather than a repeating guajeo.
        [OneTwoAndThreeAnd, TwoAndThreeAnd, AnchorAndAnswer, LateAnswer],
        [CompactSyncopation, OneAndLong, TwoAndRelease, OneAndFourAnd],
        [SplitAnswer, TwoAndLong, OneTwoAndThreeAnd, FourAndPickup]
    ];

    private static readonly JazzLatinPianoCell[][] MamboSentences =
    [
        [CompactSyncopation, SplitAnswer, OneTwoAndThreeAnd, LateAnswer],
        [SplitAnswer, CompactSyncopation, TwoAndThreeAnd, OneAndFourAnd],
        [OneTwoAndThreeAnd, LateAnswer, CompactSyncopation, SplitAnswer]
    ];

    public static PianoGenerationResult Generate(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<byte>? previousVoicing,
        int previousCellIndex,
        int seed,
        LatinChorusStage stage,
        PerformanceGuidance? performanceGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);
        if (bars.Count != arrangements.Count)
        {
            throw new ArgumentException("Bars and arrangements must have the same length.");
        }
        if (bars.Count == 0)
        {
            throw new ArgumentException("At least one bar is required.", nameof(bars));
        }

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var notes = new List<ScheduledNote>(bars.Count * 16);
        var cells = new int[bars.Count];
        IReadOnlyList<byte> lastVoicing =
            previousVoicing ?? Array.Empty<byte>();
        var (sentence, sentenceIndex) =
            SelectSentence(stage, seed, previousCellIndex);

        var events = new List<JazzLatinPianoEvent>(bars.Count * 4);
        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var nextBarChord = barIndex + 1 < bars.Count
                ? bars[barIndex + 1].GetChordAtBeat(0)
                : followingChord;
            var cell = sentence[barIndex % sentence.Count];
            var hits = BuildHits(
                bar,
                cell,
                arrangements[barIndex],
                stage,
                seed,
                barIndex);
            cells[barIndex] =
                8600 + (int)stage * 100 + sentenceIndex * 10 +
                barIndex % sentence.Count;
            var barStart =
                (long)barIndex * SessionConstants.BarTicks;

            foreach (var hit in hits)
            {
                var chord = ResolveChord(
                    bar,
                    nextBarChord,
                    hit.Offset);
                chord = ChordFactory.ApplyMinorTargetTensions(
                    chord,
                    ChordFactory.GetFollowingChord(
                        bar,
                        hit.Offset,
                        nextBarChord));
                if (chord.IsNoChord)
                {
                    continue;
                }

                events.Add(new JazzLatinPianoEvent(
                    barIndex,
                    barStart + hit.Offset,
                    hit,
                    chord));
            }
        }

        events.Sort((left, right) => left.Tick.CompareTo(right.Tick));
        for (var eventIndex = 0; eventIndex < events.Count; eventIndex++)
        {
            var item = events[eventIndex];
            var hitIndexInBar = events
                .Take(eventIndex)
                .Count(prior => prior.BarIndex == item.BarIndex);
            var voiceCount = SelectVoiceCount(
                item.Chord,
                stage,
                item.Hit.Shell,
                seed,
                item.BarIndex,
                hitIndexInBar);
            var voicing = VoiceLead(
                item.Chord,
                voiceCount,
                lastVoicing,
                seed,
                item.BarIndex,
                hitIndexInBar);
            var rendered = item.Hit.Shell
                ? ReduceToShell(voicing)
                : voicing;

            // Piano sits a hair behind the bass/kick.  The spread is kept below
            // 6 ticks, so the rhythm remains crisp at fast Latin-jazz tempos.
            var delay = 2 + (long)Math.Round(
                DeterministicNoise.Unit(
                    seed,
                    item.BarIndex,
                    hitIndexInBar,
                    8701) * 4);
            var start = item.Tick + delay;
            if (start >= segmentLength)
            {
                continue;
            }

            var duration = item.Hit.DurationTicks;
            if (eventIndex + 1 < events.Count)
            {
                var nextGridStart = events[eventIndex + 1].Tick + 2;
                duration = Math.Min(
                    duration,
                    Math.Max(1, nextGridStart - start));
            }
            duration = Math.Min(duration, segmentLength - start);

            var arrangement = arrangements[item.BarIndex];
            var stageLift = stage switch
            {
                LatinChorusStage.Opening or LatinChorusStage.HeadOut => -3,
                LatinChorusStage.Ponchando => -1,
                LatinChorusStage.Mambo => 3,
                _ => 1
            };
            var phraseLift = arrangement.Function switch
            {
                PhraseFunction.Build => 3,
                PhraseFunction.Space => -4,
                PhraseFunction.Release => -1,
                _ => 0
            };
            var localOffset =
                item.Tick % SessionConstants.BarTicks;
            var syncopationLift =
                localOffset % SessionConstants.Ppq == 0 ? -1 : 2;
            var velocity = 59 + stageLift + phraseLift +
                syncopationLift +
                arrangement.DynamicLift / 3 +
                (guidance.HighStage ? 2 : 0) -
                (arrangement.IsTransitionLeadIn ? 2 : 0);
            var renderedVelocity = (byte)Math.Clamp(
                velocity,
                45,
                79);

            foreach (var noteNumber in rendered)
            {
                notes.Add(new ScheduledNote(
                    start,
                    duration,
                    noteNumber,
                    rendered.Count <= 3 &&
                    noteNumber == rendered[^1]
                        ? (byte)Math.Max(
                            42,
                            renderedVelocity - 3)
                        : renderedVelocity,
                    SessionConstants.PianoChannel));
            }

            lastVoicing = voicing;
        }

        return new PianoGenerationResult(
            notes,
            lastVoicing,
            cells[^1],
            cells);
    }

    private static IReadOnlyList<JazzLatinPianoHit> BuildHits(
        TuneBar bar,
        JazzLatinPianoCell cell,
        BarArrangement arrangement,
        LatinChorusStage stage,
        int seed,
        int barIndex)
    {
        var hits = cell.Hits.ToList();
        var structuralOffsets = new HashSet<long>();

        // Chord changes on beat 3 are normally anticipated on 2&.  This is the
        // central harmonic behaviour in the supplied jazz-Latin reference.
        foreach (var change in bar.ChordChanges.Skip(1))
        {
            var changeTick =
                (long)change.StartBeat * SessionConstants.Ppq;
            var anticipation =
                Math.Max(0, changeTick - SessionConstants.Ppq / 2);
            structuralOffsets.Add(anticipation);
            if (!hits.Any(hit =>
                    Math.Abs(hit.Offset - anticipation) <= 1))
            {
                hits.Add(H(
                    anticipation,
                    changeTick == SessionConstants.Ppq * 2
                        ? 600
                        : 360));
            }
        }

        if (arrangement.Function == PhraseFunction.Build &&
            (stage is LatinChorusStage.Montuno or
                LatinChorusStage.Mambo) &&
            !hits.Any(hit => hit.Offset == 1680) &&
            DeterministicNoise.Unit(seed, barIndex, 8703) < 0.56)
        {
            hits.Add(H(1680, 600));
        }

        if (arrangement.Function == PhraseFunction.Space &&
            hits.Count > 1)
        {
            var removable = hits
                .Where(hit =>
                    !structuralOffsets.Contains(hit.Offset))
                .OrderBy(hit =>
                    DeterministicNoise.Unit(
                        seed,
                        barIndex,
                        (int)hit.Offset,
                        8705))
                .ToArray();
            if (removable.Length > 0)
            {
                hits.Remove(removable[0]);
            }
        }

        if (arrangement.IsTransitionLeadIn &&
            hits.Count > 2)
        {
            var keep = hits
                .OrderByDescending(hit => hit.Offset == 1680)
                .ThenByDescending(hit =>
                    structuralOffsets.Contains(hit.Offset))
                .ThenBy(hit => hit.Offset)
                .Take(2)
                .ToHashSet();
            hits = hits
                .Where(keep.Contains)
                .ToList();
        }

        var maximum = stage switch
        {
            LatinChorusStage.Opening or LatinChorusStage.HeadOut => 3,
            LatinChorusStage.Ponchando => 4,
            LatinChorusStage.Montuno => 4,
            LatinChorusStage.Mambo => 5,
            _ => 4
        };

        return hits
            .GroupBy(hit => hit.Offset)
            .Select(group => group
                .OrderByDescending(hit =>
                    structuralOffsets.Contains(hit.Offset))
                .ThenByDescending(hit => hit.DurationTicks)
                .First())
            .OrderBy(hit => hit.Offset)
            .Take(maximum)
            .ToArray();
    }

    private static ChordSpec ResolveChord(
        TuneBar bar,
        ChordSpec nextBarChord,
        long offset)
    {
        if (offset >=
            SessionConstants.BarTicks -
            SessionConstants.Ppq / 2)
        {
            return nextBarChord;
        }

        var anticipated = bar.ChordChanges
            .FirstOrDefault(change =>
                change.StartBeat * SessionConstants.Ppq >
                    offset &&
                change.StartBeat * SessionConstants.Ppq -
                    offset <=
                    SessionConstants.Ppq / 2);
        return anticipated?.Chord ??
            bar.GetChordAtTick(
                Math.Min(offset, bar.BarTicks - 1));
    }

    private static int SelectVoiceCount(
        ChordSpec chord,
        LatinChorusStage stage,
        bool shell,
        int seed,
        int barIndex,
        int hitIndex)
    {
        if (shell)
        {
            return Math.Min(4, chord.PianoPitchClasses.Count);
        }

        var highStage =
            stage is LatinChorusStage.Montuno or
                LatinChorusStage.Mambo;
        var chooseFive =
            highStage &&
            DeterministicNoise.Unit(
                seed,
                barIndex,
                hitIndex,
                8711) < 0.58;
        return Math.Clamp(
            chooseFive ? 5 : 4,
            3,
            Math.Max(3, chord.PianoPitchClasses.Count));
    }

    private static IReadOnlyList<byte> VoiceLead(
        ChordSpec chord,
        int requestedVoiceCount,
        IReadOnlyList<byte> previous,
        int seed,
        int barIndex,
        int hitIndex)
    {
        var pitchClasses = chord.PianoPitchClasses
            .Distinct()
            .ToArray();
        if (pitchClasses.Length < 3)
        {
            pitchClasses = chord.BassPitchClasses
                .Distinct()
                .ToArray();
        }
        var voiceCount = Math.Clamp(
            requestedVoiceCount,
            Math.Min(3, pitchClasses.Length),
            pitchClasses.Length);

        return PianoVoicingVocabulary.Choose(
            pitchClasses,
            previous,
            voiceCount,
            lower: 48,
            upper: 82,
            targetCenter: 64.0,
            PianoVoicingStyle.Latin,
            chord.RootPitchClass,
            seed,
            barIndex,
            hitIndex);
    }

    private static IReadOnlyList<byte> ReduceToShell(
        IReadOnlyList<byte> voicing)
    {
        if (voicing.Count <= 3)
        {
            return voicing;
        }

        return new[]
        {
            voicing[0],
            voicing[^2],
            voicing[^1]
        }
        .Distinct()
        .Order()
        .ToArray();
    }

    private static (
        IReadOnlyList<JazzLatinPianoCell> Sentence,
        int Index)
        SelectSentence(
            LatinChorusStage stage,
            int seed,
            int previousCellIndex)
    {
        var source = stage switch
        {
            LatinChorusStage.Opening or
                LatinChorusStage.HeadOut =>
                OpeningSentences,
            LatinChorusStage.Ponchando =>
                PonchandoSentences,
            LatinChorusStage.Mambo =>
                MamboSentences,
            _ => MontunoSentences
        };
        var index = (int)(
            DeterministicNoise.Unit(
                seed,
                (int)stage,
                8717) * source.Length) %
            source.Length;
        var stageBase = 8600 + (int)stage * 100;
        var previousSentence =
            previousCellIndex >= stageBase &&
            previousCellIndex < stageBase + 100
                ? (previousCellIndex - stageBase) / 10
                : -1;
        if (source.Length > 1 &&
            index == previousSentence)
        {
            index = (index + 1) % source.Length;
        }

        return (source[index], index);
    }

    private static JazzLatinPianoHit H(
        long offset,
        long duration,
        bool shell = false) =>
        new(offset, duration, shell);

    private static JazzLatinPianoCell Cell(
        int id,
        params JazzLatinPianoHit[] hits) =>
        new(id, hits);

    private readonly record struct JazzLatinPianoHit(
        long Offset,
        long DurationTicks,
        bool Shell);

    private sealed record JazzLatinPianoCell(
        int Id,
        IReadOnlyList<JazzLatinPianoHit> Hits);

    private readonly record struct JazzLatinPianoEvent(
        int BarIndex,
        long Tick,
        JazzLatinPianoHit Hit,
        ChordSpec Chord);
}
