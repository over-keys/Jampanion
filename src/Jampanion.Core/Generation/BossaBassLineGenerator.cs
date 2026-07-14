using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BossaBassLineGenerator
{
    private const int MinimumNote = 31;
    private const int MaximumNote = 55;
    private const int HistoryLength = 8;

    public static BassGenerationResult Generate(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        byte? previousNote,
        IReadOnlyList<byte>? recentNotes,
        int previousDirection,
        int previousDirectionRun,
        int seed,
        BossaChorusStage stage,
        PerformanceGuidance? performanceGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);
        if (bars.Count != arrangements.Count) throw new ArgumentException("Bars and arrangements must have the same length.");
        if (bars.Count == 0) throw new ArgumentException("At least one bar is required.", nameof(bars));

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var events = BuildEvents(bars, followingChord, arrangements, stage, seed);
        var notes = new List<ScheduledNote>(events.Count);
        var generated = new List<byte>(events.Count);
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var lastNote = previousNote;

        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            var selectedPitchClasses = item.UseFifth
                ? AllowedBassPitchClasses(item.Chord)
                : new[] { item.Chord.BassFoundationPitchClass };
            var note = BassLineConstraints.Constrain(
                SelectChordNote(item.Chord, item.UseFifth, lastNote),
                lastNote,
                BassLineConstraints.MinimumAcousticNote,
                BassLineConstraints.MaximumAcousticNote,
                40,
                selectedPitchClasses);
            var nextTick = index + 1 < events.Count ? events[index + 1].Tick : segmentLength;
            var lead = 1 + (long)Math.Round(DeterministicNoise.Unit(seed, index, 2701) * 2);
            var start = Math.Clamp(item.Tick - lead, 0, segmentLength - 1);
            var duration = item.IsSparseHold
                ? Math.Clamp(nextTick - item.Tick - 8, 120, SessionConstants.Ppq * 2)
                : Math.Clamp(nextTick - item.Tick - 55, item.IsPickup ? 130 : 280, item.IsPickup ? 230 : 900);
            duration = Math.Min(duration, segmentLength - start);
            var barIndex = (int)(item.Tick / SessionConstants.BarTicks);
            var arrangement = arrangements[Math.Min(barIndex, arrangements.Count - 1)];
            var chorusLift = stage == BossaChorusStage.Lifted ? 1 : stage is BossaChorusStage.Opening or BossaChorusStage.HeadOut ? -1 : 0;
            var lift = chorusLift + (guidance.HighStage ? 2 : 0);
            var phrase = arrangement.Function switch
            {
                PhraseFunction.Build => 2,
                PhraseFunction.Space => -2,
                PhraseFunction.Release => -1,
                _ => 0
            };
            var velocity = (byte)Math.Clamp((item.IsPickup ? 59 : item.UseFifth ? 68 : 73) + lift + phrase, 50, 84);
            notes.Add(new ScheduledNote(start, duration, note, velocity, SessionConstants.BassChannel));
            generated.Add(note);
            lastNote = note;
        }

        var history = (recentNotes ?? Array.Empty<byte>()).Concat(generated).TakeLast(HistoryLength).ToArray();
        var lastDirection = generated.Count >= 2
            ? Math.Sign(generated[^1] - generated[^2])
            : previousDirection;
        var directionRun = generated.Count >= 2 && lastDirection != 0
            ? lastDirection == previousDirection ? Math.Min(previousDirectionRun + 1, 4) : 1
            : previousDirectionRun;

        return new BassGenerationResult(notes, generated[^1], history, lastDirection, directionRun);
    }

    private static List<BassEvent> BuildEvents(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        BossaChorusStage stage,
        int seed)
    {
        // The theme can alternate the full pulse with a sparse beat-1/beat-3
        // figure. From the first solo onward, 1-&2-3-&4 is a density floor:
        // phrase-space decisions must not remove the bass groove.
        var events = new List<BassEvent>(bars.Count * 6);
        var sparseParity = (int)(DeterministicNoise.Unit(seed, 2703) * 2) % 2;

        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var barStart = (long)barIndex * SessionConstants.BarTicks;
            var openingChord = bar.GetChordAtBeat(0);
            var secondHalfChord = bar.GetChordAtBeat(2);
            var nextChord = barIndex + 1 < bars.Count ? bars[barIndex + 1].Chord : followingChord;
            var secondHalfStartsNewHarmony = bar.ChordChanges.Any(change => change.StartBeat is > 0 and <= 2);
            var useSparseQuarterPulse = stage is BossaChorusStage.Opening or BossaChorusStage.HeadOut &&
                (barIndex + sparseParity) % 2 == 0;

            events.Add(new BassEvent(
                barStart,
                openingChord,
                UseFifth: false,
                IsPickup: false,
                IsSparseHold: useSparseQuarterPulse));

            // Preserve uncommon changes exactly where they occur.
            foreach (var change in bar.ChordChanges.Where(change => change.StartBeat is 1 or 3))
            {
                events.Add(new BassEvent(
                    barStart + change.StartBeat * SessionConstants.Ppq,
                    change.Chord,
                    UseFifth: false,
                    IsPickup: false,
                    IsSparseHold: useSparseQuarterPulse));
            }

            if (!useSparseQuarterPulse)
            {
                // &2 anticipates the beat-3 target.
                events.Add(new BassEvent(
                    barStart + 3L * SessionConstants.Ppq / 2,
                    secondHalfChord,
                    UseFifth: !secondHalfStartsNewHarmony,
                    IsPickup: true,
                    IsSparseHold: false));
            }

            events.Add(new BassEvent(
                barStart + 2L * SessionConstants.Ppq,
                secondHalfChord,
                UseFifth: !secondHalfStartsNewHarmony,
                IsPickup: false,
                IsSparseHold: useSparseQuarterPulse));

            if (!useSparseQuarterPulse)
            {
                // &4 anticipates the next root.
                events.Add(new BassEvent(
                    barStart + 7L * SessionConstants.Ppq / 2,
                    nextChord,
                    UseFifth: false,
                    IsPickup: true,
                    IsSparseHold: false));
            }
        }

        return events
            .GroupBy(item => item.Tick)
            .Select(group => group.OrderBy(item => item.IsPickup).First())
            .OrderBy(item => item.Tick)
            .ToList();
    }

    private static byte SelectChordNote(ChordSpec chord, bool useFifth, byte? previous)
    {
        if (chord.IsOnChord)
        {
            var onChordPitchClass = useFifth && chord.OnChordBassPitchClasses.Count > 1
                ? chord.OnChordBassPitchClasses[1]
                : chord.BassFoundationPitchClass;
            return FitPitchClass(onChordPitchClass, previous, preferLower: !useFifth);
        }

        var pitchClass = useFifth ? SelectSupportPitchClass(chord) : chord.BassRoot % 12;
        return FitPitchClass(pitchClass, previous, preferLower: !useFifth);
    }

    private static int SelectSupportPitchClass(ChordSpec chord)
    {
        if (chord.IsOnChord)
        {
            return chord.OnChordBassPitchClasses.Count > 1
                ? chord.OnChordBassPitchClasses[1]
                : chord.RootPitchClass;
        }

        var rootPitchClass = chord.BassRoot % 12;
        var chordTones = chord.BassPitchClasses
            .Where(pitchClass => pitchClass != rootPitchClass)
            .Distinct()
            .ToArray();
        if (chordTones.Length == 0)
        {
            return chord.BassFifth % 12;
        }

        return chordTones
            .OrderBy(pitchClass => Math.Abs(Mod12(pitchClass - rootPitchClass) - 7))
            .ThenBy(pitchClass => Mod12(pitchClass - rootPitchClass))
            .First();
    }

    private static int Mod12(int value) => (value % 12 + 12) % 12;

    private static IEnumerable<int> AllowedBassPitchClasses(ChordSpec chord) =>
        (chord.IsOnChord ? chord.OnChordBassPitchClasses : chord.BassPitchClasses
            .Append(chord.BassRoot)
            .Append(chord.BassFifth))
        .Select(Mod12)
        .Distinct();

    private static byte FitPitchClass(int pitchClass, byte? previous, bool preferLower)
    {
        var candidates = Enumerable.Range(MinimumNote, MaximumNote - MinimumNote + 1)
            .Where(note => note % 12 == pitchClass)
            .Select(note => (byte)note)
            .ToArray();
        if (previous is null)
        {
            return preferLower ? candidates[0] : candidates.OrderBy(note => Math.Abs(note - 40)).First();
        }

        return candidates
            .OrderBy(note => Math.Abs(note - previous.Value) + (note > 48 ? 2 : 0))
            .First();
    }

    private readonly record struct BassEvent(
        long Tick,
        ChordSpec Chord,
        bool UseFifth,
        bool IsPickup,
        bool IsSparseHold);
}
