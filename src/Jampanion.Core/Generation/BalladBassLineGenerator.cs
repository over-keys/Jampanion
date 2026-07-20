using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BalladBassLineGenerator
{
    // E1 is the useful lower bridge for a slow two-feel.  Keeping it in the
    // candidate pool avoids a forced jump from the low B/C area to the next
    // occurrence of a written root at the bottom of the staff.
    private const int MinimumNote = 28;
    private const int MaximumNote = 55;
    private const int MaximumBassLeap = BassHarmonicMotion.AbsoluteMaximumLeap;
    private const int HistoryLength = 8;

    public static BassGenerationResult Generate(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<BalladChorusStage> stages,
        byte? previousNote,
        IReadOnlyList<byte>? recentNotes,
        int previousDirection,
        int previousDirectionRun,
        int seed,
        PerformanceGuidance? performanceGuidance = null,
        bool prepareNextFourFeel = false,
        TimeFeelProfile? timeFeel = null)
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
        var timing = timeFeel ?? TimeFeelProfile.Resolve(AccompanimentStyle.JazzBallad, 64);
        var events = BuildEvents(bars, followingChord, arrangements, stages, seed, prepareNextFourFeel);
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var notes = new List<ScheduledNote>(events.Count);
        var generated = new List<byte>(events.Count);
        var lastNote = previousNote;
        var previousWasFoundationOctave = false;

        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            var next = index + 1 < events.Count ? events[index + 1] : null;
            var barIndex = Math.Min((int)(item.Tick / SessionConstants.BarTicks), arrangements.Count - 1);
            var arrangement = arrangements[barIndex];
            var registerCenter = RegisterCenter(item.Stage, arrangement.Function);
            var registerMaximum = RegisterMaximum(item.Stage, arrangement.Function);
            if ((item.PatternRegisterAnchor > 0 &&
                 item.PatternPitchClass is int patternRoot &&
                 Mod12(patternRoot) == Mod12(item.Chord.BassFoundationPitchClass)) ||
                item.FoundationOctaveDirection > 0 ||
                previousWasFoundationOctave)
            {
                registerMaximum = Math.Min(
                    registerMaximum,
                    BassHarmonicMotion.LowOctaveUpperMaximum);
            }
            if (next is null && item.IsTransitionPickup)
            {
                // The following chorus is outside this segment, so provide the
                // next root to the pickup selector explicitly. This preserves
                // the same approach-note logic used for an in-segment change.
                next = new BalladBassEvent(
                    segmentLength,
                    followingChord,
                    item.Stage,
                    StrongArrival: true,
                    Beat: 0,
                    PatternPitchClass: null,
                    PatternDirection: 0,
                    PatternRegisterAnchor: 0,
                    RootOnlySplit: false);
            }
            // SelectNote owns the complete candidate policy.  There is no
            // second pass that replaces a chosen musical note after the fact.
            var note = SelectNote(
                item,
                next,
                lastNote,
                seed,
                index,
                registerCenter,
                registerMaximum,
                index == 0 && previousNote is null);
            var nextTick = next is null ? segmentLength : timing.MapGrid(next.Tick);
            var start = PlaceBassAttack(item.Tick, index, seed, timing, segmentLength);
            var nextAttackStart = next is null || next.Tick >= segmentLength
                ? segmentLength
                : PlaceBassAttack(next.Tick, index + 1, seed, timing, segmentLength);
            // A ballad bass should connect to the next attack instead of
            // releasing a third of a beat early.  The next-event clamp below
            // still protects written changes and pickups, so these ceilings
            // only remove the artificial gaps left by the old short gates.
            var maximumDuration = item.Stage switch
            {
                BalladChorusStage.Theme or BalladChorusStage.HeadOut => 1_900,
                BalladChorusStage.QuietSolo => 1_860,
                BalladChorusStage.MovingTwoFeel => 1_820,
                _ => 900
            };
            var mappedGridTick = timing.MapGrid(item.Tick);
            var duration = item.RootOnlySplit
                ? Math.Max(250, nextTick - mappedGridTick)
                : Math.Clamp(nextTick - mappedGridTick, 250, maximumDuration);
            duration = timing.ScaleGate(duration, TimeFeelRole.Bass);
            // The mapped grid is not necessarily the performed attack: the
            // bass lead and deterministic timing nudge move the next note
            // earlier.  Cap against that actual attack so a slow-ballad gate
            // cannot overlap the next NoteOn and be cut by its NoteOff.
            duration = Math.Min(duration, Math.Max(1, nextAttackStart - start));
            duration = Math.Min(duration, segmentLength - start);
            var stageLift = item.Stage switch
            {
                BalladChorusStage.Theme or BalladChorusStage.HeadOut => -4,
                BalladChorusStage.QuietSolo => -2,
                BalladChorusStage.MovingTwoFeel => 0,
                BalladChorusStage.FourFeel => 2,
                _ => 0
            };
            var interactionLift = guidance.HighStage ? 2 : 0;
            var velocity = 65 + stageLift + interactionLift + arrangements[barIndex].DynamicLift / 3 +
                (item.StrongArrival ? 2 : item.IsOffbeat ? -3 : -1);
            notes.Add(new ScheduledNote(
                start,
                duration,
                note,
                (byte)Math.Clamp(velocity, 52, 78),
                SessionConstants.BassChannel));
            generated.Add(note);
            previousWasFoundationOctave = item.PatternRegisterAnchor > 0 &&
                item.PatternPitchClass is int rootPitchClass &&
                Mod12(rootPitchClass) == Mod12(item.Chord.BassFoundationPitchClass) &&
                note > BassHarmonicMotion.LowOctaveUpperMaximum - 12;
            lastNote = note;
        }

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

    private static long PlaceBassAttack(
        long tick,
        int index,
        int seed,
        TimeFeelProfile timing,
        long segmentLength)
    {
        return Math.Clamp(
            timing.Place(tick, TimeFeelRole.Bass) +
                timing.MillisecondsToTicks((DeterministicNoise.Unit(seed, index, 7101) - 0.5) * 1.6),
            0,
            segmentLength - 1);
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

    private static List<BalladBassEvent> BuildEvents(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<BalladChorusStage> stages,
        int seed,
        bool prepareNextFourFeel)
    {
        var events = new List<BalladBassEvent>(bars.Count * 4);
        var twoFeelIdioms = BuildTwoFeelIdiomAssignments(bars, stages, seed);
        var octaveIdioms = BuildFoundationOctaveAssignments(
            bars,
            arrangements,
            stages,
            twoFeelIdioms,
            seed);
        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var stage = stages[barIndex];
            var barStart = (long)barIndex * SessionConstants.BarTicks;
            var chosenBeats = new HashSet<int> { 0 };
            var rootOnlySplit = IsOnePlusThreeSplit(bar) &&
                stage != BalladChorusStage.FourFeel;
            var nextBarChord = barIndex + 1 < bars.Count ? bars[barIndex + 1].Chord : followingChord;
            var repeatedHarmony = bar.ChordChanges.Count == 1 && SameHarmony(bar.Chord, nextBarChord);
            var walkingCell = repeatedHarmony && stage == BalladChorusStage.FourFeel
                ? SelectWalkingCell(bar.Chord, barIndex, seed)
                : null;
            var previousHarmony = barIndex == 0
                ? null
                : bars[barIndex - 1].GetChordAtBeat(bars[barIndex - 1].BeatsPerBar - 1);

            foreach (var change in bar.ChordChanges.Skip(1))
            {
                chosenBeats.Add(change.StartBeat);
            }

            if (!rootOnlySplit)
            {
                switch (stage)
                {
                    case BalladChorusStage.Theme:
                        if (arrangements[barIndex].Function != PhraseFunction.Space ||
                            DeterministicNoise.Unit(seed, barIndex, 7111) < 0.45)
                        {
                            chosenBeats.Add(2);
                        }
                        break;

                    case BalladChorusStage.HeadOut:
                        if (DeterministicNoise.Unit(seed, barIndex, 7113) < 0.58)
                        {
                            chosenBeats.Add(2);
                        }
                        break;

                    case BalladChorusStage.QuietSolo:
                        chosenBeats.Add(2);
                        break;

                    case BalladChorusStage.MovingTwoFeel:
                        chosenBeats.Add(2);
                        break;

                    case BalladChorusStage.FourFeel:
                        chosenBeats.UnionWith([0, 1, 2, 3]);
                        break;
                }
            }

            foreach (var beat in chosenBeats.Order())
            {
                var chord = bar.GetChordAtBeat(beat);
                if (chord.IsNoChord)
                {
                    continue;
                }
                twoFeelIdioms.TryGetValue((barIndex, beat), out var twoFeelStep);
                octaveIdioms.TryGetValue((barIndex, beat), out var octaveStep);
                // A written slash bass is a genuine pedal instruction. Its
                // octave figure takes precedence over an ordinary repeated-
                // harmony cell; otherwise the generic 1-3/5-8 vocabulary can
                // silently remove the pedal motion altogether.
                var patternStep = walkingCell?[beat] ?? octaveStep ?? twoFeelStep;
                var writtenChange = bar.ChordChanges.Any(change => change.StartBeat == beat && beat > 0);
                var strong = writtenChange ||
                    beat == 0 && (previousHarmony is null || !SameHarmony(previousHarmony, chord));
                events.Add(new BalladBassEvent(
                    barStart + beat * SessionConstants.Ppq,
                    chord,
                    stage,
                    strong,
                    beat,
                    patternStep?.PitchClass,
                    patternStep?.Direction ?? 0,
                    patternStep?.RegisterAnchor ?? 0,
                    rootOnlySplit,
                    FoundationOctaveDirection: patternStep?.FoundationOctaveDirection ?? 0));
            }

            AddRhythmicDecorations(
                events,
                bar,
                barIndex,
                barStart,
                stage,
                arrangements[barIndex],
                seed,
                rootOnlySplit,
                isLastBar: barIndex == bars.Count - 1);

            if (prepareNextFourFeel &&
                barIndex == bars.Count - 1 &&
                stage != BalladChorusStage.FourFeel &&
                !bar.GetChordAtBeat(3).IsNoChord &&
                !nextBarChord.IsNoChord)
            {
                // Keep the final two-feel bar intact and add only a late
                // pickup into the next chorus's walking downbeat.
                events.Add(new BalladBassEvent(
                    barStart + 3L * SessionConstants.Ppq + SessionConstants.Ppq * 2 / 3,
                    bar.GetChordAtBeat(3),
                    stage,
                    StrongArrival: false,
                    Beat: 3,
                    PatternPitchClass: null,
                    PatternDirection: 0,
                    PatternRegisterAnchor: 0,
                    RootOnlySplit: rootOnlySplit,
                    IsTransitionPickup: true));
            }
        }

        return events
            .GroupBy(item => item.Tick)
            .Select(group => group.OrderByDescending(item => item.StrongArrival).First())
            .OrderBy(item => item.Tick)
            .ToList();
    }

    private static IReadOnlyDictionary<(int Bar, int Beat), WalkingCellStep> BuildFoundationOctaveAssignments(
        IReadOnlyList<TuneBar> bars,
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<BalladChorusStage> stages,
        IReadOnlyDictionary<(int Bar, int Beat), WalkingCellStep> existingIdioms,
        int seed)
    {
        var result = new Dictionary<(int Bar, int Beat), WalkingCellStep>();
        for (var bar = 0; bar < bars.Count; bar++)
        {
            var chord = bars[bar].Chord;
            if (stages[bar] == BalladChorusStage.FourFeel ||
                bars[bar].ChordChanges.Count != 1 ||
                !chord.IsOnChord &&
                (existingIdioms.ContainsKey((bar, 0)) ||
                    existingIdioms.ContainsKey((bar, 2))))
            {
                continue;
            }

            var probability = chord.IsOnChord
                ? 0.30
                : arrangements[bar].Function switch
            {
                PhraseFunction.Build or PhraseFunction.Setup => 0.08,
                PhraseFunction.Space => 0.10,
                _ => 0.14
            };
            if (DeterministicNoise.Unit(seed, bar, 7161) >= probability)
            {
                continue;
            }

            var foundation = chord.BassFoundationPitchClass;
            result[(bar, 0)] = new WalkingCellStep(foundation, 0, 1);
            result[(bar, 2)] = new WalkingCellStep(
                foundation,
                -1,
                -1,
                FoundationOctaveDirection: -1);
        }
        return result;
    }

    private static void AddRhythmicDecorations(
        ICollection<BalladBassEvent> events,
        TuneBar bar,
        int barIndex,
        long barStart,
        BalladChorusStage stage,
        BarArrangement arrangement,
        int seed,
        bool rootOnlySplit,
        bool isLastBar)
    {
        if (rootOnlySplit)
        {
            return;
        }

        var activity = arrangement.Function switch
        {
            PhraseFunction.Build => 1.22,
            PhraseFunction.Setup => 1.15,
            PhraseFunction.Space => 0.55,
            PhraseFunction.Release => 0.72,
            _ => 1.0
        };
        if (stage == BalladChorusStage.FourFeel)
        {
            if (DeterministicNoise.Unit(seed, barIndex, 7171) < 0.11 * activity)
            {
                Add(3, offbeat: true);
            }
            return;
        }

        if (DeterministicNoise.Unit(seed, barIndex, 7173) < 0.16 * activity)
        {
            Add(1, offbeat: true);
        }
        if (DeterministicNoise.Unit(seed, barIndex, 7175) < 0.18 * activity)
        {
            Add(3, offbeat: false);
        }
        if (DeterministicNoise.Unit(seed, barIndex, 7177) < 0.22 * activity)
        {
            Add(3, offbeat: true);
        }

        void Add(int beat, bool offbeat)
        {
            var chord = bar.GetChordAtBeat(beat);
            if (chord.IsNoChord)
            {
                return;
            }

            events.Add(new BalladBassEvent(
                barStart + beat * SessionConstants.Ppq +
                    (offbeat ? SessionConstants.Ppq * 2 / 3 : 0),
                chord,
                stage,
                StrongArrival: false,
                Beat: beat,
                PatternPitchClass: null,
                PatternDirection: 0,
                PatternRegisterAnchor: 0,
                RootOnlySplit: false,
                IsTransitionPickup: isLastBar && beat == 3 && offbeat,
                IsOffbeat: offbeat));
        }
    }

    private static IReadOnlyDictionary<(int Bar, int Beat), WalkingCellStep> BuildTwoFeelIdiomAssignments(
        IReadOnlyList<TuneBar> bars,
        IReadOnlyList<BalladChorusStage> stages,
        int seed)
    {
        var result = new Dictionary<(int Bar, int Beat), WalkingCellStep>();
        for (var bar = 0; bar + 1 < bars.Count;)
        {
            var chord = bars[bar].GetChordAtBeat(0);
            var secondChord = bars[bar + 1].GetChordAtBeat(0);
            var third = BassPitchVocabulary.ThirdPitchClass(chord);
            var fifth = BassPitchVocabulary.FifthPitchClass(chord);
            if (stages[bar] == BalladChorusStage.FourFeel ||
                stages[bar + 1] == BalladChorusStage.FourFeel ||
                bars[bar].ChordChanges.Count != 1 ||
                bars[bar + 1].ChordChanges.Count != 1 ||
                !SameHarmony(chord, secondChord) ||
                third is null ||
                fifth is null)
            {
                bar++;
                continue;
            }

            var root = chord.BassFoundationPitchClass;
            var ascending = DeterministicNoise.Unit(seed, bar, 7151) < 0.5;
            var steps = ascending
                ? new[]
                {
                    new WalkingCellStep(root, 0, -1),
                    new WalkingCellStep(third.Value, 1),
                    new WalkingCellStep(fifth.Value, 1),
                    new WalkingCellStep(root, 1)
                }
                : new[]
                {
                    new WalkingCellStep(root, 0, 1),
                    new WalkingCellStep(fifth.Value, -1),
                    new WalkingCellStep(third.Value, -1),
                    new WalkingCellStep(root, -1)
                };
            var positions = new[] { (bar, 0), (bar, 2), (bar + 1, 0), (bar + 1, 2) };
            for (var index = 0; index < steps.Length; index++)
            {
                result[positions[index]] = steps[index];
            }

            bar += 2;
        }

        return result;
    }

    private static byte SelectNote(
        BalladBassEvent item,
        BalladBassEvent? next,
        byte? previous,
        int seed,
        int eventIndex,
        int registerCenter,
        int registerMaximum,
        bool isOpening)
    {
        if (isOpening && previous is null)
        {
            return BassHarmonicMotion.ChooseOpeningFoundation(
                item.Chord,
                MinimumNote,
                registerMaximum);
        }

        if (item.Chord.IsOnChord)
        {
            // Keep the root as the gravity point, but leave its upper octave
            // available when it gives a smoother connection from the previous
            // bass note.  Slash chords must not force the line into the lowest
            // possible register.
            var anchors = item.Chord.OnChordBassPitchClasses;
            var useFifth = !item.StrongArrival && anchors.Count > 1 &&
                DeterministicNoise.Unit(seed, eventIndex, 7141) < 0.38;
            return FitPitchClass(
                useFifth ? anchors[1] : item.Chord.BassFoundationPitchClass,
                previous,
                registerCenter,
                registerMaximum,
                MaximumBassLeap);
        }

        if (item.RootOnlySplit)
        {
            return FitPitchClass(item.Chord.BassRoot % 12, previous, registerCenter, registerMaximum, MaximumBassLeap);
        }

        if (item.StrongArrival)
        {
            if (item.PatternPitchClass is null)
            {
                return FitPitchClass(item.Chord.BassRoot % 12, previous, registerCenter, registerMaximum, MaximumBassLeap);
            }
        }

        if (item.PatternPitchClass is int patternPitchClass)
        {
            return FitPatternPitchClass(
                patternPitchClass,
                previous,
                item.PatternDirection,
                item.PatternRegisterAnchor,
                registerCenter,
                registerMaximum,
                MaximumBassLeap);
        }

        if (next is not null &&
            next.StrongArrival &&
            next.Tick - item.Tick <= SessionConstants.Ppq)
        {
            var allowChromatic = item.Stage == BalladChorusStage.FourFeel ||
                item.IsTransitionPickup ||
                item.IsOffbeat ||
                BassHarmonicMotion.FunctionalMotionStrength(item.Chord, next.Chord) > 0;
            var target = FitPitchClass(
                next.Chord.BassFoundationPitchClass,
                previous,
                registerCenter,
                registerMaximum,
                MaximumBassLeap);
            return BassHarmonicMotion.ChooseApproachNote(
                target,
                previous,
                item.Chord,
                next.Chord,
                MinimumNote,
                registerMaximum,
                registerCenter,
                allowChromatic);
        }

        return FitPitchClass(
            SelectStructuralTone(item, previous, seed, eventIndex),
            previous,
            registerCenter,
            registerMaximum,
            MaximumBassLeap);
    }

    private static int RegisterCenter(BalladChorusStage stage, PhraseFunction function)
    {
        var energy = stage switch
        {
            BalladChorusStage.Theme => 0.08,
            BalladChorusStage.QuietSolo => 0.24,
            BalladChorusStage.MovingTwoFeel => 0.56,
            BalladChorusStage.FourFeel => 0.88,
            BalladChorusStage.HeadOut => 0.12,
            _ => 0.30
        };
        return BassHarmonicMotion.ShapeRegisterCenter(36, 40, energy, function);
    }

    private static int RegisterMaximum(BalladChorusStage stage, PhraseFunction function) =>
        Math.Clamp(RegisterCenter(stage, function) + 12, 48, MaximumNote);

    private static int SelectStructuralTone(
        BalladBassEvent item,
        byte? previous,
        int seed,
        int eventIndex)
    {
        if (item.Chord.IsOnChord)
        {
            return item.Chord.OnChordBassPitchClasses.Count > 1
                ? item.Chord.OnChordBassPitchClasses[1]
                : item.Chord.BassFoundationPitchClass;
        }

        var root = item.Chord.BassRoot % 12;
        var fifth = ChordalFifthPitchClass(item.Chord);
        var third = ChordalThirdPitchClass(item.Chord);
        var seventh = ChordalSeventhPitchClass(item.Chord);
        var selector = DeterministicNoise.Unit(seed, eventIndex, 7127);

        // The slow two-feel retains the same root/fifth gravity as swing.  It may
        // be spacious, but an interior note is never chosen merely because a 7th
        // happens to be a semitone away from the preceding root.
        if (item.Stage is BalladChorusStage.Theme or BalladChorusStage.HeadOut or BalladChorusStage.QuietSolo)
        {
            return item.Beat switch
            {
                0 => root,
                2 when selector < 0.84 => fifth,
                2 when third is int chordalThird && selector < 0.96 => chordalThird,
                2 => root,
                _ => fifth
            };
        }

        if (item.Stage == BalladChorusStage.MovingTwoFeel)
        {
            return item.Beat switch
            {
                0 => root,
                1 when selector < 0.74 => fifth,
                1 when third is int chordalThird && selector < 0.94 => chordalThird,
                1 => root,
                2 when selector < 0.50 => root,
                2 when selector < 0.84 => fifth,
                2 when third is int chordalThird => chordalThird,
                3 when selector < 0.68 => fifth,
                3 when third is int chordalThird && selector < 0.92 => chordalThird,
                3 => root,
                _ => fifth
            };
        }

        // Four-feel uses the same walking vocabulary as swing:
        // root/fifth are the default anchors, thirds articulate quality, and a
        // seventh is an optional continuation of 1-3-5-7, never a root's default
        // nearest neighbour. Repeated harmony receives the explicit directional
        // cells above; this path covers changing harmony and interrupted cells.
        if (item.Beat == 0)
        {
            return root;
        }

        var previousPitchClass = previous is byte previousNote ? previousNote % 12 : -1;
        return item.Beat switch
        {
            1 when third is int chordalThird && selector < 0.48 => chordalThird,
            1 when selector < 0.94 => fifth,
            1 => root,
            2 when previousPitchClass == third && selector < 0.76 => fifth,
            2 when previousPitchClass == fifth && third is int chordalThird && selector < 0.72 => chordalThird,
            2 when selector < 0.52 => fifth,
            2 when third is int chordalThird && selector < 0.86 => chordalThird,
            2 => root,
            3 when previousPitchClass == fifth && seventh is int chordalSeventh && selector < 0.18 => chordalSeventh,
            3 when selector < 0.52 => root,
            3 when selector < 0.84 => fifth,
            3 when third is int chordalThird => chordalThird,
            _ => fifth
        };
    }

    private static WalkingCellStep[] SelectWalkingCell(ChordSpec chord, int barIndex, int seed)
    {
        if (chord.IsOnChord)
        {
            var onChordRoot = chord.BassFoundationPitchClass;
            return [new(onChordRoot, 0), new(onChordRoot, 0), new(onChordRoot, 0), new(onChordRoot, 0)];
        }

        var root = chord.BassRoot % 12;
        var third = ChordalThirdPitchClass(chord) ?? Mod12(chord.RootPitchClass + 2);
        var fifth = ChordalFifthPitchClass(chord);
        var seventh = BassPitchVocabulary.SeventhPitchClass(chord);
        var second = Mod12(root + 2);
        var selector = DeterministicNoise.Unit(seed, barIndex, 7131);
        return selector switch
        {
            < 0.26 when seventh is int chordalSeventh =>
            [
                new(root, 0, -1), new(third, 1),
                new(fifth, 1), new(chordalSeventh, 1)
            ],
            < 0.50 =>
            [
                new(root, 0, -1), new(second, 1),
                new(third, 1), new(fifth, 1)
            ],
            < 0.78 =>
            [
                new(root, 0, -1), new(third, 1), new(fifth, 1), new(root, 1)
            ],
            _ =>
            [
                new(root, 0, 1), new(fifth, -1), new(third, -1), new(root, -1)
            ]
        };
    }

    private static int? ChordalThirdPitchClass(ChordSpec chord) =>
        BassPitchVocabulary.ThirdPitchClass(chord);

    private static int ChordalFifthPitchClass(ChordSpec chord) =>
        BassPitchVocabulary.FifthPitchClass(chord) ?? Mod12(chord.BassFifth);

    private static int? ChordalSeventhPitchClass(ChordSpec chord) =>
        BassPitchVocabulary.SeventhPitchClass(chord);

    private static byte FitPatternPitchClass(
        int pitchClass,
        byte? previous,
        int direction,
        int registerAnchor,
        int center,
        int maximum,
        int maximumLeap)
    {
        var candidates = NotesForPitchClass(Mod12(pitchClass), maximum);
        if (previous is byte prior)
        {
            var nearby = candidates
                .Where(note => Math.Abs(note - prior) <= maximumLeap)
                .ToArray();
            if (nearby.Length > 0)
            {
                candidates = nearby;
            }
        }
        if (registerAnchor > 0)
        {
            return candidates.Max();
        }

        if (registerAnchor < 0)
        {
            return candidates.Min();
        }

        if (previous is null)
        {
            return candidates.OrderBy(note => Math.Abs(note - center)).First();
        }

        var directed = direction > 0
            ? candidates.Where(note => note > previous.Value).ToArray()
            : direction < 0
                ? candidates.Where(note => note < previous.Value).ToArray()
                : Array.Empty<byte>();
        return (directed.Length > 0 ? directed : candidates)
            .OrderBy(note => Math.Abs(note - previous.Value) + Math.Abs(note - center) * 0.08)
            .First();
    }

    private static int Mod12(int value) => (value % 12 + 12) % 12;

    private static void AddPitchClass(ICollection<int> pitchClasses, int? pitchClass)
    {
        if (pitchClass is int value && !pitchClasses.Contains(Mod12(value)))
        {
            pitchClasses.Add(Mod12(value));
        }
    }

    private static byte FitPitchClass(
        int pitchClass,
        byte? previous,
        int center,
        int maximum,
        int maximumLeap)
    {
        var candidates = NotesForPitchClass(pitchClass, maximum);
        if (previous is byte prior)
        {
            var nearby = candidates
                .Where(note => Math.Abs(note - prior) <= maximumLeap)
                .ToArray();
            if (nearby.Length > 0)
            {
                candidates = nearby;
            }
        }
        return previous is null
            ? candidates.OrderBy(note => Math.Abs(note - center)).First()
            : candidates.OrderBy(note =>
                Math.Abs(note - previous.Value) +
                Math.Abs(note - center) * 0.12 +
                (note == previous.Value ? 4.0 : 0)).First();
    }

    private static byte[] NotesForPitchClass(int pitchClass, int maximum = MaximumNote) =>
        Enumerable.Range(MinimumNote, maximum - MinimumNote + 1)
            .Where(note => note % 12 == pitchClass)
            .Select(note => (byte)note)
            .ToArray();

    private static bool SameHarmony(ChordSpec first, ChordSpec second) =>
        BassHarmonicMotion.SameHarmony(first, second);

    private static bool IsOnePlusThreeSplit(TuneBar bar) =>
        bar.BeatsPerBar == SessionConstants.BeatsPerBar &&
        bar.ChordChanges.Count == 2 &&
        bar.ChordChanges[1].StartBeat is 1 or 3;

    private sealed record BalladBassEvent(
        long Tick,
        ChordSpec Chord,
        BalladChorusStage Stage,
        bool StrongArrival,
        int Beat,
        int? PatternPitchClass,
        int PatternDirection,
        int PatternRegisterAnchor,
        bool RootOnlySplit,
        bool IsTransitionPickup = false,
        bool IsOffbeat = false,
        int FoundationOctaveDirection = 0);

    private sealed record WalkingCellStep(
        int PitchClass,
        int Direction,
        int RegisterAnchor = 0,
        int FoundationOctaveDirection = 0);
}
