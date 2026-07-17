using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BalladBassLineGenerator
{
    private const int MinimumNote = 31;
    private const int MaximumNote = 55;
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
        var events = BuildEvents(bars, followingChord, arrangements, stages, seed);
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var notes = new List<ScheduledNote>(events.Count);
        var generated = new List<byte>(events.Count);
        var lastNote = previousNote;

        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            var next = index + 1 < events.Count ? events[index + 1] : null;
            var constrained = BassLineConstraints.Constrain(
                SelectNote(item, next, lastNote, seed, index),
                lastNote,
                BassLineConstraints.MinimumAcousticNote,
                RegisterMaximum(item.Stage),
                RegisterCenter(item.Stage),
                item.Beat == 0
                    ? DownbeatBassPitchClasses(item.Chord)
                    : item.StrongArrival
                    ? null
                    : AllowedBassPitchClasses(item.Chord)
                        .Concat(next is not null && next.StrongArrival && next.Tick - item.Tick <= SessionConstants.Ppq
                            ? ApproachPitchClasses(next.Chord)
                            : Array.Empty<int>()));
            var note = AvoidRepeatedTwoFeelNote(item, constrained, lastNote);
            if (item.Beat == 0)
            {
                // Keep this as a final invariant after register/voice-leading
                // correction too: a downbeat can never leak to a 7th or colour
                // tone when the selected root has no nearby octave.
                note = NearestDownbeatNote(note, lastNote, item.Chord, item.Stage);
            }
            var nextTick = next?.Tick ?? segmentLength;
            var lead = 1 + (long)Math.Round(DeterministicNoise.Unit(seed, index, 7101) * 2);
            var start = Math.Clamp(item.Tick - lead, 0, segmentLength - 1);
            var maximumDuration = item.Stage switch
            {
                BalladChorusStage.Theme or BalladChorusStage.HeadOut => 1_620,
                BalladChorusStage.QuietSolo => 1_320,
                BalladChorusStage.MovingTwoFeel => 980,
                _ => 620
            };
            var duration = item.RootOnlySplit
                ? Math.Max(250, nextTick - item.Tick - 34)
                : Math.Clamp(nextTick - item.Tick - 34, 250, maximumDuration);
            duration = Math.Min(duration, segmentLength - start);
            var barIndex = Math.Min((int)(item.Tick / SessionConstants.BarTicks), arrangements.Count - 1);
            var stageLift = item.Stage switch
            {
                BalladChorusStage.Theme or BalladChorusStage.HeadOut => -4,
                BalladChorusStage.QuietSolo => -2,
                BalladChorusStage.MovingTwoFeel => 0,
                BalladChorusStage.FourFeel => 2,
                _ => 0
            };
            var interactionLift = guidance.HighStage ? 2 : 0;
            var velocity = 65 + stageLift + interactionLift + arrangements[barIndex].DynamicLift / 3 + (item.StrongArrival ? 2 : -1);
            notes.Add(new ScheduledNote(
                start,
                duration,
                note,
                (byte)Math.Clamp(velocity, 52, 78),
                SessionConstants.BassChannel));
            generated.Add(note);
            lastNote = note;
        }

        var history = (recentNotes ?? Array.Empty<byte>()).Concat(generated).TakeLast(HistoryLength).ToArray();
        var lastDirection = generated.Count >= 2 ? Math.Sign(generated[^1] - generated[^2]) : previousDirection;
        var directionRun = generated.Count >= 2 && lastDirection != 0
            ? lastDirection == previousDirection ? Math.Min(previousDirectionRun + 1, 4) : 1
            : previousDirectionRun;
        return new BassGenerationResult(notes, generated[^1], history, lastDirection, directionRun);
    }

    private static byte AvoidRepeatedTwoFeelNote(
        BalladBassEvent item,
        byte selected,
        byte? previous)
    {
        if (item.Stage == BalladChorusStage.FourFeel ||
            previous is not byte prior ||
            selected != prior)
        {
            return selected;
        }

        // In two-feel, two consecutive attacks may not park on the same note,
        // especially across a barline (the forbidden | 1 2 | 2 x | family).
        // A downbeat keeps its written bass root in another octave. Interior
        // attacks first seek another 1/3/5/7 chord tone; colour tensions are not
        // admitted as an escape from the repetition.
        var pitchClasses = item.Beat == 0 || item.StrongArrival
            ? new[] { item.Chord.BassFoundationPitchClass }
            : BassPitchVocabulary.StructuralChordPitchClasses(item.Chord);
        var candidates = Enumerable.Range(MinimumNote, MaximumNote - MinimumNote + 1)
            .Where(note => note != prior && pitchClasses.Contains(note % 12))
            .Select(note => (byte)note)
            .ToArray();
        if (candidates.Length == 0)
        {
            return selected;
        }

        var selectedPitchClass = selected % 12;
        return candidates
            .OrderBy(note => item.Beat == 0 || item.StrongArrival
                ? note % 12 == selectedPitchClass ? 0 : 100
                : note % 12 == selectedPitchClass ? 1 : 0)
            .ThenBy(note => Math.Abs(note - prior) <= 7 ? 0 : 1)
            .ThenBy(note => Math.Abs(note - prior))
            .ThenBy(note => Math.Abs(note - RegisterCenter(item.Stage)))
            .First();
    }

    private static List<BalladBassEvent> BuildEvents(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<BalladChorusStage> stages,
        int seed)
    {
        var events = new List<BalladBassEvent>(bars.Count * 4);
        var twoFeelIdioms = BuildTwoFeelIdiomAssignments(bars, stages, seed);
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
                twoFeelIdioms.TryGetValue((barIndex, beat), out var twoFeelStep);
                var patternStep = walkingCell?[beat] ?? twoFeelStep;
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
                    rootOnlySplit));
            }
        }

        return events
            .GroupBy(item => item.Tick)
            .Select(group => group.OrderByDescending(item => item.StrongArrival).First())
            .OrderBy(item => item.Tick)
            .ToList();
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
        int eventIndex)
    {
        var registerCenter = RegisterCenter(item.Stage);
        var registerMaximum = RegisterMaximum(item.Stage);
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
                Math.Max(registerMaximum, 55));
        }

        if (item.RootOnlySplit)
        {
            return FitPitchClass(item.Chord.BassRoot % 12, previous, registerCenter, registerMaximum);
        }

        if (item.StrongArrival)
        {
            if (item.PatternPitchClass is null)
            {
                return FitPitchClass(item.Chord.BassRoot % 12, previous, registerCenter, registerMaximum);
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
                registerMaximum);
        }

        if (next is not null &&
            next.StrongArrival &&
            next.Tick - item.Tick <= SessionConstants.Ppq)
        {
            var target = next.Chord.BassRoot % 12;
            var approach = DeterministicNoise.Unit(seed, eventIndex, 7121) < 0.32
                ? (target + 11) % 12
                : (target + 1) % 12;
            return FitPitchClass(approach, previous, registerCenter, registerMaximum);
        }

        return FitPitchClass(
            SelectStructuralTone(item, previous, seed, eventIndex),
            previous,
            registerCenter,
            registerMaximum);
    }

    private static int RegisterCenter(BalladChorusStage stage) => stage switch
    {
        BalladChorusStage.Theme or BalladChorusStage.HeadOut => 36,
        BalladChorusStage.QuietSolo => 37,
        BalladChorusStage.MovingTwoFeel => 38,
        _ => 40
    };

    private static int RegisterMaximum(BalladChorusStage stage) => stage switch
    {
        BalladChorusStage.Theme or BalladChorusStage.HeadOut or BalladChorusStage.QuietSolo => 47,
        BalladChorusStage.MovingTwoFeel => 50,
        _ => MaximumNote
    };

    private static IEnumerable<int> AllowedBassPitchClasses(ChordSpec chord) =>
        BassPitchVocabulary.StructuralChordPitchClasses(chord);

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

    private static IEnumerable<int> ApproachPitchClasses(ChordSpec chord) =>
        BassPitchVocabulary.RootApproachPitchClasses(chord);

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
        var selector = DeterministicNoise.Unit(seed, barIndex, 7131);
        return selector switch
        {
            < 0.30 when seventh is int chordalSeventh =>
            [
                new(root, 0, -1), new(third, 1),
                new(fifth, 1), new(chordalSeventh, 1)
            ],
            < 0.70 =>
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
        int maximum)
    {
        var candidates = NotesForPitchClass(Mod12(pitchClass), maximum);
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

    private static byte FitPitchClass(int pitchClass, byte? previous, int center, int maximum)
    {
        var candidates = NotesForPitchClass(pitchClass, maximum);
        return previous is null
            ? candidates.OrderBy(note => Math.Abs(note - center)).First()
            : candidates.OrderBy(note =>
                Math.Abs(note - previous.Value) +
                Math.Abs(note - center) * 0.12 +
                (note == previous.Value ? 4.0 : 0)).First();
    }

    private static byte NearestDownbeatNote(
        byte selected,
        byte? previous,
        ChordSpec chord,
        BalladChorusStage stage)
    {
        var candidates = DownbeatBassPitchClasses(chord)
            .SelectMany(pitchClass => NotesForPitchClass(pitchClass, RegisterMaximum(stage)))
            .ToArray();
        if (candidates.Length == 0)
        {
            return selected;
        }

        var root = chord.BassFoundationPitchClass;
        var fifth = BassPitchVocabulary.FifthPitchClass(chord);
        return candidates
            .OrderBy(note => previous is byte prior
                ? Math.Abs(note - prior)
                : Math.Abs(note - RegisterCenter(stage)))
            .ThenBy(note => note % 12 == root ? 0 : note % 12 == fifth ? 1 : 2)
            .ThenBy(note => Math.Abs(note - selected))
            .First();
    }

    private static byte[] NotesForPitchClass(int pitchClass, int maximum = MaximumNote) =>
        Enumerable.Range(MinimumNote, maximum - MinimumNote + 1)
            .Where(note => note % 12 == pitchClass)
            .Select(note => (byte)note)
            .ToArray();

    private static bool SameHarmony(ChordSpec first, ChordSpec second) =>
        first.RootPitchClass == second.RootPitchClass &&
        string.Equals(first.Symbol, second.Symbol, StringComparison.Ordinal);

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
        bool RootOnlySplit);

    private sealed record WalkingCellStep(int PitchClass, int Direction, int RegisterAnchor = 0);
}
