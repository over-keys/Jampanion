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
        IEnumerable<int>? alternativePitchClasses = null)
    {
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
            .Where(note => pitchClasses.Contains(note % 12) && Math.Abs(note - prior) <= 12)
            .Select(note => (byte)note)
            .ToArray();
        if (selectedLeap > 12 || candidates.Length == 0)
        {
            // The written pitch class may have no nearby occurrence in a
            // restricted early-stage register. In that case, keep the line in
            // the acoustic range and enforce the hard one-octave ceiling with
            // any available bass pitch rather than allowing an unsafe jump.
            candidates = Enumerable.Range(minimum, maximum - minimum + 1)
                .Where(note => Math.Abs(note - prior) <= 12)
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
            .OrderBy(note => TransitionCost(note, prior))
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

    private static double TransitionCost(byte current, byte previous)
    {
        var leap = Math.Abs(current - previous);
        return leap switch
        {
            <= 5 => leap * 0.80,
            <= 7 => 4.0 + (leap - 5) * 0.95,
            <= 12 => 5.9 + (leap - 7) * 1.45,
            _ => 1000.0 + leap * 10.0
        };
    }

    private static int Mod12(int value) => (value % 12 + 12) % 12;
}
