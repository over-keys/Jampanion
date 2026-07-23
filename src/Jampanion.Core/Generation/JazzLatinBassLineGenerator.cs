using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

/// <summary>
/// Jazz-Latin bass derived from the supplied reference performance.
///
/// The basic pulse is not the older "&2 plus beat 4 in every bar" salsa
/// tumbao.  It establishes each harmony on beat 1, answers on 2&, and uses
/// beat 4 only as an occasional phrase pickup.  This gives the drums and
/// piano room to sound like a jazz rhythm section while retaining a clear
/// straight-eighth Latin foundation.
/// </summary>
internal static class JazzLatinBassLineGenerator
{
    private const int MinimumNote = 29;
    private const int MaximumNote = 55;
    private const int HistoryLength = 8;
    private const int MaximumBassLeap = 12;

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
        if (bars.Count != arrangements.Count)
        {
            throw new ArgumentException("Bars and arrangements must have the same length.");
        }
        if (bars.Count == 0)
        {
            throw new ArgumentException("At least one bar is required.", nameof(bars));
        }

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var events = BuildEvents(bars, followingChord, arrangements, stage, seed)
            .Where(item => !item.Chord.IsNoChord)
            .OrderBy(item => item.Tick)
            .ToArray();
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var notes = new List<ScheduledNote>(events.Length);
        var generated = new List<byte>(events.Length);
        var lastNote = previousNote;

        // Precompute the actual humanized starts. Durations must be bounded
        // against these starts, not against the unshifted grid ticks.
        // Otherwise a pickup can overlap a same-pitch downbeat and its late
        // Note Off can silence the newly started note until the following 2&.
        var eventStarts = events
            .Select((item, index) =>
            {
                var lead = 1 + (long)Math.Round(
                    DeterministicNoise.Unit(seed, index, 8601) * 2);
                return Math.Clamp(item.Tick - lead, 0, segmentLength - 1);
            })
            .ToArray();

        for (var index = 0; index < events.Length; index++)
        {
            var item = events[index];
            var pitchClass = item.PitchClassOverride ??
                (item.Chord.IsOnChord
                    ? item.Chord.BassFoundationPitchClass
                    : item.Chord.BassRoot % 12);
            var note = FitPitchClass(
                pitchClass,
                lastNote,
                item.IsStrongArrival,
                MaximumBassLeap);

            var start = eventStarts[index];
            var nextStart = index + 1 < events.Length
                ? eventStarts[index + 1]
                : segmentLength;
            // Keep a tiny release gap before the next actual Note On.
            // This prevents same-pitch Note Off/Note On collisions at bar lines.
            var availableDuration = index + 1 < events.Length
                ? Math.Max(1, nextStart - start - 3)
                : Math.Max(1, segmentLength - start);
            var duration = Math.Min(item.PreferredDuration, availableDuration);
            duration = Math.Min(duration, segmentLength - start);

            var barIndex = Math.Min(
                (int)(item.Tick / SessionConstants.BarTicks),
                arrangements.Count - 1);
            var arrangement = arrangements[barIndex];
            var stageLift = stage switch
            {
                LatinChorusStage.Opening or LatinChorusStage.HeadOut => -2,
                LatinChorusStage.Ponchando => -1,
                LatinChorusStage.Mambo => 3,
                _ => 1
            };
            var phraseLift = arrangement.Function switch
            {
                PhraseFunction.Build => 2,
                PhraseFunction.Space => -2,
                PhraseFunction.Release => -1,
                _ => 0
            };
            var velocity = 75 + stageLift + phraseLift +
                arrangement.DynamicLift / 3 +
                (guidance.HighStage ? 2 : 0) +
                (item.IsStrongArrival ? 3 : item.IsPhrasePickup ? -1 : 0);

            notes.Add(new ScheduledNote(
                start,
                duration,
                note,
                (byte)Math.Clamp(velocity, 61, 91),
                SessionConstants.BassChannel));
            generated.Add(note);
            lastNote = note;
        }

        var history = (recentNotes ?? Array.Empty<byte>())
            .Concat(generated)
            .TakeLast(HistoryLength)
            .ToArray();
        var lastDirection = generated.Count >= 2
            ? Math.Sign(generated[^1] - generated[^2])
            : previousDirection;
        var directionRun = generated.Count >= 2 && lastDirection != 0
            ? lastDirection == previousDirection
                ? Math.Min(previousDirectionRun + 1, 4)
                : 1
            : previousDirectionRun;
        var lastNoteForContext = generated.Count > 0
            ? generated[^1]
            : previousNote ?? (byte)36;

        return new BassGenerationResult(
            notes,
            lastNoteForContext,
            history,
            lastDirection,
            directionRun);
    }

    private static IReadOnlyList<JazzLatinBassEvent> BuildEvents(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        LatinChorusStage stage,
        int seed)
    {
        var events = new List<JazzLatinBassEvent>(bars.Count * 3);
        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var arrangement = arrangements[barIndex];
            var barStart = (long)barIndex * SessionConstants.BarTicks;
            var openingChord = bar.GetChordAtBeat(0);
            var beatThreeChord = bar.GetChordAtBeat(2);
            var nextBarChord = barIndex + 1 < bars.Count
                ? bars[barIndex + 1].GetChordAtBeat(0)
                : followingChord;

            // The measured jazz-Latin reference places a low harmonic arrival on
            // beat 1 in essentially every bar.  Keep it even when the piano rests.
            events.Add(new JazzLatinBassEvent(
                barStart,
                openingChord,
                PitchClassOverride: null,
                IsStrongArrival: true,
                IsPhrasePickup: false,
                PreferredDuration: 5L * SessionConstants.Ppq / 4));

            // 2& is the recurring answer.  On a beat-three chord change it
            // anticipates the new root; otherwise it uses the fifth or another
            // stable structural tone.
            events.Add(new JazzLatinBassEvent(
                barStart + 3L * SessionConstants.Ppq / 2,
                beatThreeChord,
                SelectTwoAndPitchClass(openingChord, beatThreeChord),
                IsStrongArrival: false,
                IsPhrasePickup: false,
                PreferredDuration: 5L * SessionConstants.Ppq / 2));

            // Beat 4 is a phrase pickup, not a compulsory second tumbao note.
            // It becomes more likely at harmonic or formal joins and as the
            // arrangement rises, matching jazz phrasing rather than a fixed loop.
            if (ShouldAddBeatFour(
                    openingChord,
                    beatThreeChord,
                    nextBarChord,
                    arrangement,
                    stage,
                    seed,
                    barIndex))
            {
                events.Add(new JazzLatinBassEvent(
                    barStart + 3L * SessionConstants.Ppq,
                    nextBarChord,
                    PitchClassOverride: null,
                    IsStrongArrival: false,
                    IsPhrasePickup: true,
                    PreferredDuration: SessionConstants.Ppq));
            }
        }

        return events
            .GroupBy(item => item.Tick)
            .Select(group => group
                .OrderByDescending(item => item.IsStrongArrival)
                .ThenByDescending(item => item.IsPhrasePickup)
                .First())
            .OrderBy(item => item.Tick)
            .ToArray();
    }

    private static bool ShouldAddBeatFour(
        ChordSpec openingChord,
        ChordSpec beatThreeChord,
        ChordSpec nextBarChord,
        BarArrangement arrangement,
        LatinChorusStage stage,
        int seed,
        int barIndex)
    {
        var harmonicJoin =
            !string.Equals(
                beatThreeChord.Symbol,
                nextBarChord.Symbol,
                StringComparison.Ordinal) ||
            !string.Equals(
                openingChord.Symbol,
                beatThreeChord.Symbol,
                StringComparison.Ordinal);
        var structuralJoin =
            arrangement.IsSectionEnding ||
            arrangement.IsTransitionLeadIn ||
            arrangement.Function is PhraseFunction.Build or
                PhraseFunction.Setup or
                PhraseFunction.Release;

        if (harmonicJoin && structuralJoin)
        {
            return true;
        }

        var probability = stage switch
        {
            LatinChorusStage.Opening or LatinChorusStage.HeadOut => 0.12,
            LatinChorusStage.Ponchando => 0.20,
            LatinChorusStage.Montuno => 0.28,
            LatinChorusStage.Mambo => 0.42,
            _ => 0.20
        };
        if (harmonicJoin)
        {
            probability += 0.16;
        }
        if (structuralJoin)
        {
            probability += 0.14;
        }

        return DeterministicNoise.Unit(seed, barIndex, 8603) < probability;
    }

    private static int SelectTwoAndPitchClass(
        ChordSpec openingChord,
        ChordSpec beatThreeChord)
    {
        if (beatThreeChord.IsOnChord)
        {
            return beatThreeChord.BassFoundationPitchClass;
        }

        var rootPitchClass = Mod12(beatThreeChord.BassRoot);
        if (!string.Equals(
                openingChord.Symbol,
                beatThreeChord.Symbol,
                StringComparison.Ordinal))
        {
            return rootPitchClass;
        }

        var fifthPitchClass = Mod12(beatThreeChord.BassFifth);
        var structural = BassPitchVocabulary
            .StructuralChordPitchClasses(beatThreeChord)
            .Select(Mod12)
            .Distinct()
            .ToArray();
        if (fifthPitchClass != rootPitchClass &&
            structural.Contains(fifthPitchClass))
        {
            return fifthPitchClass;
        }

        return structural
            .Where(pitchClass => pitchClass != rootPitchClass)
            .OrderBy(pitchClass =>
                Math.Abs(Mod12(pitchClass - rootPitchClass) - 7))
            .ThenBy(pitchClass => Mod12(pitchClass - rootPitchClass))
            .DefaultIfEmpty(rootPitchClass)
            .First();
    }

    private static byte FitPitchClass(
        int pitchClass,
        byte? previous,
        bool strongArrival,
        int maximumLeap)
    {
        var normalizedPitchClass = Mod12(pitchClass);
        var candidates = Enumerable
            .Range(MinimumNote, MaximumNote - MinimumNote + 1)
            .Where(note => note % 12 == normalizedPitchClass)
            .Select(note => (byte)note)
            .ToArray();

        if (previous is null)
        {
            return candidates
                .OrderBy(note => Math.Abs(note - 40))
                .First();
        }

        var nearby = candidates
            .Where(note => Math.Abs(note - previous.Value) <= maximumLeap)
            .ToArray();
        if (nearby.Length > 0)
        {
            candidates = nearby;
        }

        return candidates
            .OrderBy(note =>
                Math.Abs(note - previous.Value) +
                (note > 48 ? 3 : 0) +
                (strongArrival && note > previous.Value + 7 ? 2 : 0))
            .First();
    }

    private static int Mod12(int value) =>
        (value % 12 + 12) % 12;

    private readonly record struct JazzLatinBassEvent(
        long Tick,
        ChordSpec Chord,
        int? PitchClassOverride,
        bool IsStrongArrival,
        bool IsPhrasePickup,
        long PreferredDuration);
}
