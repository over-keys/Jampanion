using Jampanion.Core.Music;

namespace Jampanion.Core.Generation;

// The accompaniment generators share the same harmonic grammar, but not the
// same repetition policy.  This helper supplies a small, deterministic palette
// of close-position forms so the rhythm engines can keep their own idioms.
internal enum PianoVoicingStyle
{
    Swing,
    Ballad,
    Waltz,
    Bossa,
    Latin
}

internal static class PianoVoicingVocabulary
{
    public static IReadOnlyList<byte> Choose(
        IReadOnlyList<int> sourcePitchClasses,
        IReadOnlyList<byte> previous,
        int voiceCount,
        int lower,
        int upper,
        double targetCenter,
        PianoVoicingStyle style,
        int rootPitchClass,
        int seed,
        int barIndex,
        int hitIndex)
    {
        var pitchClasses = sourcePitchClasses
            .Select(Mod12)
            .Distinct()
            .ToArray();
        if (pitchClasses.Length == 0 || voiceCount <= 0)
        {
            return Array.Empty<byte>();
        }

        voiceCount = Math.Min(voiceCount, pitchClasses.Length);
        var candidates = new List<byte[]>();
        foreach (var selection in Combinations(pitchClasses, voiceCount))
        {
            foreach (var order in Permutations(selection))
            {
                for (var start = lower; start <= Math.Min(upper, lower + 11); start++)
                {
                    var notes = BuildAscending(order, start);
                    if (notes.Length != voiceCount ||
                        notes[0] < lower ||
                        notes[^1] > upper ||
                        notes[^1] - notes[0] > MaximumSpan(style, voiceCount) ||
                        !HasPlayableSpacing(notes))
                    {
                        continue;
                    }

                    candidates.Add(notes);
                }
            }
        }

        if (candidates.Count == 0)
        {
            return Fallback(pitchClasses, voiceCount, lower, upper);
        }

        return candidates
            .DistinctBy(candidate => string.Join(',', candidate))
            .OrderBy(candidate => Score(
                candidate,
                previous,
                targetCenter,
                style,
                rootPitchClass,
                pitchClasses,
                seed,
                barIndex,
                hitIndex))
            .First();
    }

    private static byte[] BuildAscending(IReadOnlyList<int> order, int start)
    {
        var notes = new byte[order.Count];
        var previous = start - 1;
        for (var index = 0; index < order.Count; index++)
        {
            var note = previous + 1;
            while (Mod12(note) != order[index] && note <= 127)
            {
                note++;
            }

            if (note > 127)
            {
                return Array.Empty<byte>();
            }

            notes[index] = (byte)note;
            previous = note;
        }

        return notes;
    }

    private static IEnumerable<int[]> Combinations(IReadOnlyList<int> values, int count)
    {
        var current = new int[count];
        return Visit(0, 0);

        IEnumerable<int[]> Visit(int sourceIndex, int depth)
        {
            if (depth == count)
            {
                yield return current.ToArray();
                yield break;
            }

            var remaining = count - depth;
            for (var index = sourceIndex; index <= values.Count - remaining; index++)
            {
                current[depth] = values[index];
                foreach (var result in Visit(index + 1, depth + 1))
                {
                    yield return result;
                }
            }
        }
    }

    private static IEnumerable<int[]> Permutations(IReadOnlyList<int> values)
    {
        var current = new int[values.Count];
        var used = new bool[values.Count];
        return Visit(0);

        IEnumerable<int[]> Visit(int depth)
        {
            if (depth == values.Count)
            {
                yield return current.ToArray();
                yield break;
            }

            for (var index = 0; index < values.Count; index++)
            {
                if (used[index]) continue;
                used[index] = true;
                current[depth] = values[index];
                foreach (var result in Visit(depth + 1))
                {
                    yield return result;
                }

                used[index] = false;
            }
        }
    }

    private static bool HasPlayableSpacing(IReadOnlyList<byte> notes)
    {
        for (var index = 1; index < notes.Count; index++)
        {
            var interval = notes[index] - notes[index - 1];
            if (notes[index - 1] < 55 && interval < 3)
            {
                return false;
            }

            if (notes.Count >= 3 && interval > 12)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Fallback(
        IReadOnlyList<int> pitchClasses,
        int voiceCount,
        int lower,
        int upper)
    {
        var result = new List<byte>(voiceCount);
        foreach (var pitchClass in pitchClasses)
        {
            var note = Enumerable.Range(lower, Math.Max(0, upper - lower + 1))
                .FirstOrDefault(value => Mod12(value) == pitchClass &&
                    (result.Count == 0 || value > result[^1]));
            if (note >= lower && note <= upper)
            {
                result.Add((byte)note);
            }

            if (result.Count == voiceCount) break;
        }

        if (result.Count == voiceCount)
        {
            return result.ToArray();
        }

        // If the requested texture is impossible, keep the fallback itself
        // inside the same acoustic window. Never fabricate an out-of-range or
        // duplicate MIDI pitch for a later safety pass to repair.
        return pitchClasses
            .SelectMany(pitchClass => Enumerable.Range(lower, Math.Max(0, upper - lower + 1))
                .Where(note => Mod12(note) == pitchClass)
                .Select(note => (byte)note))
            .Distinct()
            .Order()
            .Take(voiceCount)
            .ToArray();
    }

    private static double Score(
        IReadOnlyList<byte> candidate,
        IReadOnlyList<byte> previous,
        double targetCenter,
        PianoVoicingStyle style,
        int rootPitchClass,
        IReadOnlyList<int> availablePitchClasses,
        int seed,
        int barIndex,
        int hitIndex)
    {
        var targetSpan = style switch
        {
            PianoVoicingStyle.Ballad => candidate.Count == 2 ? 8 : candidate.Count == 3 ? 13 : 16,
            PianoVoicingStyle.Waltz => candidate.Count == 3 ? 11 : 14,
            PianoVoicingStyle.Bossa => candidate.Count == 3 ? 10 : 13,
            PianoVoicingStyle.Latin => 10,
            _ => candidate.Count == 2 ? 8 : candidate.Count == 3 ? 13 : 15
        };
        var score = Math.Abs(candidate.Average(note => note) - targetCenter) * 0.23 +
            Math.Abs(candidate[^1] - candidate[0] - targetSpan) * 0.09;

        // Guide tones remain the harmonic anchor, but colour tones are no
        // longer mandatory.  In particular, a 6th/9th/13th should be allowed
        // to drop out when a closer, more singable top line is available.
        var availableGuides = availablePitchClasses.Count(pc => IsGuideTone(pc, rootPitchClass));
        var guideTarget = Math.Min(candidate.Count, Math.Min(2, availableGuides));
        var guideCount = candidate.Count(note => IsGuideTone(note, rootPitchClass));
        score += Math.Max(0, guideTarget - guideCount) * 0.52;

        // Natural extensions are optional colour.  Altered chord tones are not
        // penalized here because they can define the written harmony.
        var optionalColourCount = candidate.Count(note => IsOptionalColourTone(note, rootPitchClass));
        score += optionalColourCount * 0.16;

        if (previous.Count > 0)
        {
            if (candidate.Count == previous.Count)
            {
                // With equal voice counts, preserve the identity of each
                // ascending voice. Nearest-note set distance can hide crossed
                // or reassigned voices that sound less connected to a pianist.
                for (var voice = 0; voice < candidate.Count; voice++)
                {
                    score += Math.Abs(candidate[voice] - previous[voice]) * 0.24;
                }
            }
            else
            {
                var forward = candidate.Sum(note => previous.Min(prior => Math.Abs(note - prior)));
                var backward = previous.Sum(prior => candidate.Min(note => Math.Abs(note - prior)));
                score += (forward + backward) * 0.14;
            }

            var topMotion = Math.Abs(candidate[^1] - previous[^1]);
            score += topMotion switch
            {
                0 => SameTopPenalty(style),
                <= 2 => -0.28,
                <= 5 => 0,
                <= 7 => 0.32,
                _ => 0.32 + (topMotion - 7) * 0.95
            };

            if (candidate.SequenceEqual(previous))
            {
                score += ExactRepeatPenalty(style);
            }

            if (candidate[^1] % 12 == previous[^1] % 12)
            {
                score += SameTopPenalty(style) * 0.45;
            }

            // Let the top voice lead the decision: a small, melodic movement
            // should beat a tempting extension that would force a large leap.
            if (topMotion <= 2)
            {
                score -= 0.10;
            }
        }

        score += DeterministicNoise.Unit(
            seed,
            barIndex,
            hitIndex,
            candidate[0],
            candidate[^1],
            9091) * 0.12;
        return score;
    }

    private static double ExactRepeatPenalty(PianoVoicingStyle style) => style switch
    {
        PianoVoicingStyle.Bossa => 0.05,
        PianoVoicingStyle.Latin => 0.04,
        PianoVoicingStyle.Waltz => 0.25,
        PianoVoicingStyle.Ballad => 0.52,
        _ => 0.42
    };

    private static double SameTopPenalty(PianoVoicingStyle style) => style switch
    {
        PianoVoicingStyle.Bossa => 0.03,
        PianoVoicingStyle.Latin => 0.02,
        PianoVoicingStyle.Waltz => 0.10,
        // Preserve a singable top line without parking on the same pitch.
        // Stepwise alternatives are already preferred by Score; this stronger
        // penalty only breaks long plateaus when a musical alternative exists.
        PianoVoicingStyle.Ballad => 0.48,
        _ => 0.16
    };

    private static int MaximumSpan(PianoVoicingStyle style, int voiceCount) => style switch
    {
        PianoVoicingStyle.Ballad => voiceCount == 2 ? 14 : voiceCount == 3 ? 18 : 20,
        PianoVoicingStyle.Waltz => 18,
        PianoVoicingStyle.Bossa => 18,
        PianoVoicingStyle.Latin => 16,
        _ => voiceCount == 2 ? 14 : 20
    };

    private static bool IsGuideTone(int note, int rootPitchClass)
    {
        var interval = Mod12(note - rootPitchClass);
        return interval is 3 or 4 or 10 or 11;
    }

    private static bool IsOptionalColourTone(int note, int rootPitchClass)
    {
        var interval = Mod12(note - rootPitchClass);
        return interval is 2 or 9;
    }

    private static int Mod12(int value) => (value % 12 + 12) % 12;
}
