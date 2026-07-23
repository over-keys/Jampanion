using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class BossaBassLineGenerator
{
    private const int MinimumNote = 29;
    private const int MaximumNote = 55;
    private const int HistoryLength = 8;
    private const int MaximumBassLeap = 10;

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
        var events = BuildEvents(bars);
        var notes = new List<ScheduledNote>(events.Count);
        var generated = new List<byte>(events.Count);
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var lastNote = previousNote;

        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            if (item.Chord.IsNoChord)
            {
                continue;
            }

            var note = SelectChordNote(item.Chord, item.UseFifth, lastNote, MaximumBassLeap);
            var nextTick = index + 1 < events.Count ? events[index + 1].Tick : segmentLength;
            var lead = 1 + (long)Math.Round(DeterministicNoise.Unit(seed, index, 2701) * 2);
            var start = Math.Clamp(item.Tick - lead, 0, segmentLength - 1);

            // Blue Bossa 2 uses the complete Brazilian four-note pulse in every
            // chorus: long 1, short 2&, long 3, short 4&. Keep the written
            // durations instead of deriving a generic legato value from density.
            var referenceDuration = item.IsPickup ? 225L : 705L;
            var duration = Math.Min(
                referenceDuration,
                Math.Max(1, nextTick - start - 4));
            duration = Math.Min(duration, segmentLength - start);

            var barIndex = Math.Min((int)(item.Tick / SessionConstants.BarTicks), arrangements.Count - 1);
            var arrangement = arrangements[barIndex];
            var chorusLift = stage == BossaChorusStage.Lifted
                ? 1
                : stage is BossaChorusStage.Opening or BossaChorusStage.HeadOut ? -1 : 0;
            var lift = chorusLift + (guidance.HighStage ? 2 : 0);
            var phrase = arrangement.Function switch
            {
                PhraseFunction.Build => 2,
                PhraseFunction.Space => -2,
                PhraseFunction.Release => -1,
                _ => 0
            };

            // The reference bass does not make the two pickups disappear beneath
            // the downbeats. They are slightly lighter, but remain a clear part of
            // the groove at every stage.
            var velocity = (byte)Math.Clamp(
                (item.IsPickup ? 65 : item.UseFifth ? 69 : 72) + lift + phrase,
                52,
                84);
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
        var lastNoteForContext = generated.Count > 0
            ? generated[^1]
            : previousNote ?? (byte)36;

        return new BassGenerationResult(notes, lastNoteForContext, history, lastDirection, directionRun);
    }

    private static List<BassEvent> BuildEvents(IReadOnlyList<TuneBar> bars)
    {
        // Measured Blue Bossa 2 pattern: 1, 2&, 3, 4& in every bar.
        // 2& and 3 normally use the fifth. When a new harmony arrives on beat 3,
        // 2& anticipates its root and the root remains through the second half.
        // 4& reiterates the current second-half foundation; unlike Afro-Cuban
        // tumbao it does not automatically anticipate the next bar's root.
        var events = new List<BassEvent>(bars.Count * 6);

        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var barStart = (long)barIndex * SessionConstants.BarTicks;
            var openingChord = bar.GetChordAtBeat(0);
            var secondHalfChord = bar.GetChordAtBeat(2);
            var finalQuarterChord = bar.GetChordAtBeat(3);
            var secondHalfStartsNewHarmony = bar.ChordChanges.Any(change => change.StartBeat is > 0 and <= 2);
            var finalQuarterStartsNewHarmony = bar.ChordChanges.Any(change => change.StartBeat == 3);

            events.Add(new BassEvent(
                barStart,
                openingChord,
                UseFifth: false,
                IsPickup: false));

            // Preserve uncommon written arrivals on beats 2 and 4.
            foreach (var change in bar.ChordChanges.Where(change => change.StartBeat is 1 or 3))
            {
                events.Add(new BassEvent(
                    barStart + change.StartBeat * SessionConstants.Ppq,
                    change.Chord,
                    UseFifth: false,
                    IsPickup: false));
            }

            events.Add(new BassEvent(
                barStart + 3L * SessionConstants.Ppq / 2,
                secondHalfChord,
                UseFifth: !secondHalfStartsNewHarmony,
                IsPickup: true));

            events.Add(new BassEvent(
                barStart + 2L * SessionConstants.Ppq,
                secondHalfChord,
                UseFifth: !secondHalfStartsNewHarmony,
                IsPickup: false));

            events.Add(new BassEvent(
                barStart + 7L * SessionConstants.Ppq / 2,
                finalQuarterChord,
                UseFifth: finalQuarterStartsNewHarmony || !secondHalfStartsNewHarmony,
                IsPickup: true));
        }

        return events
            .GroupBy(item => item.Tick)
            .Select(group => group.OrderBy(item => item.IsPickup).First())
            .OrderBy(item => item.Tick)
            .ToList();
    }

    private static byte SelectChordNote(
        ChordSpec chord,
        bool useFifth,
        byte? previous,
        int maximumLeap)
    {
        if (chord.IsOnChord)
        {
            var onChordPitchClass = useFifth && chord.OnChordBassPitchClasses.Count > 1
                ? chord.OnChordBassPitchClasses[1]
                : chord.BassFoundationPitchClass;
            return FitPitchClass(onChordPitchClass, previous, preferLower: !useFifth, maximumLeap);
        }

        var pitchClass = useFifth ? SelectSupportPitchClass(chord) : chord.BassRoot % 12;
        return FitPitchClass(pitchClass, previous, preferLower: !useFifth, maximumLeap);
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
        var chordTones = BassPitchVocabulary.StructuralChordPitchClasses(chord)
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

    private static byte FitPitchClass(int pitchClass, byte? previous, bool preferLower, int maximumLeap)
    {
        var candidates = Enumerable.Range(MinimumNote, MaximumNote - MinimumNote + 1)
            .Where(note => note % 12 == pitchClass)
            .Select(note => (byte)note)
            .ToArray();
        if (previous is null)
        {
            return preferLower ? candidates[0] : candidates.OrderBy(note => Math.Abs(note - 40)).First();
        }

        var nearby = candidates
            .Where(note => Math.Abs(note - previous.Value) <= maximumLeap)
            .ToArray();
        if (nearby.Length > 0)
        {
            candidates = nearby;
        }

        return candidates
            .OrderBy(note => Math.Abs(note - previous.Value) + (note > 48 ? 2 : 0))
            .First();
    }

    private readonly record struct BassEvent(
        long Tick,
        ChordSpec Chord,
        bool UseFifth,
        bool IsPickup);
}
