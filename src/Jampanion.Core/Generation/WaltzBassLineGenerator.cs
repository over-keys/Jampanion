using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class WaltzBassLineGenerator
{
    // The model waltz uses the low F/Gb bridge (29/30). Keeping it available
    // prevents a first-theme root from being forced up an octave.
    private const int MinimumNote = 29;
    private const int MaximumNote = 55;
    private const int HistoryLength = 8;
    private const long WaltzEighthTicks = 320;
    // A short single offbeat is a connector; a held anticipation should keep
    // the normal bass presence instead of being mistaken for a ghost note.
    private const long ShortOffbeatDurationTicks = SessionConstants.Ppq / 2;
    private const int MaximumWalkingLeap = BassHarmonicMotion.AbsoluteMaximumLeap;
    private const int MaximumWalkingPatternLeap = BassHarmonicMotion.AbsoluteMaximumLeap;

    // A jazz waltz has a genuine pre-walking language.  Keep it separate from
    // the three-quarter walking pulse so that energy changes do not merely
    // delete or insert arbitrary notes in an already-built line.
    private enum WaltzBassFeel
    {
        PreWalkOne,
        PreWalkTwo,
        WalkThree
    }

    public static BassGenerationResult Generate(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        byte? previousNote,
        IReadOnlyList<byte>? recentNotes,
        int previousDirection,
        int previousDirectionRun,
        int seed,
        WaltzChorusStage stage,
        int chorusBarOffset,
        int chorusBarCount,
        PerformanceGuidance? performanceGuidance = null,
        bool prepareNextWalking = false,
        TimeFeelProfile? timeFeel = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);
        if (bars.Count == 0) throw new ArgumentException("At least one bar is required.", nameof(bars));
        if (bars.Count != arrangements.Count) throw new ArgumentException("Bars and arrangements must have the same length.");
        if (bars.Any(bar => bar.BeatsPerBar != 3)) throw new ArgumentException("Jazz waltz generation requires 3/4 bars.", nameof(bars));

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var timing = timeFeel ?? TimeFeelProfile.Resolve(AccompanimentStyle.JazzWaltz, 140);
        var events = BuildEvents(
            bars,
            followingChord,
            arrangements,
            stage,
            seed,
            chorusBarOffset,
            chorusBarCount,
            prepareNextWalking);
        events = ApplyOctaveEligibility(events);
        var segmentLength = (long)bars.Count * SessionConstants.GetBarTicks(3);
        var notes = new List<ScheduledNote>(events.Count);
        var generated = new List<byte>(events.Count);
        var lastNote = previousNote;
        var previousWasFoundationOctave = false;

        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            var arrangement = arrangements[Math.Min(item.BarIndex, arrangements.Count - 1)];
            var registerCenter = RegisterCenter(stage, item.Feel, arrangement.Function);
            var registerMaximum = item.PatternOctaveShift > 0
                ? BassHarmonicMotion.LowOctaveUpperMaximum
                : StageMaximum(stage, item.Feel, arrangement.Function);
            if (previousWasFoundationOctave)
            {
                // The attack after an octave is the return point.  Do not let
                // the selector choose another upper-register chord tone before
                // the line has settled back into its low register.
                registerMaximum = Math.Min(
                    registerMaximum,
                    BassHarmonicMotion.LowOctaveUpperMaximum);
            }
            var maximumLeap = item.Feel == WaltzBassFeel.WalkThree && item.PatternOctaveShift == 0
                ? MaximumWalkingLeap
                : MaximumWalkingPatternLeap;
            // The register ceiling is part of candidate generation.  The
            // selected note is emitted unchanged; no later musical rewrite is
            // allowed to replace it.
            registerMaximum = ApplyWalkingRegisterCeiling(
                registerMaximum,
                registerCenter,
                recentNotes,
                generated);
            var note = SelectNote(
                item,
                lastNote,
                stage,
                arrangement.Function,
                registerMaximum,
                maximumLeap,
                index == 0 && previousNote is null);
            var extraLeadMilliseconds = item.Feel == WaltzBassFeel.WalkThree ? 1.1 : 0.5;
            if (arrangement.Function is PhraseFunction.Build or PhraseFunction.Setup) extraLeadMilliseconds += 0.5;
            var start = Math.Clamp(
                timing.Place(item.Tick, TimeFeelRole.Bass) - timing.MillisecondsToTicks(extraLeadMilliseconds),
                0,
                segmentLength - 1);
            var isFinalEvent = index + 1 >= events.Count;
            var nextTick = !isFinalEvent
                ? timing.MapGrid(events[index + 1].Tick)
                : segmentLength;
            var requestedHold = timing.MapGrid(item.HoldUntilTick ?? item.Tick + SessionConstants.Ppq);
            // Walking notes should carry the line into the next attack. In
            // particular, a beat-2 note must not be cut at beat 3 when the next
            // event is a swung beat-3-and pickup. Passing tones retain their
            // shorter articulation; the quarter-note framework remains legato.
            var holdUntil = item.IsPassing
                ? isFinalEvent
                    // A segment may end on a pre-walk pickup. Let that note
                    // carry to the next segment's downbeat instead of ending
                    // at the artificial four-bar generation boundary.
                    ? nextTick
                    : Math.Min(nextTick, requestedHold)
                : nextTick;
            // Keep the waltz bass legato into the next attack. The former
            // 12-tick gap was audible in the sparse pre-walk language and
            // made the transition into three-beat walking sound clipped.
            // Duration is determined here from the next planned attack; no
            // later overlap pass is allowed to shorten this note.
            const long releaseGap = 0;
            var maximumDuration = item.IsPassing
                ? isFinalEvent ? SessionConstants.GetBarTicks(3) : 560
                : SessionConstants.GetBarTicks(3);
            var baseDuration = Math.Clamp(
                holdUntil - start - releaseGap,
                120,
                maximumDuration);
            // Sparse pre-walk anchors must sustain all the way into the next
            // attack. Applying the ordinary walking gate to a full bar leaves
            // an audible hole at slow/medium waltz tempos. Offbeat pickups and
            // the active three-beat walk retain their articulated gate.
            var performedDuration = item.Feel == WaltzBassFeel.WalkThree || item.IsPassing
                ? timing.ScaleGate(baseDuration, TimeFeelRole.Bass)
                : baseDuration;
            var duration = Math.Min(
                Math.Min(performedDuration, Math.Max(1, holdUntil - start)),
                segmentLength - start);
            var isOffbeat = item.Tick % SessionConstants.Ppq != 0;
            var isShortOffbeat = isOffbeat && duration <= ShortOffbeatDurationTicks;
            var stageLift = stage switch
            {
                WaltzChorusStage.Lifted => 2,
                WaltzChorusStage.Opening or WaltzChorusStage.HeadOut => -2,
                _ => 0
            };
            var interactionLift = guidance.HighStage ? 2 : 0;
            var phraseLift = arrangement.Function switch
            {
                PhraseFunction.Build => 2,
                PhraseFunction.Setup => 1,
                PhraseFunction.Space => -3,
                PhraseFunction.Release => -1,
                _ => 0
            };
            // Offbeats are light connectors. In particular, 1& must not
            // inherit the beat-1 accent merely because BeatInBar is zero.
            var beatShape = item.BeatInBar switch
                {
                    0 => 3,
                    _ => 0
                };
            // Keep the three quarter-note attacks clearly in front. Passing
            // eighths remain audible as ghosted motion but never inherit the
            // stage or phrase lift of the structural walking pulse.
            var velocity = isShortOffbeat
                ? (byte)Math.Clamp(52 + (guidance.HighStage ? 1 : 0), 48, 56)
                : (byte)Math.Clamp(70 + stageLift + interactionLift + phraseLift + beatShape, 56, 82);
            notes.Add(new ScheduledNote(
                start,
                duration,
                note,
                velocity,
                SessionConstants.BassChannel,
                AllowSamePitchTouch: item.Feel is WaltzBassFeel.PreWalkOne or WaltzBassFeel.PreWalkTwo));
            generated.Add(note);
            previousWasFoundationOctave = item.PatternOctaveShift > 0 &&
                Mod12(note) == Mod12(item.Chord.BassFoundationPitchClass) &&
                note > BassHarmonicMotion.LowOctaveUpperMaximum - 12;
            lastNote = note;
        }

        // A completely silent/no-chord segment is legal (and can occur in an
        // imported chart).  Keep the continuity context without indexing an
        // empty generated list; the old generated[^1] exception stopped
        // playback at the next segment boundary.
        var history = (recentNotes ?? Array.Empty<byte>()).Concat(generated).TakeLast(HistoryLength).ToArray();
        var lastNoteForContext = generated.Count > 0
            ? generated[^1]
            : previousNote ?? FindFallbackNote(bars, followingChord);
        var lastDirection = generated.Count >= 2 ? Math.Sign(generated[^1] - generated[^2]) : previousDirection;
        var directionRun = generated.Count >= 2 && lastDirection != 0
            ? lastDirection == previousDirection ? Math.Min(previousDirectionRun + 1, 4) : 1
            : previousDirectionRun;

        return new BassGenerationResult(notes, lastNoteForContext, history, lastDirection, directionRun);
    }

    private static byte FindFallbackNote(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord)
    {
        var chord = bars
            .SelectMany(bar => bar.ChordChanges.Select(change => change.Chord))
            .Concat([followingChord])
            .FirstOrDefault(candidate => !candidate.IsNoChord);
        return chord is null
            ? (byte)36
            : BassHarmonicMotion.ChooseOpeningFoundation(chord, MinimumNote, MaximumNote);
    }

    private static List<BassEvent> ApplyOctaveEligibility(
        IReadOnlyList<BassEvent> source)
    {
        var result = source.ToList();
        for (var index = 0; index < result.Count; index++)
        {
            var item = result[index];
            if (item.PatternOctaveShift <= 0 ||
                index + 1 >= result.Count)
            {
                if (item.PatternOctaveShift > 0)
                {
                    result[index] = item with { PatternOctaveShift = 0 };
                }

                continue;
            }

            var next = result[index + 1];
            // An upper foundation must have a same-harmony attack after it.
            // This both supplies the timing needed to return low and prevents
            // an octave immediately before a chord change from launching a
            // high-register connection.
            var allowed = SameHarmony(item.Chord, next.Chord) &&
                next.Tick - item.Tick >= SessionConstants.Ppq;
            if (!allowed)
            {
                result[index] = item with { PatternOctaveShift = 0 };
            }
        }

        return result;
    }

    private static List<BassEvent> BuildEvents(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        WaltzChorusStage stage,
        int seed,
        int chorusBarOffset,
        int chorusBarCount,
        bool prepareNextWalking)
    {
        var result = new List<BassEvent>(bars.Count * 3);
        var barTicks = SessionConstants.GetBarTicks(3);
        var lastWalkingDecorationBar = -2;
        var repeatedPatterns = BuildRepeatedHarmonyPatterns(
            bars, stage, seed, chorusBarOffset, chorusBarCount);

        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var barStart = (long)barIndex * barTicks;
            var nextBarChord = barIndex + 1 < bars.Count ? bars[barIndex + 1].Chord : followingChord;
            var arrangement = arrangements[barIndex];
            var absoluteBar = chorusBarOffset + barIndex;
            var feel = ResolveFeel(
                stage,
                absoluteBar,
                chorusBarCount,
                prepareNextWalking && barIndex == bars.Count - 1);
            // Imported grids sometimes repeat the same chord on every beat
            // (G7 G7 G7).  Those are one written bass harmony, not three
            // pre-walk attacks.  Collapse them before building events.
            var changes = EffectiveChordChanges(bar);

            AddWrittenChordEvents(
                result,
                barIndex,
                barStart,
                barTicks,
                changes,
                nextBarChord,
                feel,
                repeatedPatterns);

            if (prepareNextWalking &&
                barIndex == bars.Count - 1 &&
                feel != WaltzBassFeel.WalkThree &&
                !bar.GetChordAtBeat(2).IsNoChord &&
                !nextBarChord.IsNoChord)
            {
                // Preserve the pre-walk language in this bar. The final
                // offbeat pickup is the only added signal before three-beat
                // walking begins in the following chorus.
                AddPreWalkPickup(
                    result,
                    barIndex,
                    barStart,
                    bar.GetChordAtBeat(2),
                    nextBarChord,
                    2L * SessionConstants.Ppq + WaltzEighthTicks,
                    feel);
            }

            if (feel == WaltzBassFeel.WalkThree)
            {
                AddWalkingPulseEvents(result, bar, barIndex, barStart, barTicks, nextBarChord, feel, repeatedPatterns);
                var canDecorate = !(prepareNextWalking && barIndex == bars.Count - 1);
                var addDecoration = canDecorate &&
                    barIndex - lastWalkingDecorationBar >= 4 &&
                    ShouldAddWalkingPassingTone(stage, barIndex, arrangement, seed);
                if (addDecoration)
                {
                    AddWalkingDecoration(result, bar, barIndex, barStart, nextBarChord, feel, seed);
                    lastWalkingDecorationBar = barIndex;
                }

                continue;
            }

            if (changes.Count != 1)
            {
                // Written harmony changes are already represented at their
                // actual beat.  This is especially important for 2+1 and 1+2
                // bars: do not invent a fifth that hides the new chord.
                continue;
            }

            var chord = bar.Chord;
            var changesNextBar = !SameHarmony(chord, nextBarChord);
            if (feel == WaltzBassFeel.PreWalkOne)
            {
                if (changesNextBar && ShouldUseThemePickup(barIndex, arrangement, seed))
                {
                    AddPreWalkPickup(result, barIndex, barStart, chord, nextBarChord,
                        UseThemePickupAtBeatThree(barIndex, seed)
                            ? 2L * SessionConstants.Ppq
                            : 2L * SessionConstants.Ppq + WaltzEighthTicks,
                        feel);
                }
            }
            else
            {
                // The pattern map also contains single-bar cells for the
                // walking pulse.  Keep its downbeat colour, but never copy
                // the whole three-note cell into the pre-walk stage.
                // PreWalkTwo may add one anchor/pickup, but never the full
                // three-note walking cell.  The latter belongs only to
                // WalkThree; using every repeated-pattern key here caused
                // 1-2-3 attacks before the walking stage had begun.
                var offsets = SelectPreWalkTwoPattern(
                    absoluteBar,
                    chorusBarCount,
                    arrangement,
                    seed);

                foreach (var offset in offsets)
                {
                    AddPreWalkPickup(
                        result,
                        barIndex,
                        barStart,
                        chord,
                        nextBarChord,
                        offset,
                        feel,
                        repeatedPatterns.GetValueOrDefault((barIndex, (int)(offset / SessionConstants.Ppq))).PitchClass,
                        repeatedPatterns.GetValueOrDefault((barIndex, (int)(offset / SessionConstants.Ppq))).Direction);
                }
            }
        }

        return result.OrderBy(item => item.Tick).ToList();
    }

    private static WaltzBassFeel ResolveFeel(
        WaltzChorusStage stage,
        int absoluteBar,
        int chorusBarCount,
        bool prepareNextWalking)
    {
        if (stage is WaltzChorusStage.Opening or WaltzChorusStage.HeadOut)
        {
            return WaltzBassFeel.PreWalkOne;
        }

        if (stage is WaltzChorusStage.Developing or WaltzChorusStage.Lifted)
        {
            return WaltzBassFeel.WalkThree;
        }

        // Solo 1 opens with the one-note language, then moves to the two-note
        // anchor/pickup language. The explicit handoff pickup is added by
        // BuildEvents without changing this bar's underlying feel.
        var transitionBar = Math.Max(4, (int)Math.Ceiling(Math.Max(1, chorusBarCount) * 0.34));
        return absoluteBar >= transitionBar
            ? WaltzBassFeel.PreWalkTwo
            : WaltzBassFeel.PreWalkOne;
    }

    internal static bool IsWalkingAtBar(
        WaltzChorusStage stage,
        int absoluteBar,
        int chorusBarCount,
        bool prepareNextWalking)
    {
        return ResolveFeel(stage, absoluteBar, chorusBarCount, prepareNextWalking) == WaltzBassFeel.WalkThree;
    }

    private static void AddWrittenChordEvents(
        List<BassEvent> events,
        int barIndex,
        long barStart,
        long barTicks,
        IReadOnlyList<ChordChange> changes,
        ChordSpec nextBarChord,
        WaltzBassFeel feel,
        IReadOnlyDictionary<(int Bar, int Beat), WaltzPatternStep> repeatedPatterns)
    {
        foreach (var (change, changeIndex) in changes.Select((change, index) => (change, index)))
        {
            if (change.Chord.IsNoChord)
            {
                continue;
            }
            var onset = barStart + (long)change.StartBeat * SessionConstants.Ppq;
            var nextOnset = changeIndex + 1 < changes.Count
                ? barStart + (long)changes[changeIndex + 1].StartBeat * SessionConstants.Ppq
                : barStart + barTicks;
            var pattern = repeatedPatterns.GetValueOrDefault((barIndex, change.StartBeat));
            if ((feel == WaltzBassFeel.PreWalkOne && !pattern.UseInPreWalkOne) ||
                feel is not (WaltzBassFeel.PreWalkOne or WaltzBassFeel.PreWalkTwo or WaltzBassFeel.WalkThree))
            {
                pattern = default;
            }
            AddEventIfMissing(events, new BassEvent(
                onset,
                barIndex,
                change.StartBeat,
                change.Chord,
                nextOnset < barStart + barTicks ? changes[changeIndex + 1].Chord : nextBarChord,
                IsChordOnset: true,
                ApproachesNextHarmony: false,
                PreferFifth: false,
                IsPassing: false,
                HoldUntilTick: nextOnset,
                Feel: feel,
                PatternPitchClass: pattern.PitchClass,
                PatternDirection: pattern.Direction,
                PatternOctaveShift: pattern.OctaveShift));
        }
    }

    private static void AddWalkingPulseEvents(
        List<BassEvent> events,
        TuneBar bar,
        int barIndex,
        long barStart,
        long barTicks,
        ChordSpec nextBarChord,
        WaltzBassFeel feel,
        IReadOnlyDictionary<(int Bar, int Beat), WaltzPatternStep> patternMap)
    {
        for (var beat = 0; beat < 3; beat++)
        {
            var tick = barStart + beat * SessionConstants.Ppq;
            if (events.Any(item => item.BarIndex == barIndex && item.Tick == tick))
            {
                continue;
            }

            var chord = bar.GetChordAtBeat(beat);
            if (chord.IsNoChord)
            {
                continue;
            }
            var nextHarmony = beat < 2 ? bar.GetChordAtBeat(beat + 1) : nextBarChord;
            var pattern = patternMap.GetValueOrDefault((barIndex, beat));
            AddEventIfMissing(events, new BassEvent(
                tick,
                barIndex,
                beat,
                chord,
                nextHarmony,
                IsChordOnset: beat == 0,
                ApproachesNextHarmony: beat == 2 && !SameHarmony(chord, nextHarmony),
                PreferFifth: beat == 1,
                IsPassing: false,
                HoldUntilTick: beat == 0 ? barStart + SessionConstants.Ppq : null,
                Feel: feel,
                PatternPitchClass: pattern.PitchClass,
                PatternDirection: pattern.Direction,
                PatternOctaveShift: pattern.OctaveShift));
        }
    }

    private static void AddPreWalkPickup(
        List<BassEvent> events,
        int barIndex,
        long barStart,
        ChordSpec chord,
        ChordSpec nextBarChord,
        long offset,
        WaltzBassFeel feel,
        int? patternPitchClass = null,
        int patternDirection = 0,
        int patternOctaveShift = 0)
    {
        var beat = (int)(offset / SessionConstants.Ppq);
        var isOffbeat = offset % SessionConstants.Ppq != 0;
        var nextHarmony = beat == 2 ? nextBarChord : chord;
        AddEventIfMissing(events, new BassEvent(
            barStart + offset,
            barIndex,
            beat,
            chord,
            nextHarmony,
            IsChordOnset: false,
            ApproachesNextHarmony: beat == 2 && !SameHarmony(chord, nextHarmony),
            PreferFifth: false,
            // The late beat-2 pickup is the long side of the
            // | 1 - 2& - | hemiole.  It must fill the remaining bar; treating
            // it as a short passing eighth makes ScaleGate shorten the already
            // small 2& -> barline span and leaves an audible hole.
            IsPassing: isOffbeat && !IsHeldTwoAndPickup(offset),
            HoldUntilTick: null,
            Feel: feel,
            PatternPitchClass: patternPitchClass,
            PatternDirection: patternDirection,
            PatternOctaveShift: patternOctaveShift));
    }

    private static bool IsHeldTwoAndPickup(long offset) =>
        offset == 2L * SessionConstants.Ppq + WaltzEighthTicks;

    private static void AddWalkingDecoration(
        List<BassEvent> events,
        TuneBar bar,
        int barIndex,
        long barStart,
        ChordSpec nextBarChord,
        WaltzBassFeel feel,
        int seed)
    {
        // Once walking begins, beats 1, 2, and 3 remain the framework. A rare
        // swung offbeat is an added connector and never replaces a pulse.
        var selector = DeterministicNoise.Unit(seed, barIndex, 6231);
        var beat = selector switch
        {
            < 0.15 => 0,
            < 0.60 => 1,
            _ => 2
        };
        var offset = beat * SessionConstants.Ppq + WaltzEighthTicks;
        var chord = bar.GetChordAtBeat(beat);
        if (chord.IsNoChord)
        {
            return;
        }
        var nextHarmony = beat < 2
            ? bar.GetChordAtBeat(beat + 1)
            : nextBarChord;
        AddEventIfMissing(events, new BassEvent(
            barStart + offset,
            barIndex,
            beat,
            chord,
            nextHarmony,
            IsChordOnset: false,
            ApproachesNextHarmony: !SameHarmony(chord, nextHarmony),
            PreferFifth: false,
            IsPassing: true,
            HoldUntilTick: null,
            Feel: feel));
    }

    private static void AddEventIfMissing(List<BassEvent> events, BassEvent item)
    {
        if (!events.Any(existing => existing.BarIndex == item.BarIndex && existing.Tick == item.Tick))
        {
            events.Add(item);
        }
    }

    private static IReadOnlyList<ChordChange> EffectiveChordChanges(TuneBar bar)
    {
        var changes = bar.ChordChanges.OrderBy(change => change.StartBeat).ToArray();
        if (changes.Length <= 1)
        {
            return changes;
        }

        return changes
            .Where((change, index) => index == 0 ||
                !SameHarmony(changes[index - 1].Chord, change.Chord))
            .ToArray();
    }

    private static bool ShouldUseThemePickup(int barIndex, BarArrangement arrangement, int seed) =>
        DeterministicNoise.Unit(seed, barIndex, (int)arrangement.Function, 3103) <
            (arrangement.Function is PhraseFunction.Build or PhraseFunction.Setup ? 0.16 : 0.10);

    private static bool UseThemePickupAtBeatThree(int barIndex, int seed) =>
        DeterministicNoise.Unit(seed, barIndex, 6207) < 0.50;

    private static IReadOnlyList<long> SelectPreWalkTwoPattern(
        int chorusBarIndex,
        int chorusBarCount,
        BarArrangement arrangement,
        int seed)
    {
        if (arrangement.Function is PhraseFunction.Space or PhraseFunction.Release)
        {
            return Array.Empty<long>();
        }

        var selector = DeterministicNoise.Unit(seed, chorusBarIndex, (int)arrangement.Function, 6209);
        var isLate = chorusBarIndex >= Math.Max(1, chorusBarCount * 3 / 4);
        var isBuilding = arrangement.Function is PhraseFunction.Build or PhraseFunction.Setup;
        // Pre-walking is intentionally a spacious two-note language.  The
        // previous implementation always added one pickup to every bar,
        // which made a ballad-like waltz move almost as constantly as the
        // later three-beat walk.  Keep the root attack and add a connector
        // only on selected phrase bars.
        var pickupProbability = isBuilding ? 0.52 : isLate ? 0.24 : 0.36;
        if (selector >= pickupProbability)
        {
            return Array.Empty<long>();
        }

        var placement = DeterministicNoise.Unit(seed, chorusBarIndex, (int)arrangement.Function, 6210);
        if (placement < (isLate || isBuilding ? 0.28 : 0.18))
        {
            return [2L * SessionConstants.Ppq + WaltzEighthTicks];
        }

        return [2L * SessionConstants.Ppq];
    }

    private static IReadOnlyDictionary<(int Bar, int Beat), WaltzPatternStep> BuildRepeatedHarmonyPatterns(
        IReadOnlyList<TuneBar> bars,
        WaltzChorusStage stage,
        int seed,
        int chorusBarOffset,
        int chorusBarCount)
    {
        var result = new Dictionary<(int Bar, int Beat), WaltzPatternStep>();
        for (var bar = 0; bar < bars.Count; bar++)
        {
            var changes = EffectiveChordChanges(bars[bar]);
            if (changes.Count != 1)
            {
                continue;
            }

            var chord = bars[bar].Chord;
            if ((stage is WaltzChorusStage.Opening or WaltzChorusStage.Standard or WaltzChorusStage.HeadOut) &&
                !chord.IsNoChord && !chord.IsOnChord)
            {
                var runEnd = bar + 1;
                while (runEnd < bars.Count &&
                    EffectiveChordChanges(bars[runEnd]).Count == 1 &&
                    !bars[runEnd].Chord.IsNoChord &&
                    !bars[runEnd].Chord.IsOnChord &&
                    SameHarmony(chord, bars[runEnd].Chord))
                {
                    runEnd++;
                }

                if (runEnd - bar >= 2)
                {
                    AddSettledRepeatedHarmonyPattern(result, chord, bar, runEnd, seed, chorusBarOffset);
                    bar = runEnd - 1;
                    continue;
                }
            }

            if (chord.IsOnChord)
            {
                if (stage is WaltzChorusStage.Developing or WaltzChorusStage.Lifted)
                {
                    var pedal = chord.BassFoundationPitchClass;
                    result[(bar, 0)] = new(pedal, 0);
                    result[(bar, 1)] = new(pedal, 1, 1);
                    result[(bar, 2)] = new(pedal, -1, -1);
                }
                continue;
            }

            var sameNext = bar + 1 < bars.Count &&
                EffectiveChordChanges(bars[bar + 1]).Count == 1 &&
                !bars[bar + 1].Chord.IsOnChord &&
                SameHarmony(chord, bars[bar + 1].Chord);
            var root = chord.BassFoundationPitchClass;
            var third = FindChordTone(chord, root, 3, 4);
            var fifth = Mod12(chord.BassFifth);
            var second = Mod12(root + 2);
            var selector = DeterministicNoise.Unit(seed, chorusBarOffset + bar, (int)stage, 6221);

            // Two bars of one harmony are an ideal place for a complete
            // sentence: 1-3-5 followed by the octave pickup 8-5-3.  The
            // second bar's octave root is explicitly marked so it cannot be
            // collapsed to the old root/seventh/root shape.
            if (sameNext)
            {
                if (selector < 0.38)
                {
                    result[(bar, 0)] = new(root, 1, -1);
                    result[(bar, 1)] = new(third, 1);
                    result[(bar, 2)] = new(fifth, 1);
                    result[(bar + 1, 0)] = new(root, 1, 1);
                    result[(bar + 1, 1)] = new(fifth, -1);
                    result[(bar + 1, 2)] = new(third, -1);
                }
                else if (selector < 0.72)
                {
                    result[(bar, 0)] = new(root, -1, 1);
                    result[(bar, 1)] = new(fifth, -1);
                    result[(bar, 2)] = new(third, -1);
                    result[(bar + 1, 0)] = new(root, -1, -1);
                    result[(bar + 1, 1)] = new(third, 1);
                    result[(bar + 1, 2)] = new(fifth, 1);
                }
                else
                {
                    // Meter-shaped scalar idiom: the ninth is a passing 2
                    // which resolves directly to 3 while the harmony remains
                    // unchanged, then the upper root releases through 5 to 3.
                    result[(bar, 0)] = new(root, 1, -1);
                    result[(bar, 1)] = new(second, 1);
                    result[(bar, 2)] = new(third, 1);
                    result[(bar + 1, 0)] = new(root, 1, 1);
                    result[(bar + 1, 1)] = new(fifth, -1);
                    result[(bar + 1, 2)] = new(third, -1);
                }

                bar++;
                continue;
            }

            // Otherwise use one of four compact three-beat cells made from
            // stable chord tones. A 1-2-3 figure immediately before a harmony
            // change can become two consecutive tensions (2, then chromatic
            // approach), so scalar neighbours are reserved for a genuinely
            // extended repeated harmony.
            switch ((int)(selector * 4.0))
            {
                case 0:
                    result[(bar, 0)] = new(root, 1, -1);
                    result[(bar, 1)] = new(third, 1);
                    result[(bar, 2)] = new(fifth, 1);
                    break;
                case 1:
                    result[(bar, 0)] = new(root, 1);
                    result[(bar, 1)] = new(fifth, 1);
                    result[(bar, 2)] = new(third, -1);
                    break;
                case 2:
                    result[(bar, 0)] = new(root, 1);
                    result[(bar, 1)] = new(third, 1);
                    result[(bar, 2)] = new(root, -1);
                    break;
                default:
                    // Keep a single-bar cell in the prevailing register.
                    // Upper roots are reserved for a complete repeated-
                    // harmony sentence or an explicit slash-bass pedal, so
                    // an isolated bar cannot create a gratuitous register
                    // jump merely because its pattern was selected.
                    result[(bar, 0)] = new(root, 1);
                    result[(bar, 1)] = new(fifth, 1);
                    result[(bar, 2)] = new(root, -1);
                    break;
            }
        }

        return result;
    }

    private static bool ShouldAddWalkingPassingTone(
        WaltzChorusStage stage,
        int barIndex,
        BarArrangement arrangement,
        int seed)
    {
        var probability = stage == WaltzChorusStage.Developing
            ? arrangement.Function switch
            {
                PhraseFunction.Build or PhraseFunction.Setup => 0.06,
                PhraseFunction.Space => 0.01,
                PhraseFunction.Release => 0.02,
                _ => 0.035
            }
            : arrangement.Function switch
            {
                PhraseFunction.Build => 0.07,
                PhraseFunction.Setup => 0.06,
                PhraseFunction.Space => 0.01,
                PhraseFunction.Release => 0.02,
                _ => 0.04
            };
        return DeterministicNoise.Unit(seed, barIndex, (int)arrangement.Function, 6213) < probability;
    }

    private static int RegisterCenter(
        WaltzChorusStage stage,
        WaltzBassFeel feel,
        PhraseFunction function = PhraseFunction.Ground)
    {
        var energy = feel switch
        {
            WaltzBassFeel.PreWalkOne => stage == WaltzChorusStage.HeadOut ? 0.08 : 0.16,
            WaltzBassFeel.PreWalkTwo => 0.38,
            WaltzBassFeel.WalkThree when stage == WaltzChorusStage.Lifted => 0.94,
            WaltzBassFeel.WalkThree => 0.70,
            _ => 0.30
        };
        return BassHarmonicMotion.ShapeRegisterCenter(37, 40, energy, function);
    }

    private static byte SelectNote(
        BassEvent item,
        byte? previous,
        WaltzChorusStage stage,
        PhraseFunction function,
        int registerMaximum,
        int maximumLeap,
        bool isOpening)
    {
        var registerCenter = RegisterCenter(stage, item.Feel, function);
        if (isOpening && previous is null)
        {
            return BassHarmonicMotion.ChooseOpeningFoundation(
                item.Chord,
                MinimumNote,
                registerMaximum);
        }
        if (item.Feel == WaltzBassFeel.WalkThree &&
            item.BeatInBar == 0 &&
            !item.ApproachesNextHarmony)
        {
            // Walking may choose a chord tone for voice leading, but the first
            // pulse of a waltz bar must remain a clear 1/5/3 foundation. This
            // also prevents a pattern or a nearby seventh from becoming the
            // apparent downbeat root.
            var root = item.Chord.BassFoundationPitchClass;
            var fifth = BassPitchVocabulary.FifthPitchClass(item.Chord);
            var third = BassPitchVocabulary.ThirdPitchClass(item.Chord);
            var pitchClasses = DownbeatBassPitchClasses(item.Chord);
            var downbeatCandidates = pitchClasses
                .SelectMany(pitchClass => Enumerable.Range(
                        MinimumNote,
                        registerMaximum - MinimumNote + 1)
                    .Where(note => note % 12 == pitchClass)
                    .Select(note => (byte)note))
                .ToArray();
            downbeatCandidates = KeepSingableCandidates(downbeatCandidates, previous, maximumLeap);
            if (downbeatCandidates.Length > 0)
            {
                var patternIsRoot = item.PatternPitchClass is int pattern &&
                    Mod12(pattern) == Mod12(root) &&
                    item.PatternOctaveShift != 0;
                if (patternIsRoot)
                {
                    var targetRegister = registerCenter +
                        12 * item.PatternOctaveShift;
                    var rootCandidates = downbeatCandidates
                        .Where(note => note % 12 == Mod12(root))
                        .ToArray();
                    if (rootCandidates.Length > 0)
                    {
                        return rootCandidates
                            .OrderBy(note => Math.Abs(note - targetRegister))
                            .First();
                    }

                    // If the singable-leap filter removed every root octave,
                    // keep the selected pattern's register rather than asking
                    // LINQ for an impossible root candidate.
                    return downbeatCandidates
                        .OrderBy(note => Math.Abs(note - targetRegister))
                        .First();
                }

                return downbeatCandidates
                    .OrderBy(note => note % 12 == root ? 0 : note % 12 == fifth ? 1 : 2)
                    .ThenBy(note => previous is byte prior ? Math.Abs(note - prior) : Math.Abs(note - registerCenter))
                    .First();
            }
        }

        // Prefer the explicit waltz figure over the generic extension scorer.
        // This keeps the bass in a singable 1-3-5 / 1-5-3 / 8-5-3 vocabulary
        // and prevents the former 1-7-1 alternation.
        if (item.PatternPitchClass is int patternPitchClass && !item.ApproachesNextHarmony)
        {
            var patternCandidates = Enumerable.Range(MinimumNote, registerMaximum - MinimumNote + 1)
                .Where(note => note % 12 == Mod12(patternPitchClass))
                .Select(note => (byte)note)
                .ToArray();
            patternCandidates = KeepSingableCandidates(patternCandidates, previous, maximumLeap);
            if (patternCandidates.Length > 0)
            {
                var targetRegister = registerCenter + 12 * item.PatternOctaveShift;
                // A cell describes its local melodic direction, not a command
                // to keep climbing or falling across the whole chorus. Once
                // the line reaches the edge of the stage register, relax an
                // outward direction and let the next chord tone turn it back
                // toward the centre.
                var effectiveDirection = item.PatternDirection;
                if (previous is byte registerPrior &&
                    (registerPrior >= registerCenter + 5 && effectiveDirection > 0 ||
                        registerPrior <= registerCenter - 5 && effectiveDirection < 0))
                {
                    effectiveDirection = 0;
                }
                var directed = previous is byte prior
                    ? effectiveDirection > 0
                        ? patternCandidates.Where(note => note > prior).ToArray()
                        : effectiveDirection < 0
                            ? patternCandidates.Where(note => note < prior).ToArray()
                            : Array.Empty<byte>()
                    : Array.Empty<byte>();
                var directedCandidates = directed.Length > 0 ? directed : patternCandidates;
                return directedCandidates
                    .OrderBy(note => Math.Abs(note - targetRegister) * 0.28
                        + (previous is byte priorNote ? Math.Abs(note - priorNote) * 0.12 : 0.0))
                    .First();
            }
        }

        if (item.Chord.IsOnChord && !item.ApproachesNextHarmony)
        {
            var onChordMaximum = registerMaximum;
            var onChordCandidates = Enumerable.Range(MinimumNote, onChordMaximum - MinimumNote + 1)
                .Where(note => item.Chord.OnChordBassPitchClasses.Contains(note % 12))
                .Select(note => (byte)note)
                .ToArray();
            onChordCandidates = KeepSingableCandidates(onChordCandidates, previous, maximumLeap);
            if (onChordCandidates.Length > 0)
            {
                return onChordCandidates
                    .OrderBy(note => Math.Abs(note - registerCenter) * 0.18
                        + (previous is byte prior ? Math.Abs(note - prior) * 0.85 : 0)
                        + (note % 12 == item.Chord.BassFoundationPitchClass ? -6.0 : -0.25))
                    .First();
            }

            // A chord-tone filter can legitimately have no singable candidate
            // when the preceding segment ends far away from the current
            // on-chord spelling. Do not let that musical continuity edge case
            // throw at a segment boundary; widen only the leap constraint while
            // keeping the same register and chord-tone priorities.
            var unconstrainedOnChord = Enumerable.Range(MinimumNote, onChordMaximum - MinimumNote + 1)
                .Where(note => item.Chord.OnChordBassPitchClasses.Contains(note % 12))
                .Select(note => (byte)note)
                .ToArray();
            if (unconstrainedOnChord.Length > 0)
            {
                return unconstrainedOnChord
                    .OrderBy(note => Math.Abs(note - registerCenter) * 0.18
                        + (previous is byte prior ? Math.Abs(note - prior) * 0.85 : 0)
                        + (note % 12 == item.Chord.BassFoundationPitchClass ? -6.0 : -0.25))
                    .First();
            }

            return BassHarmonicMotion.ChooseOpeningFoundation(
                item.Chord,
                MinimumNote,
                Math.Max(MinimumNote, registerMaximum));
        }

        var targetPitchClasses = item.IsChordOnset
            ? new HashSet<int> { item.Chord.BassRoot % 12 }
            : item.ApproachesNextHarmony
                ? BassHarmonicMotion.ConnectionPitchClasses(item.Chord, item.NextHarmony).ToHashSet()
                : InteriorChordPitchClasses(item.Chord, item.PreferFifth);
        if (item.Chord.IsOnChord && item.ApproachesNextHarmony)
        {
            var allowedApproach = BassHarmonicMotion.ConnectionPitchClasses(item.Chord, item.NextHarmony)
                .Intersect(item.Chord.OnChordBassPitchClasses)
                .ToHashSet();
            if (allowedApproach.Count > 0)
            {
                targetPitchClasses = allowedApproach;
            }
        }
        var maximum = registerMaximum;
        var candidates = Enumerable.Range(MinimumNote, maximum - MinimumNote + 1)
            .Where(note => targetPitchClasses.Contains(note % 12))
            .Select(note => (byte)note)
            .ToArray();
        candidates = KeepSingableCandidates(candidates, previous, maximumLeap);

        if (candidates.Length == 0)
        {
            return BassHarmonicMotion.ChooseOpeningFoundation(
                item.Chord,
                MinimumNote,
                registerMaximum);
        }

        return candidates
            .OrderBy(note => Score(note, item, previous, registerCenter))
            .First();
    }

    private static int StageMaximum(
        WaltzChorusStage stage,
        WaltzBassFeel feel,
        PhraseFunction function = PhraseFunction.Ground)
    {
        var center = RegisterCenter(stage, feel, function);
        return feel switch
        {
            WaltzBassFeel.PreWalkOne => Math.Clamp(center + 10, 47, 50),
            WaltzBassFeel.PreWalkTwo => Math.Clamp(center + 11, 49, 52),
            // The lifted stage expands the available range only at a phrase
            // accent. Ordinary walking returns to the central register; an
            // explicit octave pattern is granted MaximumNote by the caller.
            WaltzBassFeel.WalkThree => function switch
            {
                PhraseFunction.Build => MaximumNote,
                PhraseFunction.Setup => 53,
                PhraseFunction.Answer => 50,
                PhraseFunction.Comment => 49,
                PhraseFunction.Space => 46,
                PhraseFunction.Release => 47,
                _ => 47
            },
            _ => MaximumNote
        };
    }

    private static IReadOnlyList<int> DownbeatBassPitchClasses(ChordSpec chord)
    {
        if (chord.IsOnChord)
        {
            return chord.OnChordBassPitchClasses.Select(Mod12).Distinct().ToArray();
        }

        var result = new List<int>();
        AddPitchClass(result, chord.BassFoundationPitchClass);
        AddPitchClass(result, BassPitchVocabulary.FifthPitchClass(chord));
        AddPitchClass(result, BassPitchVocabulary.ThirdPitchClass(chord));
        return result;
    }

    private static void AddSettledRepeatedHarmonyPattern(
        IDictionary<(int Bar, int Beat), WaltzPatternStep> result,
        ChordSpec chord,
        int startBar,
        int endBarExclusive,
        int seed,
        int chorusBarOffset)
    {
        var root = chord.BassFoundationPitchClass;
        var third = FindChordTone(chord, root, 3, 4);
        var fifth = Mod12(chord.BassFifth);
        var selector = DeterministicNoise.Unit(seed, chorusBarOffset + startBar, 6227);
        // Settled repeated harmony stays root-led: choose 1-5-8-5 or
        // 1-3-5-(8)1 across four bars, with a compact 1-5 cell for two bars.
        var useRootFifthOctave = selector < 0.52;
        var length = endBarExclusive - startBar;

        for (var index = 0; index < length; index++)
        {
            var position = index % 4;
            var step = length == 2
                ? new WaltzPatternStep(index == 0 ? root : fifth, index == 0 ? 0 : 1)
                : useRootFifthOctave
                    ? position switch
                    {
                        0 => new WaltzPatternStep(root, 0),
                        1 => new WaltzPatternStep(fifth, 1),
                        2 => new WaltzPatternStep(root, 1, 1),
                        _ => new WaltzPatternStep(fifth, -1)
                    }
                    : position switch
                    {
                        0 => new WaltzPatternStep(root, 0),
                        1 => new WaltzPatternStep(third, 1),
                        2 => new WaltzPatternStep(fifth, 1),
                        _ => new WaltzPatternStep(root, 1, 1)
                    };
            result[(startBar + index, 0)] = step with { UseInPreWalkOne = true };
        }
    }

    private static double Score(byte note, BassEvent item, byte? previous, int center)
    {
        var score = Math.Abs(note - center) * 0.18;
        if (previous is byte prior)
        {
            var leap = Math.Abs(note - prior);
            score += leap * 0.85;
            if (leap > 7) score += (leap - 7) * 2.2;
            if (note == prior && item.BeatInBar != 0) score += item.IsPassing ? 7.0 : 4.5;
        }

        var rootPc = item.Chord.BassFoundationPitchClass;
        var fifthPc = item.Chord.BassFifth % 12;
        if (note % 12 == rootPc) score -= 1.2;
        if (note % 12 == fifthPc) score -= 0.9;
        if (item.IsChordOnset && note % 12 == rootPc) score -= 4.0;
        if (item.BeatInBar == 0 && note % 12 == rootPc) score -= 2.2;
        if (item.PreferFifth && note % 12 == fifthPc) score -= 1.5;

        if (item.ApproachesNextHarmony)
        {
            score += BassHarmonicMotion.FunctionalApproachCost(
                note % 12,
                item.Chord,
                item.NextHarmony);
        }

        return score;
    }

    private static int ApplyWalkingRegisterCeiling(
        int registerMaximum,
        int registerCenter,
        IReadOnlyList<byte>? recentNotes,
        IReadOnlyList<byte> generated)
    {
        var history = (recentNotes ?? Array.Empty<byte>())
            .Concat(generated)
            .TakeLast(HistoryLength)
            .ToArray();
        if (history.Length == 0)
        {
            return registerMaximum;
        }

        var highRun = history.Reverse()
            .TakeWhile(note => note >= registerCenter + 5)
            .Count();
        var ascendingRun = history.Length >= 3 &&
            history[^1] > history[^2] && history[^2] > history[^3]
            ? 3
            : 0;
        return highRun >= 2 || ascendingRun >= 3
            ? Math.Min(registerMaximum, registerCenter + 7)
            : registerMaximum;
    }

    private static byte[] KeepSingableCandidates(
        IEnumerable<byte> source,
        byte? previous,
        int maximumLeap)
    {
        var candidates = source.Distinct().ToArray();
        if (previous is not byte prior)
        {
            return candidates;
        }

        var nearby = candidates
            .Where(candidate => Math.Abs(candidate - prior) <= maximumLeap)
            .ToArray();
        return nearby.Length > 0 ? nearby : candidates;
    }

    private static HashSet<int> InteriorChordPitchClasses(ChordSpec chord, bool preferFifth)
    {
        if (chord.IsOnChord)
        {
            return chord.OnChordBassPitchClasses.Select(Mod12).ToHashSet();
        }

        var root = chord.BassFoundationPitchClass;
        var result = new HashSet<int> { root };
        // A seventh may be present in BassPitchClasses, but using it as a
        // regular interior target produces the audible 1-7-1 shape.  Keep
        // the waltz pulse to the stable triad, with a passing/approach tone
        // handled separately.
        result.Add(FindChordTone(chord, root, 3, 4));
        result.Add(chord.BassFifth % 12);
        if (preferFifth)
        {
            result.Add(chord.BassFifth % 12);
        }
        return result;
    }

    private static bool SameHarmony(ChordSpec first, ChordSpec second)
    {
        if (first.IsNoChord || second.IsNoChord)
        {
            return first.IsNoChord && second.IsNoChord;
        }

        // Piano colour changes such as G7 -> G7#11 do not require a new
        // walking foundation. Keep repeated-harmony cells continuous while
        // preserving actual slash-bass changes.
        return BassHarmonicMotion.SameHarmony(first, second) ||
            first.RootPitchClass == second.RootPitchClass &&
            first.BassFoundationPitchClass == second.BassFoundationPitchClass &&
            first.IsOnChord == second.IsOnChord;
    }

    private static int Mod12(int value) => ((value % 12) + 12) % 12;

    private static void AddPitchClass(ICollection<int> pitchClasses, int? pitchClass)
    {
        if (pitchClass is int value && !pitchClasses.Contains(Mod12(value)))
        {
            pitchClasses.Add(Mod12(value));
        }
    }

    private static int FindChordTone(ChordSpec chord, int root, params int[] intervals)
    {
        foreach (var interval in intervals)
        {
            var candidate = Mod12(root + interval);
            if (chord.BassPitchClasses.Any(pitch => Mod12(pitch) == candidate))
            {
                return candidate;
            }
        }

        return Mod12(root + intervals[0]);
    }

    private readonly record struct BassEvent(
        long Tick,
        int BarIndex,
        int BeatInBar,
        ChordSpec Chord,
        ChordSpec NextHarmony,
        bool IsChordOnset,
        bool ApproachesNextHarmony,
        bool PreferFifth,
        bool IsPassing,
        long? HoldUntilTick,
        WaltzBassFeel Feel,
        int? PatternPitchClass = null,
        int PatternDirection = 0,
        int PatternOctaveShift = 0);

    private readonly record struct WaltzPatternStep(
        int? PitchClass,
        int Direction,
        int OctaveShift = 0,
        bool UseInPreWalkOne = false);
}
