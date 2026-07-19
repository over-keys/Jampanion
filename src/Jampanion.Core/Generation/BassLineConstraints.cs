namespace Jampanion.Core.Generation;

internal static class BassLineConstraints
{
    public const int MinimumAcousticNote = 31;
    public const int MaximumAcousticNote = 55;

    public static byte Constrain(
        byte selected,
        byte? previous,
        int minimum = MinimumAcousticNote,
        int maximum = MaximumAcousticNote,
        int registerCenter = 40,
        IEnumerable<int>? alternativePitchClasses = null,
        int maximumLeap = 10)
    {
        maximumLeap = Math.Clamp(maximumLeap, 1, BassHarmonicMotion.AbsoluteMaximumLeap);
        selected = (byte)Math.Clamp(selected, minimum, maximum);
        if (previous is not byte prior)
        {
            return selected;
        }

        var selectedPitchClass = selected % 12;
        var alternatives = alternativePitchClasses?.Select(Mod12).Distinct().ToArray() ?? Array.Empty<int>();
        var selectedLeap = Math.Abs(selected - prior);
        var useAlternatives = alternatives.Length > 0 &&
            (selectedLeap > 7 || !alternatives.Contains(selectedPitchClass));
        var pitchClasses = useAlternatives
            ? alternatives.ToList()
            : new List<int> { selectedPitchClass };

        var candidates = Enumerable.Range(minimum, maximum - minimum + 1)
            .Where(note => pitchClasses.Contains(note % 12) && Math.Abs(note - prior) <= maximumLeap)
            .Select(note => (byte)note)
            .ToArray();
        if (selectedLeap > maximumLeap || candidates.Length == 0)
        {
            // Never escape a difficult connection by inventing an unrelated
            // pitch. A stage ceiling is a register target rather than a reason
            // to jump, so keep the upper acoustic bridge available when the
            // preferred range has no singable harmonic occurrence.
            var extendedMaximum = Math.Max(maximum, MaximumAcousticNote);
            candidates = Enumerable.Range(minimum, extendedMaximum - minimum + 1)
                .Where(note => pitchClasses.Contains(note % 12) &&
                    Math.Abs(note - prior) <= maximumLeap)
                .Select(note => (byte)note)
                .ToArray();
        }
        if (candidates.Length == 0)
        {
            return selected;
        }

        // Keep the written chord tone, but choose its nearest register. A
        // fifth-or-closer move is preferred; octave displacement is allowed
        // only when the acoustic range leaves no nearer occurrence.
        return candidates
            .OrderBy(note => BassHarmonicMotion.MelodicTransitionCost(prior, note))
            .ThenBy(note => note % 12 == selectedPitchClass ? 0 : 1)
            .ThenBy(note => Math.Abs(note - registerCenter))
            .First();
    }

    public static byte[] ConstrainSequence(
        IReadOnlyList<byte> selected,
        byte? previous,
        int minimum = MinimumAcousticNote,
        int maximum = MaximumAcousticNote,
        int registerCenter = 40)
    {
        var result = new byte[selected.Count];
        var last = previous;
        for (var index = 0; index < selected.Count; index++)
        {
            result[index] = Constrain(selected[index], last, minimum, maximum, registerCenter);
            last = result[index];
        }

        return result;
    }

    private static int Mod12(int value) => (value % 12 + 12) % 12;
}
