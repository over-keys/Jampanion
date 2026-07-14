using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class LatinBassLineGenerator
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
        LatinChorusStage stage,
        PerformanceGuidance? performanceGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);
        if (bars.Count != arrangements.Count) throw new ArgumentException("Bars and arrangements must have the same length.");
        if (bars.Count == 0) throw new ArgumentException("At least one bar is required.", nameof(bars));

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var events = BuildEvents(bars, followingChord, arrangements, stage, seed, startsChorus: previousNote is null);
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var notes = new List<ScheduledNote>(events.Count);
        var generated = new List<byte>(events.Count);
        var lastNote = previousNote;

        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            var pitchClass = item.Chord.IsOnChord
                ? item.PitchClassOverride ?? item.Chord.BassFoundationPitchClass
                : item.PitchClassOverride ?? item.Chord.BassRoot % 12;
            var note = BassLineConstraints.Constrain(
                FitPitchClass(pitchClass, lastNote, item.IsStrongArrival),
                lastNote,
                BassLineConstraints.MinimumAcousticNote,
                BassLineConstraints.MaximumAcousticNote,
                40,
                item.IsStrongArrival ? null : AllowedBassPitchClasses(item.Chord));
            var nextTick = index + 1 < events.Count ? events[index + 1].Tick : segmentLength;
            var lead = 1 + (long)Math.Round(DeterministicNoise.Unit(seed, index, 6101) * 2);
            var start = Math.Clamp(item.Tick - lead, 0, segmentLength - 1);
            var duration = item.IsBeatFourPonche
                ? item.Tick + 2L * SessionConstants.Ppq - start
                : item.IsSupportPickup
                    ? Math.Clamp(nextTick - item.Tick - 96, 120, 230)
                    : Math.Clamp(nextTick - item.Tick - 32, item.IsPickup ? 300 : 360, item.IsPickup ? 760 : 900);
            duration = Math.Min(duration, segmentLength - start);
            var barIndex = Math.Min((int)(item.Tick / SessionConstants.BarTicks), arrangements.Count - 1);
            var arrangement = arrangements[barIndex];
            var stageLift = stage switch
            {
                LatinChorusStage.Opening or LatinChorusStage.HeadOut => -2,
                LatinChorusStage.Ponchando => -1,
                LatinChorusStage.Mambo => 3,
                _ => 0
            };
            var interactionLift = guidance.HighStage ? 3 : 0;
            var velocity = 72 + stageLift + interactionLift + arrangement.DynamicLift / 2 +
                (item.IsSupportPickup ? -8 : item.IsPickup ? -2 : 2);
            notes.Add(new ScheduledNote(
                start,
                duration,
                note,
                (byte)Math.Clamp(velocity, 56, 88),
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

    private static List<LatinBassEvent> BuildEvents(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        LatinChorusStage stage,
        int seed,
        bool startsChorus)
    {
        var events = new List<LatinBassEvent>(bars.Count * 3);
        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var barStart = (long)barIndex * SessionConstants.BarTicks;
            var nextBarChord = barIndex + 1 < bars.Count ? bars[barIndex + 1].Chord : followingChord;

            // The first played bar must establish the floor after the count-in.
            // A tumbao may avoid its own downbeats once it is in motion, but an
            // unannounced opening &2 leaves the ensemble without a low anchor.
            if (barIndex == 0 && startsChorus)
            {
                events.Add(new LatinBassEvent(
                    barStart,
                    bar.Chord,
                    IsPickup: false,
                    IsStrongArrival: true,
                    PitchClassOverride: null,
                    IsSupportPickup: false,
                    IsBeatFourPonche: false));
            }

            // &2 anticipates the harmony arriving on beat 3. If that harmony is
            // unchanged it uses the fifth; if beat 3 brings a new chord, it
            // states that chord's root. Beat 4 anticipates the next bar's root.
            var beatThreeChord = bar.GetChordAtBeat(2);
            events.Add(new LatinBassEvent(
                barStart + 3L * SessionConstants.Ppq / 2,
                beatThreeChord,
                IsPickup: true,
                IsStrongArrival: false,
                PitchClassOverride: SelectTumbaoSupportPitchClass(bar.Chord, beatThreeChord),
                IsSupportPickup: false,
                IsBeatFourPonche: false));

            if (stage == LatinChorusStage.Mambo &&
                arrangements[barIndex].Function is PhraseFunction.Comment or PhraseFunction.Build or PhraseFunction.Setup &&
                DeterministicNoise.Unit(seed, barIndex, 6103) < 0.64)
            {
                events.Add(new LatinBassEvent(
                    barStart + 5L * SessionConstants.Ppq / 2,
                    nextBarChord,
                    IsPickup: true,
                    IsStrongArrival: false,
                    PitchClassOverride: SelectSupportPitchClass(nextBarChord),
                    IsSupportPickup: true,
                    IsBeatFourPonche: false));
            }

            events.Add(new LatinBassEvent(
                barStart + 3L * SessionConstants.Ppq,
                nextBarChord,
                IsPickup: true,
                IsStrongArrival: true,
                PitchClassOverride: null,
                IsSupportPickup: false,
                IsBeatFourPonche: true));
        }

        return events
            .GroupBy(item => item.Tick)
            .Select(group => group.OrderByDescending(item => item.IsStrongArrival).First())
            .OrderBy(item => item.Tick)
            .ToList();
    }

    private static byte FitPitchClass(int pitchClass, byte? previous, bool strongArrival)
    {
        var candidates = Enumerable.Range(MinimumNote, MaximumNote - MinimumNote + 1)
            .Where(note => note % 12 == pitchClass)
            .Select(note => (byte)note)
            .ToArray();
        if (previous is null)
        {
            return candidates.OrderBy(note => Math.Abs(note - 40)).First();
        }

        return candidates
            .OrderBy(note => Math.Abs(note - previous.Value) + (note > 48 ? 3 : 0) + (strongArrival && note > previous.Value + 7 ? 2 : 0))
            .First();
    }

    private static int SelectSupportPitchClass(ChordSpec chord)
    {
        if (chord.IsOnChord)
        {
            return chord.OnChordBassPitchClasses.Count > 1
                ? chord.OnChordBassPitchClasses[1]
                : chord.BassFoundationPitchClass;
        }

        var rootPitchClass = chord.BassRoot % 12;
        var candidates = chord.BassPitchClasses
            .Append(chord.BassFifth % 12)
            .Where(pitchClass => pitchClass != rootPitchClass)
            .Distinct()
            .ToArray();
        if (candidates.Length == 0)
        {
            return chord.BassFifth % 12;
        }

        return candidates
            .OrderBy(pitchClass => Math.Abs(Mod12(pitchClass - rootPitchClass) - 7))
            .ThenBy(pitchClass => Mod12(pitchClass - rootPitchClass))
            .First();
    }

    private static int SelectTumbaoSupportPitchClass(ChordSpec openingChord, ChordSpec beatThreeChord)
    {
        if (beatThreeChord.IsOnChord)
        {
            return beatThreeChord.OnChordBassPitchClasses.Count > 1
                ? beatThreeChord.OnChordBassPitchClasses[1]
                : beatThreeChord.BassFoundationPitchClass;
        }

        var rootPitchClass = beatThreeChord.BassRoot % 12;
        if (!string.Equals(openingChord.Symbol, beatThreeChord.Symbol, StringComparison.Ordinal))
        {
            return rootPitchClass;
        }

        var fifthPitchClass = beatThreeChord.BassFifth % 12;
        var chordTones = beatThreeChord.BassPitchClasses
            .Select(Mod12)
            .Distinct()
            .ToArray();
        if (fifthPitchClass != rootPitchClass && chordTones.Contains(fifthPitchClass))
        {
            return fifthPitchClass;
        }

        return chordTones
            .Where(pitchClass => pitchClass != rootPitchClass)
            .OrderBy(pitchClass => Math.Abs(Mod12(pitchClass - rootPitchClass) - 7))
            .ThenBy(pitchClass => Mod12(pitchClass - rootPitchClass))
            .DefaultIfEmpty(rootPitchClass)
            .First();
    }

    private static int Mod12(int value) => (value % 12 + 12) % 12;

    private static IEnumerable<int> AllowedBassPitchClasses(ChordSpec chord) =>
        (chord.IsOnChord ? chord.OnChordBassPitchClasses : chord.BassPitchClasses
            .Append(chord.BassRoot)
            .Append(chord.BassFifth))
        .Select(Mod12)
        .Distinct();

    private readonly record struct LatinBassEvent(
        long Tick,
        ChordSpec Chord,
        bool IsPickup,
        bool IsStrongArrival,
        int? PitchClassOverride,
        bool IsSupportPickup,
        bool IsBeatFourPonche);
}
