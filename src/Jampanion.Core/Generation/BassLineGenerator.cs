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
    private const int MinimumNote = 31;
    private const int MaximumNote = 55;
    private const int TwoFeelMaximumNote = 50;
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
        int initialTwoBeatTransitionRun = 0)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);
        if (bars.Count != arrangements.Count) throw new ArgumentException("Bars and arrangements must have the same length.");
        if (bars.Count == 0) throw new ArgumentException("At least one bar is required.", nameof(bars));

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var history = (recentNotes ?? Array.Empty<byte>()).TakeLast(HistoryLength).ToArray();
        var positions = BuildPositions(
            bars,
            followingChord,
            feel,
            arrangements,
            seed,
            prepareNextFourFeel,
            initialTwoBeatTransitionRun);
        var selected = FindBestLine(positions, previousNote, history, previousDirection, previousDirectionRun, seed);
        var generated = selected;
        var notes = new List<ScheduledNote>(generated.Length);
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;

        for (var i = 0; i < generated.Length; i++)
        {
            var position = positions[i];
            var start = SwingTiming.BassStart(position.GridTick, feel, guidance, position.Function);
            var isPickup = feel == RhythmFeel.TwoBeat && position.IsOffbeat;
            var isBoundaryTail = feel == RhythmFeel.TwoBeat && i == positions.Length - 1;
            var followsPickup = feel == RhythmFeel.TwoBeat
                && i + 1 < positions.Length
                && positions[i + 1].IsOffbeat;
            var nextStart = i + 1 < positions.Length
                ? SwingTiming.BassStart(
                    positions[i + 1].GridTick,
                    feel,
                    guidance,
                    positions[i + 1].Function)
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
                    : guidance.HighStage ? 380L : 420L;
            var duration = SwingTiming.ClampDuration(start, baseDuration, segmentLength);
            // Drive comes primarily from placement and connected voice-leading, not
            // from accenting every harmony change. Keep the quarter-note pulse even.
            var velocityBase = feel == RhythmFeel.TwoBeat ? 76 : 72;
            var accent = position.IsChordOnset ? 4 : position.IsBarDownbeat ? 2 : isPickup ? -5 : 0;
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
            var velocity = (byte)Math.Clamp(velocityBase + accent + phraseShape + variation + interactionLift + arrangementLift, 50, 96);
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
                    false));
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
        }

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

    private static IReadOnlyDictionary<(int Bar, int Beat), BassPatternStep> BuildPatternAssignments(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        RhythmFeel feel,
        int seed,
        bool prepareNextFourFeel)
    {
        var result = new Dictionary<(int Bar, int Beat), BassPatternStep>();

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
            if (selector < 0.30)
            {
                AddPattern(result, bar, [0, 1, 2, 3],
                    [FoundationPitchClass(chord), Mod12(chord.RootPitchClass + 2), third.Value, fifth.Value],
                    [0, 1, 1, 1]);
            }
            else if (selector < 0.44 && seventh is int seventhPitchClass)
            {
                AddPattern(result, bar, [0, 1, 2, 3],
                    [FoundationPitchClass(chord), third.Value, fifth.Value, seventhPitchClass],
                    [0, 1, 1, 1]);
            }
            else if (selector < 0.54 && seventh is int descendingSeventh)
            {
                AddPattern(result, bar, [0, 1, 2, 3],
                    [FoundationPitchClass(chord), descendingSeventh, fifth.Value, third.Value],
                    [0, -1, -1, -1]);
            }
            else if (selector < 0.66)
            {
                AddPattern(result, bar, [0, 1, 2, 3],
                    [FoundationPitchClass(chord), third.Value, fifth.Value, FoundationPitchClass(chord)],
                    [0, 1, 1, 1]);
            }
        }

        return result;
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
        int seed)
    {
        var layers = new Dictionary<StateKey, PathState>[positions.Count];
        var initialReference = previousNote ?? FindNearestNote(FoundationPitchClass(positions[0].Chord), 38);
        for (var positionIndex = 0; positionIndex < positions.Count; positionIndex++)
        {
            var candidates = BuildCandidates(positions[positionIndex], seed, positionIndex);
            var layer = new Dictionary<StateKey, PathState>();
            if (positionIndex == 0)
            {
                foreach (var candidate in candidates)
                {
                    var interval = candidate.Note - initialReference;
                    var maximumLeap = positions[positionIndex].Feel == RhythmFeel.TwoBeat ? 9 : 12;
                    if (Math.Abs(interval) > maximumLeap) continue;
                    var direction = Math.Sign(interval);
                    var run = direction != 0 && direction == previousDirection ? previousDirectionRun + 1 : direction == 0 ? 0 : 1;
                    if (run > 8) continue;
                    var score = candidate.BaseCost + TransitionCost(initialReference, candidate.Note)
                        + (positions[positionIndex].Feel == RhythmFeel.TwoBeat
                            ? 0.0
                            : HarmonicArrivalCost(positions[positionIndex], initialReference, candidate.Note, previousWasApproach: false))
                        + DirectionRunCost(run) + HistoryCost(candidate.Note, interval, history, 0)
                        + ContourCost(candidate.Note, positionIndex, positions.Count, seed, positions[positionIndex].Feel);
                    AddOrReplace(layer, new StateKey(candidate.Note, direction, run, interval), new PathState(score, null));
                }
            }
            else
            {
                foreach (var prior in layers[positionIndex - 1])
                foreach (var candidate in candidates)
                {
                    var interval = candidate.Note - prior.Key.Note;
                    // Two-feel is a grounded half-note line. An octave displacement
                    // between adjacent attacks sounds like a register accident, not
                    // a melodic choice; keep it within a fifth. Four-feel retains
                    // the wider walking-line allowance.
                    var maximumLeap = positions[positionIndex].Feel == RhythmFeel.TwoBeat ? 7 : 12;
                    if (Math.Abs(interval) > maximumLeap) continue;
                    var direction = Math.Sign(interval);
                    var run = direction != 0 && direction == prior.Key.Direction ? prior.Key.DirectionRun + 1 : direction == 0 ? 0 : 1;
                    if (run > 8) continue;
                    var score = prior.Value.Score + candidate.BaseCost + TransitionCost(prior.Key.Note, candidate.Note)
                        + (positions[positionIndex].Feel == RhythmFeel.TwoBeat
                            ? 0.0
                            : HarmonicArrivalCost(positions[positionIndex], prior.Key.Note, candidate.Note, positions[positionIndex - 1].AllowChromaticApproach))
                        + PatternMotionCost(positions[positionIndex], interval)
                        + DirectionCost(prior.Key.Direction, direction, positionIndex)
                        + DirectionRunCost(run) + RepeatedIntervalCost(prior.Key.LastInterval, interval)
                        + HistoryCost(candidate.Note, interval, history, positionIndex)
                        + ContourCost(candidate.Note, positionIndex, positions.Count, seed, positions[positionIndex].Feel);
                    AddOrReplace(layer, new StateKey(candidate.Note, direction, run, interval), new PathState(score, prior.Key));
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
            // A two-feel 4& is a short connector into the following structural
            // beat. Keep it independent of the current chord's voicing so a
            // slash chord cannot turn the pickup into a long fifth/root repeat.
            var target = FoundationPitchClass(position.NextChord);
            foreach (var pc in new[]
                     {
                         Mod12(target - 1), Mod12(target + 1),
                         Mod12(target - 2), Mod12(target + 2)
                     })
            {
                var distance = CircularPitchClassDistance(pc, target);
                var cost = distance == 1 ? -2.20 : -0.85;
                foreach (var note in GetNotesForPitchClasses([pc]))
                {
                    AddCandidate(result, note, cost);
                }
            }

            // A direct target is a safe fallback when the constrained acoustic
            // register makes both chromatic sides awkward.
            foreach (var note in GetNotesForPitchClasses([target]))
            {
                AddCandidate(result, note, 0.40);
            }

            return Finish(result, seed, positionIndex, 1697, position.Feel);
        }

        if (position.Feel == RhythmFeel.FourBeat && position.IsBarDownbeat)
        {
            // A walking line may voice-lead its downbeat, but the first pulse
            // must still read as the chord foundation.  Do not let a seventh
            // win merely because it happens to be closer to the previous note.
            var downbeatPitchClasses = DownbeatBassPitchClasses(chord);
            for (var index = 0; index < downbeatPitchClasses.Count; index++)
            {
                var pitchClass = downbeatPitchClasses[index];
                var cost = index switch
                {
                    0 => -5.4,
                    1 => -1.6,
                    _ => -1.2
                };
                foreach (var note in GetNotesForPitchClasses([pitchClass]))
                {
                    AddCandidate(result, note, cost);
                }
            }

            return Finish(result, seed, positionIndex, 1697, position.Feel);
        }

        if (chord.IsOnChord)
        {
            foreach (var note in GetNotesForPitchClasses(chord.OnChordBassPitchClasses))
            {
                var cost = PitchClass(note) == chord.RootPitchClass ? -6.0 : -1.10;
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
            if (position.PatternRegisterAnchor > 0)
            {
                patternNotes = [patternNotes.Max()];
            }
            else if (position.PatternRegisterAnchor < 0)
            {
                patternNotes = [patternNotes.Min()];
            }

            foreach (var note in patternNotes)
            {
                AddCandidate(result, note, -4.20);
            }

            foreach (var note in position.PatternRegisterAnchor == 0
                         ? GetNotesForPitchClasses(pcs)
                         : patternNotes)
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

        if (position.IsBarDownbeat)
        {
            var selector = DeterministicNoise.Unit(seed, position.BarIndex, positionIndex, 1703);
            var preferred = position.Feel == RhythmFeel.TwoBeat
                ? 0
                : selector < 0.55 ? 0 : selector < 0.70 ? 1 : 2;
            for (var i = 0; i < Math.Min(3, pcs.Length); i++)
            {
                var cost = position.Feel == RhythmFeel.TwoBeat
                    ? i switch { 0 => -1.55, 1 => 1.10, 2 => -0.05, _ => 0.75 }
                    : i switch { 0 => -0.42, 1 => 0.08, 2 => -0.12, _ => 0.55 };
                if (i == preferred) cost -= position.Feel == RhythmFeel.TwoBeat ? 1.30 : 1.05;
                foreach (var note in GetNotesForPitchClasses([pcs[i]])) AddCandidate(result, note, cost);
            }
            return Finish(result, seed, positionIndex, 1704, position.Feel);
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
            // Keep the established four-feel approach vocabulary, while
            // making a non-approach result deliberately less chromatic.
            var targets = BasicBassPitchClasses(position.NextChord).Take(3).ToArray();
            var useApproach = DeterministicNoise.Unit(seed, position.BarIndex, position.BeatInBar, 1705) < 0.42;
            for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
            {
                var target = targets[targetIndex];
                foreach (var pc in new[] { Mod12(target - 1), Mod12(target + 1), Mod12(target - 2), Mod12(target + 2) })
                {
                    var distance = CircularPitchClassDistance(pc, target);
                    var targetPenalty = targetIndex * 0.22;
                    var cost = useApproach
                        ? distance == 1 ? -0.95 + targetPenalty : -0.10 + targetPenalty
                        : distance == 1 ? 2.15 + targetPenalty : 2.75 + targetPenalty;
                    foreach (var note in GetNotesForPitchClasses([pc])) AddCandidate(result, note, cost);
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
            <= 12 => 0.55,
            _ => 6.0
        };
    }

    private static double HarmonicArrivalCost(
        BassPosition currentPosition,
        byte previous,
        byte current,
        bool previousWasApproach)
    {
        if (!currentPosition.IsNewHarmony)
        {
            return 0;
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

        var foundationBonus = PitchClass(current) == FoundationPitchClass(currentPosition.Chord)
            ? -0.32
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

    private static double ContourCost(byte note, int index, int count, int seed, RhythmFeel feel)
    {
        var length = Math.Min(16, Math.Max(4, count));
        var t = length <= 1 ? 0 : index % length / (double)(length - 1);
        var shape = (int)(DeterministicNoise.Unit(seed, index / length, 1711) * 4) % 4;
        var target = feel == RhythmFeel.TwoBeat
            ? shape switch
            {
                0 => 34 + 8 * t,
                1 => 43 - 8 * t,
                2 => 36 + 7 * (1 - Math.Abs(2 * t - 1)),
                _ => 42 - 7 * (1 - Math.Abs(2 * t - 1))
            }
            : shape switch
            {
                0 => 36 + 11 * t,
                1 => 47 - 10 * t,
                2 => 38 + 10 * (1 - Math.Abs(2 * t - 1)),
                _ => 46 - 9 * (1 - Math.Abs(2 * t - 1))
            };
        return Math.Abs(note - target) * 0.060;
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
        first.RootPitchClass == second.RootPitchClass
        && FoundationPitchClass(first) == FoundationPitchClass(second)
        && string.Equals(first.Symbol, second.Symbol, StringComparison.OrdinalIgnoreCase);

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
    private static byte FindNearestNote(int pitchClass, int reference) => GetNotesForPitchClasses([pitchClass]).OrderBy(note => Math.Abs(note - reference)).First();
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
    private sealed record BassPatternStep(int PitchClass, int Direction, int RegisterAnchor = 0);
    private readonly record struct StateKey(byte Note, int Direction, int DirectionRun, int LastInterval);
    private sealed record PathState(double Score, StateKey? Previous);
    private sealed record BassPosition(long GridTick, int BarIndex, int BeatInBar, ChordSpec Chord, bool IsChordOnset, bool IsBarDownbeat, bool IsNewHarmony, PhraseFunction Function, ChordSpec NextChord, bool LeadsToNewChord, bool AllowChromaticApproach, RhythmFeel Feel, int? PatternPitchClass, int PatternDirection, int PatternRegisterAnchor, bool RootOnlySplit, bool IsOffbeat);
}
