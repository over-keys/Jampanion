using Jampanion.Core.Music;

namespace Jampanion.Core.Generation;

/// <summary>
/// Shared musical priorities for swing, ballad, and jazz-waltz bass lines.
/// The normal vocabulary is deliberately limited to stable chord tones; a
/// chromatic or scalar neighbour is admitted only when it clearly resolves
/// into the following harmony.
/// </summary>
internal static class BassHarmonicMotion
{
    public const int PreferredMaximumLeap = 7;
    public const int AbsoluteMaximumLeap = 9;
    public const int MaximumAcousticNote = 55;
    // An octave-up foundation is a low-register colour, not a licence to
    // move the whole bass line into the upper staff.  Keep the top of that
    // colour fixed so every generator shares the same acoustic boundary.
    public const int LowOctaveUpperMaximum = 47;

    public static byte ChooseOpeningFoundation(
        ChordSpec chord,
        int minimum,
        int maximum,
        int preferredCenter = 36)
    {
        var candidates = NotesForPitchClasses(
            [chord.BassFoundationPitchClass],
            minimum,
            maximum);
        if (candidates.Length == 0)
        {
            return chord.BassRoot;
        }

        // With no preceding note, phrase continuity cannot disambiguate the
        // octave. Start the first theme from the lower acoustic home register;
        // ties choose the lower occurrence. Later notes use normal voice leading.
        return candidates
            .OrderBy(note => Math.Abs(note - preferredCenter))
            .ThenBy(note => note)
            .First();
    }

    public static byte ChooseApproachNote(
        byte selected,
        byte? previous,
        ChordSpec currentChord,
        ChordSpec nextChord,
        int minimum,
        int maximum,
        int registerCenter,
        bool allowChromatic)
    {
        var pitchClasses = ConnectionPitchClasses(currentChord, nextChord, allowChromatic);
        var candidates = NotesForPitchClasses(pitchClasses, minimum, maximum);
        if (candidates.Length == 0)
        {
            return selected;
        }

        if (previous is byte prior)
        {
            var nearby = candidates
                .Where(note => Math.Abs(note - prior) <= PreferredMaximumLeap)
                .ToArray();
            if (nearby.Length == 0)
            {
                nearby = candidates
                    .Where(note => Math.Abs(note - prior) <= AbsoluteMaximumLeap)
                    .ToArray();
            }
            if (nearby.Length == 0 && maximum < MaximumAcousticNote)
            {
                nearby = NotesForPitchClasses(
                        pitchClasses,
                        minimum,
                        MaximumAcousticNote)
                    .Where(note => Math.Abs(note - prior) <= AbsoluteMaximumLeap)
                    .ToArray();
            }
            if (nearby.Length > 0)
            {
                candidates = nearby;
            }
        }

        return candidates
            .OrderBy(note =>
                (previous is byte priorNote ? MelodicTransitionCost(priorNote, note) : 0.0) +
                FunctionalApproachCost(note % 12, currentChord, nextChord) +
                Math.Abs(note - registerCenter) * 0.08 +
                (note == selected ? -0.08 : 0.0))
            .First();
    }

    public static IReadOnlyList<int> ConnectionPitchClasses(
        ChordSpec currentChord,
        ChordSpec nextChord,
        bool allowChromatic = true)
    {
        var target = Mod12(nextChord.BassFoundationPitchClass);
        var structural = BassPitchVocabulary.StructuralChordPitchClasses(currentChord)
            .Select(Mod12)
            .Distinct()
            .OrderBy(pitchClass => CircularDistance(pitchClass, target))
            .ToArray();
        var result = new List<int>();

        // A chord tone which already lies a step from the next foundation is
        // the most harmonically integrated connector.
        foreach (var pitchClass in structural.Where(pitchClass =>
                     CircularDistance(pitchClass, target) <= 2))
        {
            AddPitchClass(result, pitchClass);
        }

        if (allowChromatic)
        {
            // In a circle progression (ii-V, V-I, and related dominants), the
            // lower leading tone has a particularly clear forward direction.
            if (FunctionalMotionStrength(currentChord, nextChord) >= 2)
            {
                AddPitchClass(result, target - 1);
                AddPitchClass(result, target + 1);
            }
            else
            {
                AddPitchClass(result, target + 1);
                AddPitchClass(result, target - 1);
            }

            AddPitchClass(result, target - 2);
            AddPitchClass(result, target + 2);
        }

        foreach (var pitchClass in structural)
        {
            AddPitchClass(result, pitchClass);
        }

        return result;
    }

    public static double FunctionalApproachCost(
        int pitchClass,
        ChordSpec currentChord,
        ChordSpec nextChord)
    {
        pitchClass = Mod12(pitchClass);
        var target = Mod12(nextChord.BassFoundationPitchClass);
        var distance = CircularDistance(pitchClass, target);
        var cost = distance switch
        {
            1 => -2.20,
            2 => -0.85,
            0 => 0.25,
            3 => 0.60,
            _ => 1.25 + (distance - 3) * 0.35
        };

        var structural = BassPitchVocabulary.StructuralChordPitchClasses(currentChord)
            .Any(value => Mod12(value) == pitchClass);
        if (structural)
        {
            cost -= 0.32;
        }

        var strength = FunctionalMotionStrength(currentChord, nextChord);
        if (strength > 0 && distance == 1)
        {
            cost -= 0.35 * strength;
        }
        if (strength >= 2 && pitchClass == Mod12(target - 1))
        {
            cost -= 0.28;
        }

        return cost;
    }

    public static int FunctionalMotionStrength(ChordSpec currentChord, ChordSpec nextChord)
    {
        if (SameHarmony(currentChord, nextChord))
        {
            return 0;
        }

        var rootMotion = Mod12(nextChord.RootPitchClass - currentChord.RootPitchClass);
        var dominant = IsDominant(currentChord);
        if (dominant && rootMotion is 5 or 11)
        {
            // V-I and tritone-substitute dominant resolution.
            return 2;
        }

        // Descending-fifth motion includes ii-V, secondary-dominant chains,
        // and the common jazz turnaround cycle.
        return rootMotion == 5 ? 1 : 0;
    }

    public static int ShapeRegisterCenter(
        int lowCenter,
        int highCenter,
        double stageEnergy,
        PhraseFunction function)
    {
        // Energy opens the upper register but must not transpose the whole
        // chorus upward. Keep the long-term centre nearly fixed and express
        // intensity through short phrase-function accents. Release and space
        // then bring the line back to its home register.
        var functionAccent = function switch
        {
            PhraseFunction.Build => 2.4,
            PhraseFunction.Setup => 1.7,
            PhraseFunction.Answer => 0.8,
            PhraseFunction.Comment => 0.5,
            PhraseFunction.Space => -0.8,
            PhraseFunction.Release => -0.5,
            _ => 0.0
        };
        var energy = Math.Clamp(stageEnergy, 0.0, 1.0);
        var persistentLift = (highCenter - lowCenter) * energy * 0.18;
        return (int)Math.Round(lowCenter + persistentLift + functionAccent);
    }

    public static double MelodicTransitionCost(int previous, int current)
    {
        var leap = Math.Abs(current - previous);
        return leap switch
        {
            0 => 0.72,
            <= 2 => leap * 0.10,
            <= 4 => 0.30 + (leap - 2) * 0.36,
            <= 5 => 1.45,
            <= PreferredMaximumLeap => 2.20 + (leap - 5) * 0.90,
            <= AbsoluteMaximumLeap => 5.00 + (leap - PreferredMaximumLeap) * 2.10,
            _ => 1000.0 + leap * 20.0
        };
    }

    public static bool SameHarmony(ChordSpec first, ChordSpec second) =>
        first.RootPitchClass == second.RootPitchClass &&
        Mod12(first.BassFoundationPitchClass) == Mod12(second.BassFoundationPitchClass) &&
        string.Equals(first.Symbol, second.Symbol, StringComparison.OrdinalIgnoreCase);

    private static bool IsDominant(ChordSpec chord)
    {
        var symbol = chord.Symbol.ToLowerInvariant();
        var third = BassPitchVocabulary.ThirdPitchClass(chord);
        var seventh = BassPitchVocabulary.SeventhPitchClass(chord);
        var hasMajorThird = third is int thirdPitchClass &&
            Mod12(thirdPitchClass - chord.RootPitchClass) == 4;
        var hasMinorSeventh = seventh is int seventhPitchClass &&
            Mod12(seventhPitchClass - chord.RootPitchClass) == 10;
        var suspendedDominant = symbol.Contains("sus", StringComparison.Ordinal) &&
            hasMinorSeventh;
        return hasMinorSeventh && hasMajorThird ||
            suspendedDominant ||
            symbol.Contains("alt", StringComparison.Ordinal);
    }

    private static byte[] NotesForPitchClasses(
        IEnumerable<int> pitchClasses,
        int minimum,
        int maximum)
    {
        var set = pitchClasses.Select(Mod12).ToHashSet();
        return Enumerable.Range(minimum, maximum - minimum + 1)
            .Where(note => set.Contains(note % 12))
            .Select(note => (byte)note)
            .ToArray();
    }

    private static int CircularDistance(int first, int second)
    {
        var distance = Math.Abs(Mod12(first) - Mod12(second));
        return Math.Min(distance, 12 - distance);
    }

    private static void AddPitchClass(ICollection<int> pitchClasses, int? pitchClass)
    {
        if (pitchClass is int value && !pitchClasses.Contains(Mod12(value)))
        {
            pitchClasses.Add(Mod12(value));
        }
    }

    private static int Mod12(int value) => (value % 12 + 12) % 12;
}
