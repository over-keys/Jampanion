using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal sealed record PianoGenerationResult(
    IReadOnlyList<ScheduledNote> Notes,
    IReadOnlyList<byte> LastVoicing,
    int LastCellIndex,
    IReadOnlyList<int> CellIndices);

internal static class PianoCompingGenerator
{
    private const int TargetChordAtHit = -1;
    private const int TargetNextBar = -2;

    private static readonly RhythmCell Rest = new(-10, []);

    // TWO-BEAT LANGUAGE
    // Bass and drums establish the broad half-note pulse.  The piano therefore does
    // not need to restate beats 1 and 3 with pads.  Its characteristic vocabulary is
    // a spacious mixture of Charleston / reverse-Charleston shapes, single upbeat
    // comments, broad offbeat pairs and restrained harmonic anticipations.  Paired
    // gestures use an asymmetric short-long articulation so that two attacks read as
    // one horn-like phrase rather than as a reduced four-beat comping stream.
    private static readonly RhythmCell TBeatTwo = C(1, H(480, 520, 45));
    private static readonly RhythmCell TAndOne = C(2, H(320, 500, 46));
    private static readonly RhythmCell TAndTwo = C(3, H(800, 560, 47));
    private static readonly RhythmCell TAndThree = C(4, H(1280, 500, 48));
    private static readonly RhythmCell TBeatFour = C(5, H(1440, 330, 47));
    private static readonly RhythmCell TCharleston = C(6, H(0, 220, 46), H(800, 560, 49));
    private static readonly RhythmCell TReverse = C(7, H(320, 220, 46), H(960, 520, 49));
    private static readonly RhythmCell TOffbeatPair = C(8, H(320, 220, 47), H(1280, 500, 50));
    private static readonly RhythmCell TBeatTwoAndThree = C(9, H(480, 220, 46), H(1280, 500, 49));
    private static readonly RhythmCell TAndTwoFour = C(10, H(800, 220, 48), H(1760, 700, 50, TargetNextBar));
    private static readonly RhythmCell TAnswer = C(11, H(800, 520, 49));
    private static readonly RhythmCell TAnticipation = C(12, H(1760, 720, 49, TargetNextBar));
    private static readonly RhythmCell TLong = C(13, H(0, 920, 45));
    private static readonly RhythmCell TChordFloor = C(14, H(0, 900, 42));
    private static readonly RhythmCell TQuick = C(15, H(800, 230, 51));
    private static readonly RhythmCell TQuickPair = C(16, H(320, 170, 50), H(1280, 240, 53));
    private static readonly RhythmCell TQuickAnticipation = C(17, H(1760, 230, 53, TargetNextBar));
    // In two-feel the bass owns the large pulse, but the piano should still carry
    // the harmony through selected spaces. These held offbeat answers preserve
    // the sparse attack count while avoiding an empty-sounding bar.
    private static readonly RhythmCell TLongAnswer = C(18, H(800, 760, 49));
    private static readonly RhythmCell TLongAnticipation = C(19, H(1760, 840, 49, TargetNextBar));

    // FOUR-BEAT LANGUAGE
    // Walking bass and ride provide the continuous reference.  Piano attacks can be
    // shorter and more syncopated, but are organized into four-bar sentences with
    // repetition, variation and release rather than selected independently by bar.
    private static readonly RhythmCell FCharleston = C(101, H(0, 210, 50), H(800, 360, 54));
    private static readonly RhythmCell FReverse = C(102, H(320, 190, 49), H(960, 520, 54));
    private static readonly RhythmCell FAndTwoAndFour = C(103, H(800, 190, 52), H(1760, 360, 55, TargetNextBar));
    private static readonly RhythmCell FBeatTwo = C(104, H(480, 520, 51));
    private static readonly RhythmCell FBeatThree = C(105, H(960, 520, 53));
    private static readonly RhythmCell FLatePair = C(106, H(1280, 180, 53), H(1440, 280, 55));
    private static readonly RhythmCell FTwoFour = C(107, H(480, 190, 51), H(1440, 260, 55));
    private static readonly RhythmCell FOffOneThree = C(108, H(320, 190, 49), H(1280, 340, 55));
    private static readonly RhythmCell FAnticipation = C(110, H(1760, 440, 55, TargetNextBar));
    private static readonly RhythmCell FMiddle = C(111, H(800, 480, 52));
    private static readonly RhythmCell FEarlyAnswer = C(112, H(320, 210, 52), H(800, 330, 54));
    // Swing.mid uses a held statement as the harmonic floor even in four-feel.
    // Keep the normal vocabulary nimble, but give calm statements enough length
    // to carry the chord instead of leaving an accidental hole.
    private static readonly RhythmCell FReferenceLong = C(121, H(0, 1180, 49));
    private static readonly RhythmCell FReferenceAnticipation = C(122, H(1760, 720, 55, TargetNextBar));
    private static readonly RhythmCell FLongOpen = C(113, H(0, 1180, 49));
    private static readonly RhythmCell FChordFloor = C(114, H(0, 820, 44));
    private static readonly RhythmCell FQuickOneThree = C(115, H(320, 170, 51), H(1280, 220, 55));
    private static readonly RhythmCell FQuickTwoFour = C(116, H(800, 170, 53), H(1760, 220, 55, TargetNextBar));
    private static readonly RhythmCell FQuickTwo = C(117, H(480, 250, 52));
    private static readonly RhythmCell FQuickThree = C(118, H(960, 250, 54));
    private static readonly RhythmCell FQuickReverse = C(119, H(320, 170, 51), H(960, 230, 54));
    private static readonly RhythmCell FQuickCharleston = C(120, H(0, 170, 52), H(800, 230, 55));

    // Four-bar sentences.  The rhythmic identity is larger than a one-bar cell:
    // statement -> space/variation -> reply/development -> setup/release.  The
    // final upbeat is frequently an explicit statement of the next harmony, not
    // merely a short rhythmic flourish.
    private static readonly PianoSentence[] TwoBeatSentences =
    [
        S(1, 1.00, TCharleston, TLongAnswer, TOffbeatPair, TLongAnticipation),
        S(2, 0.96, TAndOne, TBeatFour, TAndTwo, TLongAnticipation),
        S(3, 0.94, TReverse, TLongAnswer, TAndTwoFour, TBeatFour),
        S(4, 0.92, TBeatTwoAndThree, TAndOne, TLongAnswer, TLongAnticipation),
        S(5, 0.88, TAndTwo, TLongAnswer, TAnswer, TAnticipation),
        S(6, 0.86, TBeatTwo, TOffbeatPair, TBeatFour, TLongAnticipation),
        S(7, 0.82, TBeatTwo, TLongAnswer, TAndTwo, TAndTwoFour),
        S(8, 0.78, TAndOne, TBeatFour, TAndThree, Rest),
        S(9, 0.72, TCharleston, TLongAnswer, TBeatTwo, TLongAnticipation),
        S(10, 0.58, Rest, TBeatTwo, TOffbeatPair, Rest)
    ];

    // The head should establish harmony before the conversation becomes busy.  These
    // sentences use a held shell as the default statement and reserve short
    // punctuation for an occasional answer or anticipation.
    private static readonly PianoSentence[] OpeningTwoBeatSentences =
    [
        S(31, 1.00, TLong, TLongAnticipation, TLong, TAndTwo),
        S(32, 0.94, TLong, TBeatTwo, TLong, TAnticipation),
        S(33, 0.88, TLong, TLongAnticipation, TAndTwo, TLong),
        S(34, 0.82, TBeatTwo, TLong, TAndTwo, TLongAnticipation),
        S(35, 0.74, TLong, TAndTwo, TLong, TLong)
    ];

    private static readonly PianoSentence[] HighTwoBeatSentences =
    [
        S(21, 1.00, TQuickPair, TQuick, TQuickPair, TQuickAnticipation),
        S(22, 0.96, TQuick, TQuickAnticipation, TQuickPair, Rest),
        S(23, 0.94, TQuickPair, TQuick, TQuickPair, TQuickAnticipation),
        S(24, 0.90, TQuick, TQuickPair, Rest, TQuickAnticipation),
        S(25, 0.84, TAnswer, Rest, TQuickPair, TQuick)
    ];

    private static readonly PianoSentence[] FourBeatSentences =
    [
        S(101, 1.00, FReferenceLong, Rest, FAndTwoAndFour, FReferenceAnticipation),
        S(102, 0.96, FBeatTwo, FMiddle, Rest, FReverse),
        S(103, 0.94, FReferenceLong, Rest, FBeatThree, FReferenceAnticipation),
        S(104, 0.90, FOffOneThree, Rest, FLatePair, Rest),
        S(105, 0.88, Rest, FReverse, FReferenceLong, FReferenceAnticipation),
        S(106, 0.84, FBeatTwo, Rest, FAndTwoAndFour, FBeatThree),
        S(107, 0.80, FReferenceLong, Rest, FReverse, FReferenceAnticipation),
        S(108, 0.76, Rest, FCharleston, Rest, FAnticipation),
        S(109, 0.74, FMiddle, Rest, FOffOneThree, FAnticipation),
        S(110, 0.68, FReverse, FBeatThree, Rest, FAnticipation)
    ];

    private static readonly PianoSentence[] OpeningFourBeatSentences =
    [
        S(131, 1.00, FLongOpen, FLongOpen, FLongOpen, FReferenceAnticipation),
        S(132, 0.94, FLongOpen, FBeatTwo, FMiddle, FLongOpen),
        S(133, 0.88, FLongOpen, Rest, FMiddle, FLongOpen),
        S(134, 0.82, FBeatTwo, FLongOpen, Rest, FReferenceAnticipation),
        S(135, 0.74, FLongOpen, FMiddle, FLongOpen, FAnticipation)
    ];

    private static readonly PianoSentence[] HighFourBeatSentences =
    [
        S(121, 1.00, FQuickOneThree, FQuickThree, FQuickTwoFour, FQuickTwoFour),
        S(122, 0.96, FQuickReverse, FQuickTwoFour, FQuickTwo, FQuickTwoFour),
        S(123, 0.94, FQuickOneThree, FQuickTwo, FQuickTwoFour, FQuickThree),
        S(124, 0.90, FQuickTwo, FQuickTwoFour, FQuickReverse, FQuickTwoFour),
        S(125, 0.86, FQuickTwo, FQuickCharleston, FQuickTwoFour, FQuickTwoFour),
        S(126, 0.82, FEarlyAnswer, Rest, FQuickOneThree, FQuickTwoFour)
    ];

    private static readonly PianoSentence[] TwoBeatEndingSentences =
    [
        S(201, 1.00, TAndTwo, Rest, TBeatTwo, Rest),
        S(202, 0.90, Rest, TAndOne, Rest, TAnticipation),
        S(203, 0.82, TCharleston, Rest, Rest, Rest),
        S(204, 0.75, Rest, TAndTwo, TBeatFour, Rest)
    ];

    private static readonly PianoSentence[] FourBeatEndingSentences =
    [
        S(301, 1.00, FBeatTwo, Rest, FCharleston, Rest),
        S(302, 0.92, Rest, FReverse, FMiddle, FAnticipation),
        S(303, 0.84, FCharleston, Rest, FBeatThree, Rest),
        S(304, 0.78, FMiddle, Rest, FAnticipation, Rest)
    ];

    public static PianoGenerationResult Generate(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        RhythmFeel feel,
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<byte>? previousVoicing,
        int previousCellIndex,
        int seed,
        PerformanceGuidance? performanceGuidance = null,
        bool restrainedOpening = false)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);

        if (bars.Count != arrangements.Count)
        {
            throw new ArgumentException("Bars and arrangements must have the same length.");
        }

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var notes = new List<ScheduledNote>(bars.Count * 10);
        var cells = PlanCells(feel, arrangements, previousCellIndex, seed, guidance, restrainedOpening);
        IReadOnlyList<byte> lastVoicing = previousVoicing ?? Array.Empty<byte>();
        string? lastChordSymbol = null;
        var phraseDelay = SwingTiming.PianoDelay(seed, guidance.HighStage);
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        long occupiedUntil = -1;

        for (var bar = 0; bar < bars.Count; bar++)
        {
            var cell = cells[bar];
            var hits = ExpandForChordChanges(cell.Hits, bars[bar], feel, arrangements[bar], seed, bar);
            var barStart = (long)bar * SessionConstants.BarTicks;
            var nextBarChord = bar + 1 < bars.Count
                ? bars[bar + 1].ChordChanges[0].Chord
                : followingChord;

            for (var hitIndex = 0; hitIndex < hits.Count; hitIndex++)
            {
                var hit = hits[hitIndex];
                var chord = ResolveChord(hit, bars[bar], nextBarChord);
                chord = ChordFactory.ApplyMinorTargetTensions(
                    chord,
                    ChordFactory.GetFollowingChord(bars[bar], hit.Offset, nextBarChord));
                var texture = SelectTexture(feel, arrangements[bar], hit.Length, guidance, seed, bar, hitIndex);
                var requestedVoiceCount = VoiceCount(texture);
                var pitchClasses = SelectPitchClasses(chord, requestedVoiceCount, texture, seed, bar, hitIndex);
                var voicingTexture = texture == VoicingTexture.Full && pitchClasses.Count < 4
                    ? VoicingTexture.Spread
                    : texture;
                var voiceCount = pitchClasses.Count;
                var targetCenter = SelectRegisterCenter(feel, guidance);
                var canReuse = lastChordSymbol == chord.Symbol &&
                    lastVoicing.Count == voiceCount &&
                    lastVoicing.All(note => chord.PianoPitchClasses.Contains(note % 12));
                var reuseProbability = feel == RhythmFeel.TwoBeat
                    ? 0.80
                    : guidance.HighStage ? 0.54 : 0.72;
                if (arrangements[bar].Function is PhraseFunction.Build or PhraseFunction.Answer)
                {
                    reuseProbability -= 0.14;
                }

                var reuse = canReuse && DeterministicNoise.Unit(seed, bar, hitIndex, 1800) < reuseProbability;
                var voicing = reuse
                    ? lastVoicing
                    : SelectVoicing(pitchClasses, lastVoicing, targetCenter, voicingTexture, seed, bar, hitIndex);
                voicing = StabilizeRegister(voicing, lastVoicing, voicingTexture);
                var perHitNudge = (long)Math.Round(DeterministicNoise.Unit(seed, bar, hitIndex, 1802) * 4 - 2);
                var start = barStart + hit.Offset + phraseDelay + perHitNudge;

                if (start >= segmentLength)
                {
                    continue;
                }

                // A held anticipation already states the following harmony.  Do not
                // mechanically re-strike it at the bar line while it is still ringing.
                if (start < occupiedUntil && hit.TargetBeat == TargetChordAtHit)
                {
                    continue;
                }

                var requestedDuration = ShapeArticulation(
                    LimitToCurrentHarmony(hit, bars[bar]),
                    hit,
                    bars[bar],
                    hitIndex + 1 < hits.Count ? hits[hitIndex + 1].Offset : SessionConstants.BarTicks,
                    feel,
                    arrangements[bar],
                    guidance,
                    restrainedOpening);
                var rolled = texture is VoicingTexture.Spread or VoicingTexture.Full &&
                    DeterministicNoise.Unit(seed, bar, hitIndex, 1801) < 0.02;
                for (var voice = 0; voice < voicing.Count; voice++)
                {
                    var voiceDelay = rolled ? voice * 2L : 0L;
                    var noteStart = Math.Min(start + voiceDelay, segmentLength - 1);
                    var duration = SwingTiming.ClampDuration(noteStart, requestedDuration, segmentLength);
                    var balance = voice == voicing.Count - 1 ? 4 : voice == 0 ? -2 : 0;
                    var variation = (int)Math.Round(DeterministicNoise.Unit(seed, bar, hitIndex, voicing[voice]) * 4 - 2);
                    var interactionAdjustment = (int)Math.Round(CompingDevelopment(guidance) * 8.0);
                    var feelAdjustment = feel == RhythmFeel.TwoBeat ? 2 : 4;
                    var presenceAdjustment = arrangements[bar].Responder switch
                    {
                        ResponderRole.Piano => 3,
                        ResponderRole.Drums => -1,
                        _ => 0
                    };
                    var phraseShape = arrangements[bar].DynamicLift;
                    var velocity = (byte)Math.Clamp(
                        hit.Velocity + balance + variation + interactionAdjustment + feelAdjustment + presenceAdjustment + phraseShape + (guidance.HighStage ? 1 : 0),
                        29,
                        guidance.HighStage ? 92 : 88);

                    notes.Add(new ScheduledNote(
                        noteStart,
                        duration,
                        voicing[voice],
                        velocity,
                        SessionConstants.PianoChannel));
                }

                lastVoicing = voicing;
                lastChordSymbol = chord.Symbol;
                occupiedUntil = Math.Max(occupiedUntil, start + requestedDuration);
            }
        }

        return new PianoGenerationResult(notes, lastVoicing, cells[^1].Index, cells.Select(cell => cell.Index).ToArray());
    }

    private static RhythmCell[] PlanCells(
        RhythmFeel feel,
        IReadOnlyList<BarArrangement> arrangements,
        int previousCellIndex,
        int seed,
        PerformanceGuidance guidance,
        bool restrainedOpening)
    {
        // A four-bar planning unit is not automatically a cadence.  Reserve ending
        // sentences for section and chorus boundaries so ordinary phrases can breathe,
        // answer and continue instead of producing the same late setup every four bars.
        var majorBoundary = arrangements[^1].Boundary is BoundaryStrength.Section or BoundaryStrength.Chorus;
        var ending = majorBoundary;
        var source = GetSentenceSource(feel, ending, guidance, restrainedOpening);
        var candidates = source
            .Where(sentence => previousCellIndex == Rest.Index || sentence.Bars[0].Index != previousCellIndex)
            .ToArray();
        if (candidates.Length == 0)
        {
            candidates = source.ToArray();
        }

        var sentence = SelectWeighted(candidates, DeterministicNoise.Unit(seed, 1819));
        var result = new RhythmCell[arrangements.Count];
        for (var bar = 0; bar < arrangements.Count; bar++)
        {
            var proposed = sentence.Bars[Math.Min(bar, sentence.Bars.Count - 1)];
            result[bar] = AdaptCellToRole(
                proposed,
                feel,
                arrangements[bar],
                seed,
                bar,
                guidance,
                preserveSwingChordFloor: false,
                restrainedOpening);
        }

        EnsureMinimumPresence(result, feel, arrangements, guidance, seed, restrainedOpening);

        return result;
    }

    private static IReadOnlyList<PianoSentence> GetSentenceSource(
        RhythmFeel feel,
        bool ending,
        PerformanceGuidance guidance,
        bool restrainedOpening)
    {
        if (ending)
        {
            return feel == RhythmFeel.TwoBeat ? TwoBeatEndingSentences : FourBeatEndingSentences;
        }

        var opening = feel == RhythmFeel.TwoBeat ? OpeningTwoBeatSentences : OpeningFourBeatSentences;
        var standard = feel == RhythmFeel.TwoBeat ? TwoBeatSentences : FourBeatSentences;
        var high = feel == RhythmFeel.TwoBeat ? HighTwoBeatSentences : HighFourBeatSentences;
        var development = CompingDevelopment(guidance);

        // Do not change the whole vocabulary at a stage boundary. The head starts
        // with held statements, the standard language enters gradually, and the
        // short high-stage cells become increasingly likely before the peak.
        var openingWeight = feel == RhythmFeel.FourBeat
            ? 1.0 - SmoothStep(development, 0.28, 0.68)
            : restrainedOpening
                ? 1.0 - SmoothStep(development, 0.18, 0.52)
                : 0.0;
        var highSignal = Math.Max(development, guidance.HighStage ? 0.62 : 0.0);
        var highWeight = SmoothStep(highSignal, 0.52, 0.88);
        if (feel == RhythmFeel.TwoBeat && !guidance.HighStage)
        {
            // Two-feel can develop through longer answers and anticipations, but
            // should not inherit the four-feel chatter before the walking section
            // is structurally established.
            highWeight *= 0.25;
        }
        if (restrainedOpening)
        {
            highWeight *= 0.25;
        }

        var standardWeight = Math.Clamp(1.0 - openingWeight - highWeight, 0.0, 1.0);
        return MixSentenceSources(
            (opening, openingWeight),
            (standard, standardWeight),
            (high, highWeight));
    }

    private static RhythmCell AdaptCellToRole(
        RhythmCell proposed,
        RhythmFeel feel,
        BarArrangement arrangement,
        int seed,
        int bar,
        PerformanceGuidance guidance,
        bool preserveSwingChordFloor,
        bool restrainedOpening)
    {
        if (arrangement.Function == PhraseFunction.Space)
        {
            return preserveSwingChordFloor
                ? feel == RhythmFeel.TwoBeat ? TChordFloor : FChordFloor
                : restrainedOpening && feel == RhythmFeel.TwoBeat ? TChordFloor
                : Rest;
        }

        if (arrangement.Function == PhraseFunction.Answer)
        {
            return feel == RhythmFeel.TwoBeat ? TAnswer : FEarlyAnswer;
        }

        if (arrangement.Responder == ResponderRole.Drums)
        {
            // Piano should not compete with a drum-led bar.  A single late setup or
            // a full rest is allowed, never a separate busy commentary.
            var probability = arrangement.Function == PhraseFunction.Setup
                ? feel == RhythmFeel.TwoBeat ? 0.46 : 0.60
                : feel == RhythmFeel.TwoBeat ? 0.32 : 0.48;
            if (arrangement.IsTransitionLeadIn)
            {
                probability += 0.16;
            }
            var speak = DeterministicNoise.Unit(seed, bar, 1841) < probability;
            if (!speak)
            {
                return Rest;
            }

            return feel == RhythmFeel.TwoBeat ? TAnticipation : FAnticipation;
        }

        if (arrangement.Responder == ResponderRole.Piano && proposed.Hits.Count == 0)
        {
            var selector = DeterministicNoise.Unit(seed, bar, 1843);
            if (feel == RhythmFeel.TwoBeat)
            {
                return selector < 0.28 ? TBeatTwo : selector < 0.68 ? TAndTwo : TBeatFour;
            }

            return selector < 0.34 ? FReverse : selector < 0.67 ? FMiddle : FBeatTwo;
        }

        if (arrangement.Responder == ResponderRole.Structural)
        {
            if (proposed.Hits.Count <= 1)
            {
                return proposed;
            }

            if (feel == RhythmFeel.FourBeat)
            {
                var calmRetention = 1.0 - SmoothStep(CompingDevelopment(guidance), 0.35, 0.75);
                if (DeterministicNoise.Unit(seed, bar, 1853) < calmRetention)
                {
                    var first = proposed.Hits.OrderBy(hit => hit.Offset).First();
                    var anticipation = proposed.Hits
                        .Where(hit => hit != first)
                        .OrderByDescending(hit => hit.TargetBeat == TargetNextBar)
                        .ThenByDescending(hit => hit.Offset)
                        .First();
                    return new RhythmCell(
                        proposed.Index + 6000,
                        new[] { first, anticipation }.OrderBy(hit => hit.Offset).ToArray());
                }
            }

            // Keep only the longer or later structural punctuation.
            var selected = proposed.Hits
                .OrderByDescending(hit => hit.Length)
                .ThenByDescending(hit => hit.Offset)
                .First();
            return new RhythmCell(proposed.Index + 5000, [selected]);
        }

        if (feel == RhythmFeel.TwoBeat && arrangement.Function == PhraseFunction.Build && proposed.Hits.Count == 0)
        {
            return DeterministicNoise.Unit(seed, bar, 1845) < 0.46 ? TCharleston : TOffbeatPair;
        }

        if (feel == RhythmFeel.FourBeat && arrangement.Function == PhraseFunction.Build && proposed.Hits.Count < 2)
        {
            return DeterministicNoise.Unit(seed, bar, 1847) < 0.5 ? FCharleston : FAndTwoAndFour;
        }

        // Low-energy input should not cause a stream of filler chords.  Preserve the
        // sentence but occasionally turn a non-foreground comment into real silence.
        if (guidance.Intensity == PerformanceIntensity.Low &&
            arrangement.Responder != ResponderRole.Piano &&
            DeterministicNoise.Unit(seed, bar, 1849) < 0.12)
        {
            return Rest;
        }

        return proposed;
    }

    private static void EnsureMinimumPresence(
        RhythmCell[] cells,
        RhythmFeel feel,
        IReadOnlyList<BarArrangement> arrangements,
        PerformanceGuidance guidance,
        int seed,
        bool restrainedOpening)
    {
        var target = feel == RhythmFeel.TwoBeat
            ? guidance.IsHighStageActive ? 8 : restrainedOpening ? 6 : 4
            : guidance.HighStage
                ? 11
                : 7 + (int)Math.Round(CompingDevelopment(guidance));
        var current = cells.Sum(cell => cell.Hits.Count);
        if (current >= target)
        {
            return;
        }

        var order = Enumerable.Range(0, cells.Length)
            .Where(index => arrangements[index].Function != PhraseFunction.Space)
            .OrderBy(index => arrangements[index].Responder == ResponderRole.Piano ? 0
                : arrangements[index].Responder == ResponderRole.Structural ? 1 : 2)
            .ThenBy(index => DeterministicNoise.Unit(seed, index, 1851))
            .ToArray();

        foreach (var index in order)
        {
            if (current >= target)
            {
                break;
            }

            var arrangement = arrangements[index];
            if (arrangement.Responder == ResponderRole.Drums &&
                arrangement.Function != PhraseFunction.Setup)
            {
                continue;
            }

            if (cells[index].Hits.Count == 0)
            {
                cells[index] = feel == RhythmFeel.TwoBeat
                    ? (index % 3) switch
                    {
                        0 => TBeatTwo,
                        1 => TAndTwo,
                        _ => TBeatFour
                    }
                    : (index % 2 == 0 ? FBeatTwo : FMiddle);
                current++;
                continue;
            }

            if (cells[index].Hits.Count == 1 && arrangement.Responder == ResponderRole.Piano)
            {
                if (feel == RhythmFeel.TwoBeat && guidance.IsHighStageActive)
                {
                    cells[index] = (index % 3) switch
                    {
                        0 => TCharleston,
                        1 => TOffbeatPair,
                        _ => TAndTwoFour
                    };
                    current++;
                }
                else if (feel == RhythmFeel.FourBeat)
                {
                    cells[index] = index % 2 == 0 ? FReverse : FAndTwoAndFour;
                    current++;
                }
            }
        }
    }

    private static PianoSentence SelectWeighted(IReadOnlyList<PianoSentence> sentences, double selector)
    {
        var total = sentences.Sum(sentence => sentence.Weight);
        var target = selector * total;
        var cumulative = 0.0;
        foreach (var sentence in sentences)
        {
            cumulative += sentence.Weight;
            if (target <= cumulative)
            {
                return sentence;
            }
        }

        return sentences[^1];
    }

    private static IReadOnlyList<PianoSentence> MixSentenceSources(
        params (IReadOnlyList<PianoSentence> Source, double Weight)[] sources)
    {
        var mixed = sources
            .Where(source => source.Weight > 0.0001)
            .SelectMany(source => source.Source.Select(sentence =>
                sentence with { Weight = sentence.Weight * source.Weight }))
            .ToArray();
        return mixed.Length > 0 ? mixed : sources[0].Source;
    }

    private static double CompingDevelopment(PerformanceGuidance guidance)
    {
        var density = Math.Max(guidance.Density, guidance.HighStage ? 0.62 : 0.0);
        return Math.Clamp((density - 0.20) / 0.70, 0.0, 1.0);
    }

    private static double SmoothStep(double value, double edge0, double edge1)
    {
        var t = Math.Clamp((value - edge0) / Math.Max(0.0001, edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static IReadOnlyList<RhythmHit> ExpandForChordChanges(
        IReadOnlyList<RhythmHit> original,
        TuneBar bar,
        RhythmFeel feel,
        BarArrangement arrangement,
        int seed,
        int barIndex)
    {
        if (bar.ChordChanges.Count == 1 || original.Count == 0)
        {
            return original;
        }

        // A drum-led bar may contain one restrained piano punctuation selected by
        // AdaptCellToRole.  Do not add a second chord-change hit here: that would
        // turn a deliberate yield into competing commentary.
        if (arrangement.Responder == ResponderRole.Drums)
        {
            return original;
        }

        var hits = original.ToList();
        foreach (var change in bar.ChordChanges.Skip(1))
        {
            var changeTick = (long)change.StartBeat * SessionConstants.Ppq;
            var anticipationTick = Math.Max(0, changeTick - 160L);

            // A two-hit two-feel cell is already a complete gesture.  Never add a
            // third stab.  If both attacks would otherwise remain on the old harmony,
            // reinterpret the nearby upbeat as an anticipation of the new chord.
            if (feel == RhythmFeel.TwoBeat && hits.Count >= 2)
            {
                var alreadyStatesChange = hits.Any(hit =>
                    hit.TargetBeat == change.StartBeat ||
                    (hit.TargetBeat == TargetChordAtHit && hit.Offset >= changeTick));
                if (!alreadyStatesChange)
                {
                    var anticipationIndex = hits.FindIndex(hit =>
                        hit.TargetBeat == TargetChordAtHit &&
                        Math.Abs(hit.Offset - anticipationTick) <= 80);
                    if (anticipationIndex >= 0)
                    {
                        hits[anticipationIndex] = hits[anticipationIndex] with { TargetBeat = change.StartBeat };
                    }
                }
                continue;
            }

            if (hits.Any(hit => Math.Abs(hit.Offset - anticipationTick) <= 80 ||
                                (hit.Offset >= changeTick && hit.Offset < changeTick + 180)))
            {
                continue;
            }

            var pianoPriority = arrangement.Responder == ResponderRole.Piano;
            var probability = feel == RhythmFeel.TwoBeat
                ? pianoPriority ? 0.68 : 0.28
                : pianoPriority ? 0.64 : 0.30;
            if (DeterministicNoise.Unit(seed, barIndex, change.StartBeat, 1823) >= probability)
            {
                continue;
            }

            var anticipateProbability = feel == RhythmFeel.TwoBeat ? 0.86 : 0.68;
            var anticipate = pianoPriority &&
                DeterministicNoise.Unit(seed, barIndex, change.StartBeat, 1824) < anticipateProbability;
            var offset = anticipate ? anticipationTick : changeTick + 12L;
            var length = feel == RhythmFeel.TwoBeat ? 620L : 250L;
            var velocity = feel == RhythmFeel.TwoBeat ? 48 : 53;
            hits.Add(new RhythmHit(offset, length, velocity, change.StartBeat));
        }

        return hits.OrderBy(hit => hit.Offset).ToArray();
    }

    private static long LimitToCurrentHarmony(RhythmHit hit, TuneBar bar)
    {
        if (hit.TargetBeat != TargetChordAtHit)
        {
            return hit.Length;
        }

        var nextChangeTick = bar.ChordChanges
            .Select(change => (long)change.StartBeat * SessionConstants.Ppq)
            .Where(tick => tick > hit.Offset)
            .DefaultIfEmpty(SessionConstants.BarTicks)
            .Min();
        var available = Math.Max(80L, nextChangeTick - hit.Offset - 20L);
        return Math.Min(hit.Length, available);
    }

    private static long ShapeArticulation(
        long duration,
        RhythmHit hit,
        TuneBar bar,
        long nextHitOffset,
        RhythmFeel feel,
        BarArrangement arrangement,
        PerformanceGuidance guidance,
        bool restrainedOpening)
    {
        // Do not clip a conventional anticipation: it is the one place where a
        // comping chord is meant to connect directly into the next bar.
        if (hit.TargetBeat == TargetNextBar)
        {
            return duration;
        }

        if (feel == RhythmFeel.TwoBeat)
        {
            var hasUpcomingHarmony = bar.ChordChanges.Any(change =>
                (long)change.StartBeat * SessionConstants.Ppq > hit.Offset);
            if (!hasUpcomingHarmony)
            {
                // Swing.mid and Ballad.mid both let a two-feel chord speak through
                // most of the gap to the next attack.  Use an intermediate coverage
                // so two-feel gains continuity without becoming a ballad pad.
                var available = Math.Max(120L, nextHitOffset - hit.Offset - 16L);
                var connectedHold = (long)Math.Round(available * 0.72);
                if (hit.Offset is 0 or (2L * SessionConstants.Ppq))
                {
                    connectedHold = Math.Min(connectedHold, 920L);
                }
                return Math.Max(duration, connectedHold);
            }

            return duration;
        }

        if (restrainedOpening || duration < 420)
        {
            return duration;
        }

        // Development comes primarily from rhythm, not a sudden voicing or register
        // swap. As density rises, sustained punctuations become slightly shorter so
        // they leave air for the walking bass and ride without turning into stabs.
        var densityAboveTheme = Math.Clamp((guidance.Density - 0.42) / 0.58, 0, 1);
        var compression = feel == RhythmFeel.FourBeat
            ? 0.12 * densityAboveTheme
            : 0.08 * densityAboveTheme;
        if (arrangement.Function is PhraseFunction.Build or PhraseFunction.Setup)
        {
            compression += 0.05;
        }
        if (arrangement.IsTransitionLeadIn)
        {
            compression += 0.05;
        }

        var minimum = feel == RhythmFeel.FourBeat ? 300L : 380L;
        return Math.Max(minimum, (long)Math.Round(duration * (1.0 - compression)));
    }

    private static ChordSpec ResolveChord(RhythmHit hit, TuneBar bar, ChordSpec nextBarChord)
    {
        return hit.TargetBeat switch
        {
            TargetNextBar => nextBarChord,
            >= 0 => bar.GetChordAtBeat(hit.TargetBeat),
            _ => bar.GetChordAtTick(Math.Min(hit.Offset, SessionConstants.BarTicks - 1))
        };
    }

    private static VoicingTexture SelectTexture(
        RhythmFeel feel,
        BarArrangement arrangement,
        long durationTicks,
        PerformanceGuidance guidance,
        int seed,
        int bar,
        int hitIndex)
    {
        var value = DeterministicNoise.Unit(seed, bar, hitIndex, 1829);
        var sustained = durationTicks >= 600;
        var pianoForeground = arrangement.Responder == ResponderRole.Piano;
        var drumForeground = arrangement.Responder == ResponderRole.Drums;

        if (drumForeground)
        {
            return value < 0.68 ? VoicingTexture.Shell : VoicingTexture.OpenShell;
        }

        if (feel == RhythmFeel.TwoBeat)
        {
            if (sustained)
            {
                return value < 0.46 ? VoicingTexture.Shell
                    : value < 0.90 ? VoicingTexture.OpenShell
                    : VoicingTexture.Spread;
            }

            return value < 0.28 ? VoicingTexture.Shell
                : value < 0.76 ? VoicingTexture.OpenShell
                : value < 0.96 ? VoicingTexture.Spread
                : VoicingTexture.Full;
        }

        if (arrangement.Function == PhraseFunction.Build && pianoForeground)
        {
            // Build with rhythmic authority rather than simply adding notes. Three-note
            // rootless voicings remain the center; four voices are a selective colour.
            return value < 0.16 ? VoicingTexture.OpenShell
                : value < 0.70 ? VoicingTexture.Spread
                : VoicingTexture.Full;
        }

        if (sustained)
        {
            return value < 0.20 ? VoicingTexture.Shell
                : value < 0.65 ? VoicingTexture.OpenShell
                : value < 0.92 ? VoicingTexture.Spread
                : VoicingTexture.Full;
        }

        if (guidance.HighStage && pianoForeground)
        {
            return value < 0.08 ? VoicingTexture.Shell
                : value < 0.36 ? VoicingTexture.OpenShell
                : value < 0.78 ? VoicingTexture.Spread
                : VoicingTexture.Full;
        }

        if (guidance.Intensity == PerformanceIntensity.High && pianoForeground)
        {
            return value < 0.12 ? VoicingTexture.Shell
                : value < 0.42 ? VoicingTexture.OpenShell
                : value < 0.76 ? VoicingTexture.Spread
                : VoicingTexture.Full;
        }

        return value < 0.16 ? VoicingTexture.Shell
            : value < 0.56 ? VoicingTexture.OpenShell
            : value < 0.92 ? VoicingTexture.Spread
            : VoicingTexture.Full;
    }

    private static int VoiceCount(VoicingTexture texture) => texture switch
    {
        VoicingTexture.Shell => 2,
        VoicingTexture.OpenShell => 3,
        VoicingTexture.Spread => 3,
        VoicingTexture.Full => 4,
        _ => 3
    };

    private static IReadOnlyList<int> SelectPitchClasses(
        ChordSpec chord,
        int voiceCount,
        VoicingTexture texture,
        int seed,
        int bar,
        int hitIndex)
    {
        var source = chord.PianoPitchClasses
            .Where(pitchClass => pitchClass != chord.RootPitchClass)
            .Distinct()
            .ToArray();
        if (source.Length == 0)
        {
            source = chord.PianoPitchClasses.Distinct().ToArray();
        }

        voiceCount = Math.Min(voiceCount, source.Length);
        var result = source.Take(Math.Min(2, voiceCount)).ToList();
        if (result.Count == voiceCount)
        {
            return result;
        }

        var colors = source.Skip(2).ToArray();
        if (colors.Length == 0)
        {
            colors = source;
        }

        // Keep the harmonic colour stable enough to sound intentional.  Spread/full
        // textures prefer the first available extension; alternate colours enter as
        // phrase variation, not as a different chord spelling on every attack.
        var offset = texture is VoicingTexture.Spread or VoicingTexture.Full
            ? 0
            : (int)(DeterministicNoise.Unit(seed, bar / 2, chord.RootPitchClass, 1831) * colors.Length) % colors.Length;
        for (var i = 0; result.Count < voiceCount && i < colors.Length * 2; i++)
        {
            var pitchClass = colors[(offset + i) % colors.Length];
            if (!result.Contains(pitchClass))
            {
                result.Add(pitchClass);
            }
        }

        return result;
    }

    private static IReadOnlyList<byte> SelectVoicing(
        IReadOnlyList<int> pitchClasses,
        IReadOnlyList<byte> previousVoicing,
        double targetCenter,
        VoicingTexture texture,
        int seed,
        int bar,
        int hitIndex)
    {
        var candidates = new List<byte[]>();
        foreach (var order in Permutations(pitchClasses))
        {
            var choices = order
                .Select(pc => Enumerable.Range(47, 30)
                    .Where(note => note % 12 == pc)
                    .Select(note => (byte)note)
                    .ToArray())
                .ToArray();
            Enumerate(order, choices, new byte[choices.Length], 0);
        }

        if (candidates.Count == 0)
        {
            return pitchClasses
                .Select(pc => (byte)Enumerable.Range(50, 28).First(note => note % 12 == pc))
                .Order()
                .ToArray();
        }

        var previous = previousVoicing.Order().ToArray();
        return candidates
            .OrderBy(candidate => Score(candidate, previous, targetCenter, texture)
                + DeterministicNoise.Unit(seed, bar, hitIndex, candidate.Sum(note => note), 1837) * 0.10)
            .First();

        void Enumerate(
            IReadOnlyList<int> order,
            IReadOnlyList<byte[]> choices,
            byte[] current,
            int voice)
        {
            if (voice == choices.Count)
            {
                var sorted = current.Order().ToArray();
                if (sorted.Distinct().Count() != sorted.Length || !MatchesTexture(sorted, texture))
                {
                    return;
                }

                candidates.Add(sorted);
                return;
            }

            foreach (var note in choices[voice])
            {
                current[voice] = note;
                Enumerate(order, choices, current, voice + 1);
            }
        }
    }

    private static IEnumerable<int[]> Permutations(IReadOnlyList<int> values)
    {
        var current = new int[values.Count];
        var used = new bool[values.Count];
        return Visit(0);

        IEnumerable<int[]> Visit(int depth)
        {
            if (depth == values.Count)
            {
                yield return current.ToArray();
                yield break;
            }

            for (var index = 0; index < values.Count; index++)
            {
                if (used[index]) continue;
                used[index] = true;
                current[depth] = values[index];
                foreach (var result in Visit(depth + 1))
                {
                    yield return result;
                }

                used[index] = false;
            }
        }
    }

    private static bool MatchesTexture(IReadOnlyList<byte> notes, VoicingTexture texture)
    {
        if (!HasPlayableSpacing(notes))
        {
            return false;
        }

        var span = notes[^1] - notes[0];
        return texture switch
        {
            VoicingTexture.Shell => notes.Count == 2 && span is >= 4 and <= 14,
            VoicingTexture.OpenShell => notes.Count == 3 && span is >= 9 and <= 20 && notes[1] - notes[0] >= 4,
            VoicingTexture.Spread => notes.Count == 3 && span is >= 13 and <= 24 && notes[1] - notes[0] >= 5,
            VoicingTexture.Full => notes.Count == 4 && span is >= 11 and <= 23 && notes[1] - notes[0] >= 3,
            _ => false
        };
    }

    private static bool HasPlayableSpacing(IReadOnlyList<byte> notes)
    {
        for (var i = 1; i < notes.Count; i++)
        {
            var interval = notes[i] - notes[i - 1];
            if (notes[i - 1] < 55 && interval < 3)
            {
                return false;
            }

            if (i >= 2 && notes[i] - notes[i - 2] < 5 && notes[i - 2] < 60)
            {
                return false;
            }
        }

        return notes[0] >= 47 && notes[^1] <= 76;
    }

    private static IReadOnlyList<byte> StabilizeRegister(
        IReadOnlyList<byte> candidate,
        IReadOnlyList<byte> previous,
        VoicingTexture texture)
    {
        if (candidate.Count == 0 || previous.Count == 0)
        {
            return candidate;
        }

        var options = new List<byte[]>();
        foreach (var octaveShift in new[] { -24, -12, 0, 12, 24 })
        {
            var shifted = candidate
                .Select(note => (int)note + octaveShift)
                .ToArray();
            if (shifted.Any(note => note is < 48 or > 76))
            {
                continue;
            }

            var normalized = shifted.Select(note => (byte)note).Order().ToArray();
            if (MatchesTexture(normalized, texture))
            {
                options.Add(normalized);
            }
        }

        return options
            .OrderBy(option => VoiceLeadingDistance(option, previous))
            .ThenBy(option => Math.Abs(option.Average(note => (double)note) - previous.Average(note => (double)note)))
            .FirstOrDefault() ?? candidate;
    }

    private static int VoiceLeadingDistance(
        IReadOnlyList<byte> current,
        IReadOnlyList<byte> previous)
    {
        var forward = current.Sum(note => previous.Min(prior => Math.Abs(note - prior)));
        var backward = previous.Sum(prior => current.Min(note => Math.Abs(note - prior)));
        return forward + backward;
    }

    private static double SelectRegisterCenter(RhythmFeel feel, PerformanceGuidance guidance)
    {
        var baseCenter = feel == RhythmFeel.TwoBeat ? 61.5 : 63.0;
        var stageLift = guidance.HighStage && feel == RhythmFeel.FourBeat ? 1.25 : 0.0;
        return baseCenter + stageLift;
    }

    private static double Score(
        IReadOnlyList<byte> candidate,
        IReadOnlyList<byte> previous,
        double targetCenter,
        VoicingTexture texture)
    {
        var center = candidate.Average(note => note);
        var span = candidate[^1] - candidate[0];
        var desiredSpan = texture switch
        {
            VoicingTexture.Shell => 8,
            VoicingTexture.OpenShell => 13,
            VoicingTexture.Spread => 17,
            VoicingTexture.Full => 15,
            _ => 13
        };
        var score = Math.Abs(center - targetCenter) * 0.25 + Math.Abs(span - desiredSpan) * 0.12;

        if (candidate[0] < 50)
        {
            score += (50 - candidate[0]) * 0.72;
        }

        if (candidate[^1] > 76)
        {
            score += (candidate[^1] - 76) * 0.75;
        }

        if (previous.Count > 0)
        {
            if (candidate.Count == previous.Count)
            {
                for (var i = 0; i < candidate.Count; i++)
                {
                    score += Math.Abs(candidate[i] - previous[i]) * 0.25;
                }
            }
            else
            {
                var forward = candidate.Sum(note => previous.Min(prior => Math.Abs(note - prior)));
                var backward = previous.Sum(prior => candidate.Min(note => Math.Abs(note - prior)));
                score += (forward + backward) * 0.16;
            }

            var topMotion = Math.Abs(candidate[^1] - previous[^1]);
            score += topMotion switch
            {
                0 => 0.18,
                <= 2 => -0.42,
                <= 5 => -0.16,
                <= 7 => 0.45,
                _ => 0.45 + (topMotion - 7) * 1.05
            };

            if (candidate.SequenceEqual(previous))
            {
                score += 0.40;
            }

            var commonPitchClasses = candidate.Select(note => note % 12)
                .Intersect(previous.Select(note => note % 12))
                .Count();
            score -= commonPitchClasses * 0.10;
        }

        return score;
    }

    private static PianoSentence S(int index, double weight, params RhythmCell[] bars)
        => new(index, bars, weight);

    private static RhythmCell C(int index, params RhythmHit[] hits) => new(index, hits);

    private static RhythmHit H(long offset, long length, int velocity, int targetBeat = TargetChordAtHit)
        => new(offset, length, velocity, targetBeat);

    private sealed record PianoSentence(int Index, IReadOnlyList<RhythmCell> Bars, double Weight);

    private sealed record RhythmCell(int Index, IReadOnlyList<RhythmHit> Hits);

    private enum VoicingTexture
    {
        Shell,
        OpenShell,
        Spread,
        Full
    }

    private readonly record struct RhythmHit(long Offset, long Length, int Velocity, int TargetBeat = TargetChordAtHit);
}
