namespace Jampanion.Core.Generation;

/// <summary>
/// Shared two-feel cells used by swing and ballad.  The style generators only
/// decide how often to invite a cell; the actual vocabulary stays consistent.
/// </summary>
internal static class BassTwoFeelVocabulary
{
    public static BassTwoFeelCell Select(
        double density,
        int seed,
        int barIndex,
        int selectorSalt = 2182,
        int densitySalt = 2181)
    {
        var clampedDensity = Math.Clamp(density, 0d, 1d);
        if (DeterministicNoise.Unit(seed, barIndex, densitySalt) >= clampedDensity)
        {
            return default;
        }

        return DeterministicNoise.Unit(seed, barIndex, selectorSalt) switch
        {
            // | 1 2& 3 |
            < 0.18 => new BassTwoFeelCell(AddTwoAnd: true, AddFour: false, AddFourAnd: false),
            // | 1 3 4& |
            < 0.34 => new BassTwoFeelCell(AddTwoAnd: false, AddFour: false, AddFourAnd: true),
            // | 1 3 4 |
            < 0.52 => new BassTwoFeelCell(AddTwoAnd: false, AddFour: true, AddFourAnd: false),
            // | 1 2& 3 4 |
            < 0.74 => new BassTwoFeelCell(AddTwoAnd: true, AddFour: true, AddFourAnd: false),
            // | 1 2& 3 4& |
            _ => new BassTwoFeelCell(AddTwoAnd: true, AddFour: false, AddFourAnd: true)
        };
    }
}

internal readonly record struct BassTwoFeelCell(
    bool AddTwoAnd,
    bool AddFour,
    bool AddFourAnd);
