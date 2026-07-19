using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal sealed record BassGenerationResult(
    IReadOnlyList<ScheduledNote> Notes,
    byte LastNote,
    IReadOnlyList<byte> RecentNotes,
    int LastDirection,
    int DirectionRun);

internal static class BassLineGenerator
{
    // Keep the two-feel's lower octave available.  Without the low F#/G
    // occurrences, a low G followed by a Gmaj7 colour tone could be forced
    // up to the next octave even though a one-step connection exists below.
    private const int MinimumNote = 29;
    private const int MaximumNote = 55;
    // One extra upper occurrence is kept as a boundary bridge.  Without it,
    // a segment ending on 50 could be forced eleven semitones down to the
    // next segment's low Eb/Gb root even though the adjacent upper root is a
    // one-step connection.
    private const int TwoFeelMaximumNote = 51;
    private const int HistoryLength = 8;
    private const double TwoFeelGateRatio = 0.92;
    // Swing 4& is the late triplet eighth, not the straight midpoint of the
    // beat. At PPQ 480 this is 320 ticks after beat 4.
    private const long EighthNoteTicks = SessionConstants.Ppq * 2 / 3;

    public static BassGenerationResult Generate(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        RhythmFeel feel,
        IReadOnlyList<BarArrangement> arrangements,
        byte? previousNote,
        IReadOnlyList<byte>? recentNotes,
        int previousDirection,
        int previousDirectionRun,
        int seed,
        PerformanceGuidance? performanceGuidance = null,
        bool prepareNextFourFeel = false,
        int initialTwoBeatTransitionRun = 0,
        TimeFeelProfile? timeFeel = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);
        if (bars.Count != arrangements.Count) throw new ArgumentException("Bars and arrangements must have the same length.");
        if (bars.Count == 0) throw new ArgumentException("At least one bar is required.", nameof(bars));

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var timing = timeFeel ?? TimeFeelProfile.Resolve(AccompanimentStyle.Swing, 140);
        var history = (recentNotes ?? Array.Empty<byte>()).TakeLast(HistoryLength).ToArray();
        var positions = BuildPositions(
            bars,
            followingChord,
            feel,
            arrangements,
            seed,
            prepareNextFourFeel,
            initialTwoBeatTransitionRun);
        var selected = FindBestLine(
            positions,
            previousNote,
            history,
            previousDirection,
            previousDirectionRun,
            seed,
            guidance);
        var generated = selected;
        var notes = new List<ScheduledNote>(generated.Length);
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;

        for (var i = 0; i < generated.Length; i++)
        {
            var position = positions[i];
            var start = SwingTiming.BassStart(position.GridTick, feel, guidance, position.Function, timing);
            var isPickup = position.IsOffbeat;
            var isBoundaryTail = feel == RhythmFeel.TwoBeat && i == positions.Length - 1;
            var followsPickup = feel == RhythmFeel.TwoBeat
                && i + 1 < positions.Length
                && positions[i + 1].IsOffbeat;
            var nextStart = i + 1 < positions.Length
                ? SwingTiming.BassStart(
                    positions[i + 1].GridTick,
                    feel,
                    guidance,
                    positions[i + 1].Function,
                    timing)
                : segmentLength;
            var baseDuration = position.RootOnlySplit
                ? Math.Min(
                    i + 1 < positions.Length ? positions[i + 1].GridTick : segmentLength,
                    (long)(position.BarIndex + 1) * SessionConstants.BarTicks) - start
                    - (isBoundaryTail || followsPickup ? 0 : 24)
                : feel == RhythmFeel.TwoBeat
                    ? isBoundaryTail || followsPickup
                        ? nextStart - start
                        : Math.Max(1L, (long)Math.Round((nextStart - start) * TwoFeelGateRatio))
                    : Math.Max(1L, nextStart - start);
            var duration = SwingTiming.ClampDuration(
                start,
                timing.ScaleGate(baseDuration, TimeFeelRole.Bass),
                segmentLength);
            // Drive comes primarily from placement and connected voice-leading, not
            // from accenting every harmony change. Keep the quarter-note pulse even.
            var velocityBase = feel == RhythmFeel.TwoBeat ? 76 : 72;
            // A swung offbeat is a connector, never another accented walking
            // pulse. Evaluate it first so no structural accent can leak into
            // an added 1&/2&/3&/4& note.
            var accent = position.IsChordOnset ? 4 : position.IsBarDownbeat ? 2 : 0;
            var phraseShape = i % 8 is 6 or 7 ? 1 : 0;
            var variation = (int)Math.Round(DeterministicNoise.Unit(seed, i, generated[i]) * 2 - 1);
            var interactionLift = guidance.HighStage ? 3 : 0;
            var arrangementLift = position.Function switch
            {
                PhraseFunction.Build => 3,
                PhraseFunction.Setup => 2,
                PhraseFunction.Space => -2,
                PhraseFunction.Release => -1,
                _ => 0
            };
            // The quarter-note framework must remain perceptually primary.
            // Added swung eighths are ghosted connectors; stage and phrase
            // energy must not lift them into the walking pulse's dynamic range.
            var velocity = isPickup
                ? (byte)Math.Clamp(51 + variation + (guidance.HighStage ? 1 : 0), 47, 55)
                : (byte)Math.Clamp(
                    velocityBase + accent + phraseShape + variation + interactionLift + arrangementLift,
                    50,
                    96);
            notes.Add(new ScheduledNote(start, duration, generated[i], velocity, SessionConstants.BassChannel));
        }

        var combined = history.Concat(generated).TakeLast(HistoryLength).ToArray();
        var lastDirection = generated.Length >= 2
            ? Math.Sign(generated[^1] - generated[^2])
            : previousDirection;
        var directionRun = CalculateDirectionRun(generated, previousNote, previousDirection, previousDirectionRun);
        return new BassGenerationResult(notes, generated[^1], combined, lastDirection, directionRun);
    }

    private static BassPosition[] BuildPositions(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        RhythmFeel feel,
        IReadOnlyList<BarArrangement> arrangements,
        int seed,
        bool prepareNextFourFeel,
        int initialTwoBeatTransitionRun)
    {
        var result = new List<BassPosition>(bars.Count * (feel == RhythmFeel.TwoBeat ? 3 : 4));
        var patternAssignments = BuildPatternAssignments(
            bars,
            followingChord,
            feel,
            seed,
            prepareNextFourFeel);
        var lastFourFeelDecorationBar = -2;
        for (var bar = 0; bar < bars.Count; bar++)
        {
            IEnumerable<int> beats;
            var rootOnlySplit = feel == RhythmFeel.TwoBeat
                && IsOnePlusThreeSplit(bars[bar]);
            if (feel == RhythmFeel.FourBeat)
            {
                beats = Enumerable.Range(0, SessionConstants.BeatsPerBar);
            }
            else if (rootOnlySplit)
            {
                beats = bars[bar].ChordChanges.Select(change => change.StartBeat);
            }
            else
            {
                var list = new List<int> { 0, 2 };
                list.AddRange(bars[bar].ChordChanges.Select(change => change.StartBeat));
                beats = list.Distinct().Order();
            }

            foreach (var beat in beats)
            {
                var chord = bars[bar].GetChordAtBeat(beat);
                patternAssignments.TryGetValue((bar, beat), out var patternStep);
                result.Add(new BassPosition(
                    (long)bar * SessionConstants.BarTicks + (long)beat * SessionConstants.Ppq,
                    bar,
                    beat,
                    chord,
                    bars[bar].ChordChanges.Any(change => change.StartBeat == beat),
                    beat == 0,
                    false,
                    arrangements[bar].Function,
                    chord,
                    false,
                    false,
                    feel,
                    patternStep?.PitchClass,
                    patternStep?.Direction ?? 0,
                    patternStep?.RegisterAnchor ?? 0,
                    rootOnlySplit,
                    false,
                    patternStep?.FoundationOctaveDirection ?? 0));
            }

            if (feel == RhythmFeel.TwoBeat
                && ShouldAddTwoFeelApproach(
                    bars,
                    followingChord,
                    arrangements[bar],
                    bar,
                    seed,
                    prepareNextFourFeel))
            {
                var chord = bars[bar].GetChordAtBeat(3);
                result.Add(new BassPosition(
                    (long)bar * SessionConstants.BarTicks + 3L * SessionConstants.Ppq + EighthNoteTicks,
                    bar,
                    3,
                    chord,
                    false,
                    false,
                    false,
                    arrangements[bar].Function,
                    chord,
                    false,
                    false,
                    feel,
                    null,
                    0,
                    0,
                    false,
                    true));
            }
            else if (feel == RhythmFeel.FourBeat &&
                bar - lastFourFeelDecorationBar >= 4)
            {
                var decoration = SelectFourFeelDecoration(
                    bars,
                    followingChord,
                    arrangements[bar],
                    bar,
                    seed);
                if (decoration is not null)
                {
                    var value = decoration.Value;
                    var chord = bars[bar].GetChordAtBeat(value.Beat);
                    result.Add(new BassPosition(
                        (long)bar * SessionConstants.BarTicks +
                            (long)value.Beat * SessionConstants.Ppq + EighthNoteTicks,
                        bar,
                        value.Beat,
                        chord,
                        false,
                        false,
                        false,
                        arrangements[bar].Function,
                        chord,
                        false,
                        false,
                        feel,
                        null,
                        value.FoundationOctaveDirection,
                        0,
                        false,
                        true,
                        value.FoundationOctaveDirection));
                    lastFourFeelDecorationBar = bar;
                }
            }
        }

        result.Sort((left, right) => left.GridTick.CompareTo(right.GridTick));

        // In a two-beat harmonic rhythm, a chromatic approach on every change
        // sounds like a chain of exercises rather than a walking line. Allow
        // at most alternating changes to use one; the other changes state the
        // current harmony with its foundation or fifth.
        var twoBeatTransitionRun = feel == RhythmFeel.FourBeat
            ? initialTwoBeatTransitionRun
            : 0;
        // Keep the phase stable across the four-bar generation segments. The
        // seed includes the segment index, so deriving this from the seed would
        // allow two adjacent segments to both choose the same side of the pair.
        const int twoBeatApproachPhase = 0;
        for (var i = 0; i < result.Count; i++)
        {
            var nextChord = i == result.Count - 1 ? followingChord : result[i + 1].Chord;
            var leadsToNewChord = nextChord != result[i].Chord;
            var isNewHarmony = i == 0 || result[i - 1].Chord != result[i].Chord;
            // In four-beat, the note immediately before any harmony change may
            // connect into the next chord. In two-beat, keep the large beat-3
            // support intact and use only the short 4& pickup as an approach.
            var isApproachPosition = feel == RhythmFeel.FourBeat
                ? leadsToNewChord
                : result[i].IsOffbeat;
            var isTwoBeatTransition = feel == RhythmFeel.FourBeat
                && result[i].BeatInBar is 1 or 3;
            if (feel == RhythmFeel.FourBeat)
            {
                if (result[i].BeatInBar is 0 or 2 && leadsToNewChord)
                {
                    // A change on an even beat means this is not a clean
                    // two-beat chain, so begin a fresh alternating run.
                    twoBeatTransitionRun = 0;
                }
                else if (isTwoBeatTransition)
                {
                    twoBeatTransitionRun = leadsToNewChord
                        ? twoBeatTransitionRun + 1
                        : 0;
                }
            }
            var allowTwoBeatApproach = !isTwoBeatTransition
                || (leadsToNewChord && (twoBeatTransitionRun + twoBeatApproachPhase) % 2 == 1);
            result[i] = result[i] with
            {
                IsNewHarmony = isNewHarmony,
                NextChord = nextChord,
                LeadsToNewChord = leadsToNewChord,
                AllowChromaticApproach = leadsToNewChord
                    && isApproachPosition
                    && allowTwoBeatApproach
            };
        }
        return result.ToArray();
    }

    private static bool ShouldAddTwoFeelApproach(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        BarArrangement arrangement,
        int bar,
        int seed,
        bool prepareNextFourFeel)
    {
        var currentChord = bars[bar].GetChordAtBeat(3);
        var nextChord = bar + 1 < bars.Count
            ? bars[bar + 1].GetChordAtBeat(0)
            : followingChord;
        if (currentChord.IsNoChord || nextChord.IsNoChord)
        {
            return false;
        }

        // At the handoff into walking, the last two-feel bar keeps its normal
        // attacks and receives one late pickup even when the harmony repeats.
        // The pickup is the feel cue; it must not turn the whole bar into a
        // premature four-beat line.
        if (prepareNextFourFeel && bar == bars.Count - 1)
        {
            return true;
        }

        if (SameHarmony(currentChord, nextChord))
        {
            return false;
        }

        // Keep the broken-two decoration sparse. A formal chorus/section edge,
        // or the explicit handoff into the next four-feel chorus, may invite it;
        // ordinary bars receive it only during a genuine setup/build phrase.
        var probability = arrangement.Boundary switch
        {
            BoundaryStrength.Chorus => 0.28,
            BoundaryStrength.Section => 0.20,
            _ when arrangement.Function is PhraseFunction.Build or PhraseFunction.Setup => 0.12,
            _ => 0.035
        };
        return DeterministicNoise.Unit(seed, bar, 1641) < probability;
    }

    private static (int Beat, int FoundationOctaveDirection)? SelectFourFeelDecoration(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        BarArrangement arrangement,
        int bar,
        int seed)
    {
        var probability = arrangement.Function switch
        {
            PhraseFunction.Build => 0.10,
            PhraseFunction.Setup => 0.08,
            PhraseFunction.Space => 0.015,
            PhraseFunction.Release => 0.03,
            _ => 0.05
        };
        if (DeterministicNoise.Unit(seed, bar, 1751) >= probability)
        {
            return null;
        }

        var selector = DeterministicNoise.Unit(seed, bar, 1753);
        var beat = selector switch
        {
            < 0.12 => 0,
            < 0.47 => 1,
            < 0.88 => 2,
            _ => 3
        };
        var current = bars[bar].GetChordAtBeat(beat);
        var next = beat + 1 < SessionConstants.BeatsPerBar
            ? bars[bar].GetChordAtBeat(beat + 1)
            : bar + 1 < bars.Count
                ? bars[bar + 1].GetChordAtBeat(0)
                : followingChord;
        if (current.IsNoChord || next.IsNoChord)
        {
            return null;
        }

        // The reference occasionally answers a grounded beat-1 root with its
        // upper octave on the swung offbeat. Keep this as one explicit idiom,
        // never as a general escape from voice-leading constraints.
        var octaveDirection = beat == 0 &&
            !current.IsOnChord &&
            SameHarmony(current, next) &&
            DeterministicNoise.Unit(seed, bar, 1757) < 0.16
                ? 1
                : 0;
        return (beat, octaveDirection);
    }

    private static IReadOnlyDictionary<(int Bar, int Beat), BassPatternStep> BuildPatternAssignments(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        RhythmFeel feel,
        int seed,
        bool prepareNextFourFeel)
    {
        var result = new Dictionary<(int Bar, int Beat), BassPatternStep>();
        AddSlashPedalOctaveIdioms(result, bars, feel, seed);

        if (feel == RhythmFeel.TwoBeat)
        {
            AddTwoFeelRepeatedHarmonyIdioms(result, bars, seed);
            return result;
        }

        for (var bar = 0; bar < bars.Count; bar++)
        {
            var chord = bars[bar].GetChordAtBeat(0);
            var nextChord = bar == bars.Count - 1
                ? followingChord
                : bars[bar + 1].GetChordAtBeat(0);
            if (bars[bar].ChordChanges.Count != 1
                || !SameHarmony(chord, nextChord)
                || !IsTraditionalArpeggioEligible(chord))
            {
                continue;
            }

            var third = ChordalThirdPitchClass(chord);
            var fifth = ChordalFifthPitchClass(chord);
            var seventh = ChordalSeventhPitchClass(chord);
            if (third is null || fifth is null)
            {
                continue;
            }

            // A walking line should draw from a vocabulary rather than repeat one
            // lick. 1-2-3-5 remains a common stepwise cell, but chord arpeggios,
            // descending arpeggios and unconstrained voice-led walking share the
            // repeated-harmony opportunities.
            var selector = DeterministicNoise.Unit(seed, bar, 1739);
            if (selector < 0.18)
            {
                AddPattern(result, bar, [0, 1, 2, 3],
                    [FoundationPitchClass(chord), Mod12(chord.RootPitchClass + 2), third.Value, fifth.Value],
                    [0, 1, 1, 1]);
            }
            else if (selector < 0.28 && seventh is int seventhPitchClass)
            {
                AddPattern(result, bar, [0, 1, 2, 3],
                    [FoundationPitchClass(chord), third.Value, fifth.Value, seventhPitchClass],
                    [0, 1, 1, 1]);
            }
            else if (selector < 0.38 && seventh is int descendingSeventh)
            {
                AddPattern(result, bar, [0, 1, 2, 3],
                    [FoundationPitchClass(chord), descendingSeventh, fifth.Value, third.Value],
                    [0, -1, -1, -1]);
            }
            else if (selector < 0.45)
            {
                AddPattern(result, bar, [0, 1, 2, 3],
                    [FoundationPitchClass(chord), third.Value, fifth.Value, FoundationPitchClass(chord)],
                    [0, 1, 1, 1]);
            }
        }

        return result;
    }

    private static void AddSlashPedalOctaveIdioms(
        IDictionary<(int Bar, int Beat), BassPatternStep> assignments,
        IReadOnlyList<TuneBar> bars,
        RhythmFeel feel,
        int seed)
    {
        for (var bar = 0; bar < bars.Count; bar++)
        {
            var chord = bars[bar].Chord;
            if (bars[bar].ChordChanges.Count != 1 ||
                !chord.IsOnChord ||
                DeterministicNoise.Unit(seed, bar, 1761) >= 0.38)
            {
                continue;
            }

            var pedal = FoundationPitchClass(chord);
            if (feel == RhythmFeel.FourBeat)
            {
                assignments[(bar, 0)] = new BassPatternStep(pedal, 0);
                assignments[(bar, 1)] = new BassPatternStep(pedal, 1, 1, 1);
                assignments[(bar, 2)] = new BassPatternStep(pedal, -1, -1, -1);
            }
            else
            {
                assignments[(bar, 0)] = new BassPatternStep(pedal, 0, -1);
                assignments[(bar, 2)] = new BassPatternStep(pedal, 1, 1, 1);
            }
        }
    }

    private static void AddTwoFeelRepeatedHarmonyIdioms(
        IDictionary<(int Bar, int Beat), BassPatternStep> assignments,
        IReadOnlyList<TuneBar> bars,
        int seed)
    {
        for (var bar = 0; bar + 1 < bars.Count;)
        {
            var chord = bars[bar].GetChordAtBeat(0);
            if (bars[bar].ChordChanges.Count != 1 ||
                bars[bar + 1].ChordChanges.Count != 1 ||
                !SameHarmony(chord, bars[bar + 1].GetChordAtBeat(0)) ||
                !IsTraditionalArpeggioEligible(chord))
            {
                bar++;
                continue;
            }

            var third = ChordalThirdPitchClass(chord);
            var fifth = ChordalFifthPitchClass(chord);
            if (third is null || fifth is null)
            {
                bar++;
                continue;
            }

            // Two notes per bar, expressed as one two-bar sentence:
            // | 1 3 | 5 8 | or | 8 5 | 3 1 |.
            var ascending = DeterministicNoise.Unit(seed, bar, 1741) < 0.5;
            AddPattern(
                assignments,
                bar,
                [0, 2, 4, 6],
                ascending
                    ? [FoundationPitchClass(chord), third.Value, fifth.Value, FoundationPitchClass(chord)]
                    : [FoundationPitchClass(chord), fifth.Value, third.Value, FoundationPitchClass(chord)],
                ascending ? [0, 1, 1, 1] : [0, -1, -1, -1],
                ascending ? [-1, 0, 0, 0] : [1, 0, 0, 0]);
            bar += 2;
        }
    }

    private static void AddPattern(
        IDictionary<(int Bar, int Beat), BassPatternStep> assignments,
        int startingBar,
        IReadOnlyList<int> beatOffsets,
        IReadOnlyList<int> pitchClasses,
        IReadOnlyList<int> directions,
        IReadOnlyList<int>? registerAnchors = null)
    {
        for (var i = 0; i < beatOffsets.Count; i++)
        {
            var offset = beatOffsets[i];
            assignments[(startingBar + offset / SessionConstants.BeatsPerBar, offset % SessionConstants.BeatsPerBar)] =
                new BassPatternStep(pitchClasses[i], directions[i], registerAnchors?[i] ?? 0);
        }
    }

    private static byte[] FindBestLine(
        IReadOnlyList<BassPosition> positions,
        byte? previousNote,
        IReadOnlyList<byte> history,
        int previousDirection,
        int previousDirectionRun,
        int seed,
        PerformanceGuidance guidance)
    {
        var layers = new Dictionary<StateKey, PathState>[positions.Count];
        var openingFoundation = previousNote is null
            ? BassHarmonicMotion.ChooseOpeningFoundation(
                positions[0].Chord,
                MinimumNote,
                positions[0].Feel == RhythmFeel.TwoBeat ? TwoFeelMaximumNote : MaximumNote)
            : (byte?)null;
        var initialReference = previousNote ?? openingFoundation!.Value;
        for (var positionIndex = 0; positionIndex < positions.Count; positionIndex++)
        {
            var candidates = BuildCandidates(positions[positionIndex], seed, positionIndex);
            var layer = new Dictionary<StateKey, PathState>();
            if (positionIndex == 0)
            {
                var maximumLeap = BassHarmonicMotion.PreferredMaximumLeap;
                var hasSafeTransition = candidates.Any(candidate =>
                    Math.Abs(candidate.Note - initialReference) <= maximumLeap);
                foreach (var candidate in candidates)
                {
                    if (openingFoundation is byte opening && candidate.Note != opening)
                    {
                        continue;
                    }

                    var interval = candidate.Note - initialReference;
                    var intervalMagnitude = Math.Abs(interval);
                    var foundationOctave = IsFoundationOctaveIdiom(
                        positions[positionIndex],
                        initialReference,
                        candidate.Note);
                    // A hard interval/run rejection is musically preferred,
                    // but one penalized rescue state keeps a four-bar segment
                    // from becoming unplayable at a boundary.
                    if (intervalMagnitude > BassHarmonicMotion.AbsoluteMaximumLeap && !foundationOctave) continue;
                    var emergencyLeap = !foundationOctave &&
                        !hasSafeTransition &&
                        intervalMagnitude > maximumLeap;
                    if (intervalMagnitude > maximumLeap && !emergencyLeap && !foundationOctave) continue;
                    var direction = Math.Sign(interval);
                    var run = direction != 0 && direction == previousDirection ? previousDirectionRun + 1 : direction == 0 ? 0 : 1;
                    var emergencyRun = run > 8 && layer.Count == 0;
                    if (run > 8 && !emergencyRun) continue;
                    var boundedRun = Math.Min(8, run);
                    var score = candidate.BaseCost + TransitionCost(initialReference, candidate.Note)
                        + (positions[positionIndex].Feel == RhythmFeel.TwoBeat
                            ? 0.0
                            : HarmonicArrivalCost(
                                positions[positionIndex],
                                null,
                                initialReference,
                                candidate.Note,
                                previousWasApproach: false))
                        + DirectionRunCost(boundedRun) + HistoryCost(candidate.Note, interval, history, 0)
                        + FoundationOctaveIdiomCost(positions[positionIndex], interval, foundationOctave)
                        + (emergencyLeap ? EmergencyLeapCost(intervalMagnitude, maximumLeap) : 0)
                        + (emergencyRun ? 18.0 : 0)
                        + ContourCost(candidate.Note, positionIndex, positions.Count, seed, positions[positionIndex], guidance);
                    AddOrReplace(layer, new StateKey(candidate.Note, direction, boundedRun, interval), new PathState(score, null));
                }
            }
            else
            {
                var maximumLeap = BassHarmonicMotion.PreferredMaximumLeap;
                var hasSafeTransition = layers[positionIndex - 1].Keys.Any(prior =>
                    candidates.Any(candidate => Math.Abs(candidate.Note - prior.Note) <= maximumLeap));
                foreach (var prior in layers[positionIndex - 1])
                foreach (var candidate in candidates)
                {
                    var interval = candidate.Note - prior.Key.Note;
                    // Two-feel is a grounded half-note line. An octave displacement
                    // between adjacent attacks sounds like a register accident, not
                    // a melodic choice; keep it within a fifth. Four-feel retains
                    // the wider walking-line allowance.
                    var intervalMagnitude = Math.Abs(interval);
                    var foundationOctave = IsFoundationOctaveIdiom(
                        positions[positionIndex],
                        prior.Key.Note,
                        candidate.Note);
                    // Preserve the normal two-feel/four-feel guard whenever a
                    // valid transition exists; retain only a heavily
                    // penalized rescue state if every transition is rejected.
                    if (intervalMagnitude > BassHarmonicMotion.AbsoluteMaximumLeap && !foundationOctave) continue;
                    var emergencyLeap = !foundationOctave &&
                        !hasSafeTransition &&
                        intervalMagnitude > maximumLeap;
                    if (intervalMagnitude > maximumLeap && !emergencyLeap && !foundationOctave) continue;
                    var direction = Math.Sign(interval);
                    var run = direction != 0 && direction == prior.Key.Direction ? prior.Key.DirectionRun + 1 : direction == 0 ? 0 : 1;
                    var emergencyRun = run > 8 && layer.Count == 0;
                    if (run > 8 && !emergencyRun) continue;
                    var boundedRun = Math.Min(8, run);
                    var score = prior.Value.Score + candidate.BaseCost + TransitionCost(prior.Key.Note, candidate.Note)
                        + (positions[positionIndex].Feel == RhythmFeel.TwoBeat
                            ? 0.0
                            : HarmonicArrivalCost(
                                positions[positionIndex],
                                positions[positionIndex - 1].Chord,
                                prior.Key.Note,
                                candidate.Note,
                                positions[positionIndex - 1].AllowChromaticApproach ||
                                    positions[positionIndex - 1].IsOffbeat))
                        + PatternMotionCost(positions[positionIndex], interval)
                        + DirectionCost(prior.Key.Direction, direction, positionIndex)
                        + DirectionRunCost(boundedRun) + RepeatedIntervalCost(prior.Key.LastInterval, interval)
                        + HistoryCost(candidate.Note, interval, history, positionIndex)
                        + FoundationOctaveIdiomCost(positions[positionIndex], interval, foundationOctave)
                        + (emergencyLeap ? EmergencyLeapCost(intervalMagnitude, maximumLeap) : 0)
                        + (emergencyRun ? 18.0 : 0)
                        + ContourCost(candidate.Note, positionIndex, positions.Count, seed, positions[positionIndex], guidance);
                    AddOrReplace(layer, new StateKey(candidate.Note, direction, boundedRun, interval), new PathState(score, prior.Key));
                }
            }
            // Keep the dynamic-programming search bounded. The retained states
            // cover different notes, directions and interval histories without
            // allowing a combinatorial explosion during large validation runs.
            layers[positionIndex] = layer.Count <= 24
                ? layer
                : layer.OrderBy(pair => pair.Value.Score)
                    .Take(24)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        var final = layers[^1].OrderBy(pair => pair.Value.Score).First();
        var result = new byte[positions.Count];
        StateKey? key = final.Key;
        for (var i = positions.Count - 1; i >= 0; i--)
        {
            if (key is null) throw new InvalidOperationException("Failed to reconstruct the bass line.");
            result[i] = key.Value.Note;
            key = layers[i][key.Value].Previous;
        }
        return result;
    }

    private static IReadOnlyList<BassCandidate> BuildCandidates(BassPosition position, int seed, int positionIndex)
    {
        var chord = position.Chord;
        var result = new Dictionary<byte, double>();
        var pcs = BasicBassPitchClasses(chord);
        var foundation = FoundationPitchClass(chord);
        var slashBass = chord.IsOnChord;

        if (position.IsOffbeat)
        {
            if (position.FoundationOctaveDirection != 0)
            {
                foreach (var note in GetNotesForPitchClasses([foundation]))
                {
                    AddCandidate(result, note, -4.80);
                }
                return Finish(result, seed, positionIndex, 1696, position.Feel);
            }

            // A two-feel 4& is a short connector into the following structural
            // beat. Keep it independent of the current chord's voicing so a
            // slash chord cannot turn the pickup into a long fifth/root repeat.
            var targets = position.LeadsToNewChord
                ? new[] { FoundationPitchClass(position.NextChord) }
                : BasicBassPitchClasses(position.NextChord).Take(3).ToArray();
            for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
            {
                var target = targets[targetIndex];
                foreach (var pc in new[]
                         {
                             Mod12(target - 1), Mod12(target + 1),
                             Mod12(target - 2), Mod12(target + 2)
                         })
                {
                    var distance = CircularPitchClassDistance(pc, target);
                    var cost = position.LeadsToNewChord
                        ? BassHarmonicMotion.FunctionalApproachCost(
                            pc,
                            chord,
                            position.NextChord)
                        : distance == 1 ? -1.55 : -0.62;
                    cost += targetIndex * 0.18;
                    foreach (var note in GetNotesForPitchClasses([pc]))
                    {
                        AddCandidate(result, note, cost);
                    }
                }

                // A direct target is a safe fallback when the constrained
                // acoustic register makes both sides awkward.
                foreach (var note in GetNotesForPitchClasses([target]))
                {
                    AddCandidate(result, note, 0.40 + targetIndex * 0.18);
                }
            }

            return Finish(result, seed, positionIndex, 1697, position.Feel);
        }

        if (position.IsBarDownbeat)
        {
            // Across swing two-feel and four-feel, beat 1 is a stable harmonic
            // statement. The foundation is the norm; fifth and third remain
            // available only as voice-leading alternatives. Repeated-harmony
            // patterns influence the contour without overriding that hierarchy.
            var downbeatPitchClasses = position.RootOnlySplit
                ? new[] { foundation }
                : DownbeatBassPitchClasses(chord);
            for (var index = 0; index < downbeatPitchClasses.Count; index++)
            {
                var pitchClass = downbeatPitchClasses[index];
                var cost = index switch
                {
                    0 => -5.80,
                    1 => -1.55,
                    _ => -1.05
                };
                if (position.PatternPitchClass is int downbeatPatternPitchClass &&
                    Mod12(downbeatPatternPitchClass) == Mod12(pitchClass))
                {
                    cost -= 1.15;
                }
                foreach (var note in GetNotesForPitchClasses([pitchClass]))
                {
                    AddCandidate(result, note, cost);
                }
            }

            return Finish(result, seed, positionIndex, 1697, position.Feel);
        }

        if (chord.IsOnChord)
        {
            if (position.FoundationOctaveDirection != 0)
            {
                foreach (var note in GetNotesForPitchClasses([foundation]))
                {
                    AddCandidate(result, note, -4.80);
                }
                return Finish(result, seed, positionIndex, 1698, position.Feel);
            }

            foreach (var note in GetNotesForPitchClasses(chord.OnChordBassPitchClasses))
            {
                var cost = PitchClass(note) == foundation ? -6.0 : -1.10;
                AddCandidate(result, note, cost);
            }

            return Finish(result, seed, positionIndex, 1698, position.Feel);
        }

        if (position.RootOnlySplit)
        {
            foreach (var note in GetNotesForPitchClasses([foundation]))
            {
                AddCandidate(result, note, -6.0);
            }

            return Finish(result, seed, positionIndex, 1699, position.Feel);
        }

        if (position.PatternPitchClass is int patternPitchClass)
        {
            // Pattern steps specify harmonic vocabulary, not a fixed register.
            // The global search still selects the octave and joins each cell to
            // the surrounding line; PatternMotionCost preserves its intended
            // ascending or descending contour.
            var patternNotes = GetNotesForPitchClasses(
                [patternPitchClass],
                position.Feel == RhythmFeel.TwoBeat ? TwoFeelMaximumNote : MaximumNote);
            foreach (var note in patternNotes)
            {
                var isPreferredRegister = position.PatternRegisterAnchor > 0
                    ? note == patternNotes.Max()
                    : position.PatternRegisterAnchor < 0
                        ? note == patternNotes.Min()
                        : true;
                // Idiom register anchors are a soft preference. Keeping the
                // other octave available lets the path search join adjacent
                // four-bar segments without an avoidable leap or empty state
                // layer.
                AddCandidate(result, note, isPreferredRegister ? -4.20 : -3.55);
            }

            foreach (var note in GetNotesForPitchClasses(pcs))
            {
                var alternativeCost = PitchClass(note) == patternPitchClass ? -4.20 : 0.95;
                AddCandidate(result, note, alternativeCost);
            }

            return Finish(result, seed, positionIndex, 1700, position.Feel);
        }

        if (position.IsNewHarmony)
        {
            if (position.Feel == RhythmFeel.TwoBeat)
            {
                // The first beat that states a new harmony is the foundation of
                // the two-feel. This also protects mid-bar chord changes from
                // being hidden behind a smooth but ambiguous voice-leading tone.
                foreach (var note in GetNotesForPitchClasses([foundation]))
                {
                    AddCandidate(result, note, -6.00);
                }

                return Finish(result, seed, positionIndex, 1702, position.Feel);
            }

            var selector = DeterministicNoise.Unit(seed, position.BarIndex, positionIndex, 1701);
            for (var toneIndex = 0; toneIndex < Math.Min(3, pcs.Length); toneIndex++)
            {
                var pc = pcs[toneIndex];
                var isFoundation = pc == foundation;
                var cost = position.Feel == RhythmFeel.TwoBeat
                    ? isFoundation ? -4.00 : toneIndex == 2 ? 0.30 : 1.35
                    : isFoundation ? -2.60 : toneIndex == 1 ? 0.20 : 0.00;
                if (slashBass && !isFoundation) cost += position.Feel == RhythmFeel.TwoBeat ? 20 : 24;
                if (!slashBass && position.Feel == RhythmFeel.TwoBeat)
                {
                    // A new harmony in two-feel should be stated plainly. The root
                    // is the default; the fifth remains available for a genuinely
                    // smoother connection, while the third is deliberately secondary.
                    if (selector > 0.94 && toneIndex == 2) cost -= 0.70;
                }
                if (!slashBass && position.Feel == RhythmFeel.FourBeat && selector > 0.92 && toneIndex is 1 or 2) cost -= 0.85;
                foreach (var note in GetNotesForPitchClasses([pc])) AddCandidate(result, note, cost);
            }
            return Finish(result, seed, positionIndex, 1702, position.Feel);
        }

        var twoFeelPitchClasses = position.Feel == RhythmFeel.TwoBeat
            ? TwoFeelBeat3PitchClasses(chord)
            : pcs;
        var twoFeelFifth = position.Feel == RhythmFeel.TwoBeat
            ? TwoFeelFifthPitchClass(chord)
            : null;
        var fourFeelFifth = position.Feel == RhythmFeel.FourBeat
            ? ChordalFifthPitchClass(chord)
            : null;
        var twoFeelConnectionTone = SelectTwoFeelConnectionTone(position, seed, positionIndex, twoFeelFifth);
        foreach (var note in GetNotesForPitchClasses(twoFeelPitchClasses))
        {
            var pitchClass = PitchClass(note);
            var index = Array.IndexOf(twoFeelPitchClasses, pitchClass);
            var cost = position.Feel == RhythmFeel.TwoBeat
                ? pitchClass == twoFeelFifth ? -1.45
                    : pitchClass == foundation ? -0.05
                    : pitchClass == twoFeelConnectionTone ? -1.05
                    : index == 1 ? 0.20
                    : index == 2 ? 0.45
                    : 0.85
                : index switch { 0 => 0.08, 1 => 0.02, 2 => -0.16, 3 => 0.42, _ => 1.20 };
            if (fourFeelFifth is int fifth && pitchClass == fifth)
            {
                cost -= 0.16;
            }
            if (position.BeatInBar == 2)
            {
                cost -= position.Feel == RhythmFeel.TwoBeat
                    ? pitchClass == twoFeelConnectionTone ? 1.90
                        : pitchClass == twoFeelFifth ? twoFeelConnectionTone is null ? 1.35 : 0.20
                        : pitchClass == foundation ? 0.10
                        : 0.0
                    : index is 1 or 2 ? 0.18 : 0.02;
            }
            AddCandidate(result, note, cost);
        }

        if (position.Feel == RhythmFeel.FourBeat
            && position.LeadsToNewChord
            && !position.AllowChromaticApproach)
        {
            // The alternate slot in a two-beat chain should sound like a
            // statement of the present harmony, not another chromatic pickup.
            // Giving the foundation and fifth an explicit bonus also lets the
            // path search retain the same sounding pitch when it is the smooth
            // choice from the preceding beat.
            var fifth = ChordalFifthPitchClass(chord);
            foreach (var pitchClass in new[] { foundation, fifth }.Where(value => value is not null).Select(value => value!.Value).Distinct())
            {
                var cost = pitchClass == foundation ? -1.05 : -0.78;
                foreach (var note in GetNotesForPitchClasses([pitchClass]))
                {
                    AddCandidate(result, note, cost);
                }
            }
        }

        if (position.AllowChromaticApproach && position.Feel == RhythmFeel.FourBeat)
        {
            // A chromatic note is justified by its resolution into the next
            // foundation, not by mirroring every extension in the chord
            // symbol. Functional circle motion and dominant resolution receive
            // the clearest semitone approach.
            var useApproach = DeterministicNoise.Unit(seed, position.BarIndex, position.BeatInBar, 1705) < 0.42;
            foreach (var pc in BassHarmonicMotion.ConnectionPitchClasses(
                         chord,
                         position.NextChord))
            {
                var functionalCost = BassHarmonicMotion.FunctionalApproachCost(
                    pc,
                    chord,
                    position.NextChord);
                var target = FoundationPitchClass(position.NextChord);
                var distance = CircularPitchClassDistance(pc, target);
                var cost = useApproach
                    ? functionalCost
                    : distance <= 2
                        ? functionalCost + 2.65
                        : functionalCost + 1.40;
                foreach (var note in GetNotesForPitchClasses([pc]))
                {
                    AddCandidate(result, note, cost);
                }
            }
        }
        return Finish(result, seed, positionIndex, 1706, position.Feel);
    }

    private static IReadOnlyList<BassCandidate> Finish(
        Dictionary<byte, double> result,
        int seed,
        int index,
        int salt,
        RhythmFeel feel) =>
        result
            .Where(pair => feel != RhythmFeel.TwoBeat || pair.Key <= TwoFeelMaximumNote)
            .Select(pair => new BassCandidate(pair.Key, pair.Value + DeterministicNoise.Unit(seed, index, pair.Key, salt) * 0.12))
            .OrderBy(candidate => candidate.Note).ToArray();

    private static double TransitionCost(byte previous, byte current)
    {
        var interval = Math.Abs(current - previous);
        return interval switch
        {
            0 => 1.15, <= 2 => 0.08 * interval, <= 5 => 0.12 * interval,
            <= 7 => 0.65 + (interval - 5) * 0.25,
            <= 12 => 1.5 + (interval - 7) * 0.95,
            _ => 35 + interval
        };
    }

    private static double EmergencyLeapCost(int interval, int preferredMaximum) =>
        18.0 + (interval - preferredMaximum) * 4.0;

    private static bool IsFoundationOctaveIdiom(
        BassPosition position,
        byte previous,
        byte current) =>
        position.FoundationOctaveDirection != 0 &&
        current - previous == position.FoundationOctaveDirection * 12 &&
        PitchClass(previous) == FoundationPitchClass(position.Chord) &&
        PitchClass(current) == FoundationPitchClass(position.Chord) &&
        !position.LeadsToNewChord;

    private static double FoundationOctaveIdiomCost(
        BassPosition position,
        int interval,
        bool isFoundationOctave)
    {
        if (position.FoundationOctaveDirection == 0)
        {
            return 0;
        }

        if (isFoundationOctave)
        {
            return -8.40;
        }

        return interval == 0 ? 4.80 : 1.60;
    }

    private static double PatternMotionCost(BassPosition position, int interval)
    {
        if (position.PatternDirection == 0)
        {
            return 0;
        }

        var direction = Math.Sign(interval);
        if (direction != position.PatternDirection)
        {
            return direction == 0 ? 5.5 : 8.0;
        }

        var distance = Math.Abs(interval);
        return distance switch
        {
            <= 5 => -0.85,
            <= 7 => -0.35,
            <= BassHarmonicMotion.AbsoluteMaximumLeap => 0.55,
            _ => 6.0
        };
    }

    private static double HarmonicArrivalCost(
        BassPosition currentPosition,
        ChordSpec? sourceChord,
        byte previous,
        byte current,
        bool previousWasApproach)
    {
        if (!currentPosition.IsNewHarmony)
        {
            if (!previousWasApproach)
            {
                return 0;
            }

            var approachInterval = Math.Abs(current - previous);
            return approachInterval switch
            {
                1 => -1.10,
                2 => -0.72,
                3 => -0.20,
                <= 5 => 0.15,
                _ => 0.65
            };
        }

        var interval = Math.Abs(current - previous);
        var smoothness = interval switch
        {
            0 => -0.30,
            1 => -1.55,
            2 => -1.20,
            3 => -0.62,
            4 => -0.38,
            5 => -0.08,
            <= 7 => 0.35,
            _ => 1.80 + (interval - 7) * 0.55
        };

        var functionalStrength = sourceChord is null
            ? 0
            : BassHarmonicMotion.FunctionalMotionStrength(sourceChord, currentPosition.Chord);
        var foundationBonus = PitchClass(current) == FoundationPitchClass(currentPosition.Chord)
            ? -0.48 - functionalStrength * 0.42
            : 0.0;
        var resolvedApproach = previousWasApproach
            ? interval == 1 ? -0.68 : interval == 2 ? -0.34 : 0.0
            : 0.0;

        return smoothness + foundationBonus + resolvedApproach;
    }

    private static double DirectionCost(int previousDirection, int direction, int positionIndex)
    {
        if (previousDirection == 0 || direction == 0) return 0.08;
        var nearTurn = positionIndex % 16 is 7 or 8 or 15;
        return previousDirection == direction ? (nearTurn ? 0.18 : -0.06) : (nearTurn ? -0.10 : 0.15);
    }

    private static double DirectionRunCost(int run) => run switch
    {
        <= 3 => 0,
        4 => 0.20,
        5 => 0.75,
        6 => 1.50,
        7 => 3.00,
        8 => 6.00,
        _ => 40.0 + (run - 9) * 8.0
    };
    private static double RepeatedIntervalCost(int previous, int current) => previous != 0 && previous == current ? 0.42 : previous == -current && current != 0 ? 0.16 : 0;

    private static double HistoryCost(byte note, int interval, IReadOnlyList<byte> history, int positionIndex)
    {
        if (history.Count == 0 || positionIndex > 2) return 0;
        var cost = note == history[^1] ? 0.85 : 0;
        if (history.Count >= 2 && note == history[^2]) cost += 0.34;
        if (history.Count >= 3)
        {
            var priorInterval = history[^1] - history[^2];
            if (interval == priorInterval && interval != 0) cost += 0.38;
            if (note == history[^3]) cost += 0.20;
        }
        return cost;
    }

    private static double ContourCost(
        byte note,
        int index,
        int count,
        int seed,
        BassPosition position,
        PerformanceGuidance guidance)
    {
        var length = Math.Min(16, Math.Max(4, count));
        var t = length <= 1 ? 0 : index % length / (double)(length - 1);
        var shape = (int)(DeterministicNoise.Unit(seed, index / length, 1711) * 4) % 4;
        var energy = position.Feel == RhythmFeel.TwoBeat
            ? 0.28
            : guidance.HighStage ? 0.84 : 0.52;
        var center = position.Feel == RhythmFeel.TwoBeat
            ? BassHarmonicMotion.ShapeRegisterCenter(35, 39, energy, position.Function)
            : BassHarmonicMotion.ShapeRegisterCenter(38, 42, energy, position.Function);
        var contourExpansion = guidance.HighStage && position.Feel == RhythmFeel.FourBeat
            ? 1.06
            : 1.0;
        var phraseOffset = shape switch
        {
            0 => -2.5 + 5.0 * t,
            1 => 2.5 - 5.0 * t,
            2 => -1.8 + 4.2 * (1 - Math.Abs(2 * t - 1)),
            _ => 1.8 - 4.2 * (1 - Math.Abs(2 * t - 1))
        } * contourExpansion;
        var contourCost = Math.Abs(note - (center + phraseOffset)) * 0.075;
        var upperAccentBoundary = position.Function switch
        {
            PhraseFunction.Build => guidance.HighStage ? 50 : 48,
            PhraseFunction.Setup => guidance.HighStage ? 49 : 47,
            PhraseFunction.Answer or PhraseFunction.Comment => 47,
            _ => 45
        };
        // The upper octave is an accent register, not the new home register
        // after walking starts. This soft boundary still permits an explicit
        // octave idiom, but makes an unmarked multi-bar climb increasingly
        // expensive and encourages an early return to the middle register.
        if (note > upperAccentBoundary)
        {
            contourCost += (note - upperAccentBoundary) * 0.78;
        }
        return contourCost;
    }

    private static int CalculateDirectionRun(IReadOnlyList<byte> generated, byte? previousNote, int previousDirection, int previousRun)
    {
        var direction = previousDirection;
        var run = previousRun;
        var prior = previousNote;
        foreach (var note in generated)
        {
            if (prior is null) { prior = note; continue; }
            var next = Math.Sign(note - prior.Value);
            run = next == 0 ? 0 : next == direction ? run + 1 : 1;
            direction = next;
            prior = note;
        }
        return run;
    }

    private static int FoundationPitchClass(ChordSpec chord) => Mod12(chord.BassFoundationPitchClass);

    private static bool SameHarmony(ChordSpec first, ChordSpec second) =>
        BassHarmonicMotion.SameHarmony(first, second);

    private static bool IsOnePlusThreeSplit(TuneBar bar) =>
        bar.BeatsPerBar == SessionConstants.BeatsPerBar
        && bar.ChordChanges.Count == 2
        && bar.ChordChanges[1].StartBeat is 1 or 3;

    private static bool IsTraditionalArpeggioEligible(ChordSpec chord)
    {
        var symbol = chord.Symbol.ToLowerInvariant();
        if (chord.IsOnChord) return false;
        if (symbol.Contains("alt", StringComparison.Ordinal)
            || symbol.Contains("dim", StringComparison.Ordinal)
            || symbol.Contains("aug", StringComparison.Ordinal)
            || symbol.Contains("sus", StringComparison.Ordinal)) return false;
        return ChordalThirdPitchClass(chord) is not null && ChordalFifthPitchClass(chord) is not null;
    }

    private static int? SelectTwoFeelConnectionTone(
        BassPosition position,
        int seed,
        int positionIndex,
        int? fifthPitchClass)
    {
        if (position.Feel != RhythmFeel.TwoBeat
            || position.BeatInBar != 2
            || !position.LeadsToNewChord
            || fifthPitchClass is null
            || position.Chord.IsOnChord)
        {
            return null;
        }

        var nextFoundation = FoundationPitchClass(position.NextChord);
        var fifthDistance = CircularPitchClassDistance(fifthPitchClass.Value, nextFoundation);
        var alternatives = new[]
            {
                ChordalThirdPitchClass(position.Chord),
                ChordalSeventhPitchClass(position.Chord)
            }
            .Where(pitchClass => pitchClass is not null)
            .Select(pitchClass => pitchClass!.Value)
            .Distinct()
            .Select(pitchClass => new
            {
                PitchClass = pitchClass,
                Distance = CircularPitchClassDistance(pitchClass, nextFoundation)
            })
            .Where(candidate => candidate.Distance <= fifthDistance)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.PitchClass)
            .ToArray();

        if (alternatives.Length == 0)
        {
            return null;
        }

        var best = alternatives[0];
        var probability = best.Distance < fifthDistance ? 0.20 : 0.10;
        return DeterministicNoise.Unit(seed, position.BarIndex, positionIndex, 1731) < probability
            ? best.PitchClass
            : null;
    }

    private static int? ChordalThirdPitchClass(ChordSpec chord) =>
        BassPitchVocabulary.ThirdPitchClass(chord);

    private static int? TwoFeelFifthPitchClass(ChordSpec chord) =>
        ChordalFifthPitchClass(chord);

    private static int[] TwoFeelBeat3PitchClasses(ChordSpec chord)
    {
        var result = new List<int>();
        AddPitchClass(result, TwoFeelFifthPitchClass(chord));
        AddPitchClass(result, ChordalThirdPitchClass(chord));
        AddPitchClass(result, ChordalSeventhPitchClass(chord));
        AddPitchClass(result, FoundationPitchClass(chord));

        foreach (var pitchClass in BasicBassPitchClasses(chord))
        {
            AddPitchClass(result, pitchClass);
        }

        return result.ToArray();
    }

    private static int? ChordalFifthPitchClass(ChordSpec chord) =>
        BassPitchVocabulary.FifthPitchClass(chord);

    private static int? ChordalSeventhPitchClass(ChordSpec chord) =>
        BassPitchVocabulary.SeventhPitchClass(chord);

    private static int[] BasicBassPitchClasses(ChordSpec chord)
    {
        if (chord.IsOnChord)
        {
            return chord.OnChordBassPitchClasses.Select(Mod12).ToArray();
        }

        return BassPitchVocabulary.StructuralChordPitchClasses(chord).ToArray();
    }

    private static IReadOnlyList<int> DownbeatBassPitchClasses(ChordSpec chord)
    {
        if (chord.IsOnChord)
        {
            return chord.OnChordBassPitchClasses.Select(Mod12).Distinct().ToArray();
        }

        var result = new List<int>();
        AddPitchClass(result, FoundationPitchClass(chord));
        AddPitchClass(result, ChordalFifthPitchClass(chord));
        AddPitchClass(result, ChordalThirdPitchClass(chord));
        return result;
    }

    private static bool IsHalfDiminished(ChordSpec chord)
    {
        var symbol = chord.Symbol.ToLowerInvariant();
        return symbol.Contains("m7b5", StringComparison.Ordinal)
            || symbol.Contains("min7b5", StringComparison.Ordinal)
            || symbol.Contains("half", StringComparison.Ordinal)
            || symbol.Contains("ø", StringComparison.Ordinal);
    }

    private static void AddPitchClass(ICollection<int> pitchClasses, int? pitchClass)
    {
        if (pitchClass is int value && !pitchClasses.Contains(Mod12(value)))
        {
            pitchClasses.Add(Mod12(value));
        }
    }

    private static void AddCandidate(IDictionary<byte, double> candidates, byte note, double cost) { if (!candidates.TryGetValue(note, out var current) || cost < current) candidates[note] = cost; }
    private static void AddOrReplace(IDictionary<StateKey, PathState> layer, StateKey key, PathState state) { if (!layer.TryGetValue(key, out var current) || state.Score < current.Score) layer[key] = state; }
    private static IReadOnlyList<byte> GetNotesForPitchClasses(
        IEnumerable<int> pitchClasses,
        int maximumNote = MaximumNote)
    {
        var set = pitchClasses.Select(Mod12).ToHashSet();
        return Enumerable.Range(MinimumNote, maximumNote - MinimumNote + 1)
            .Where(note => set.Contains(PitchClass(note)))
            .Select(note => (byte)note)
            .ToArray();
    }
    private static int PitchClass(int note) => Mod12(note);
    private static int Mod12(int value) => ((value % 12) + 12) % 12;
    private static int CircularPitchClassDistance(int first, int second) { var d = Math.Abs(first - second); return Math.Min(d, 12 - d); }

    private sealed record BassCandidate(byte Note, double BaseCost);
    private sealed record BassPatternStep(
        int PitchClass,
        int Direction,
        int RegisterAnchor = 0,
        int FoundationOctaveDirection = 0);
    private readonly record struct StateKey(byte Note, int Direction, int DirectionRun, int LastInterval);
    private sealed record PathState(double Score, StateKey? Previous);
    private sealed record BassPosition(long GridTick, int BarIndex, int BeatInBar, ChordSpec Chord, bool IsChordOnset, bool IsBarDownbeat, bool IsNewHarmony, PhraseFunction Function, ChordSpec NextChord, bool LeadsToNewChord, bool AllowChromaticApproach, RhythmFeel Feel, int? PatternPitchClass, int PatternDirection, int PatternRegisterAnchor, bool RootOnlySplit, bool IsOffbeat, int FoundationOctaveDirection = 0);
}
