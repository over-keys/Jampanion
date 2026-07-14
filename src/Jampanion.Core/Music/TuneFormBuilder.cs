namespace Jampanion.Core.Music;

internal static class TuneFormBuilder
{
    public static TuneBar Bar(int index, string section, ChordSpec chord) =>
        new(index, section, chord);

    public static TuneBar Split(int index, string section, ChordSpec first, ChordSpec second) =>
        new(index, section, [new ChordChange(0, first), new ChordChange(2, second)]);

    public static string FourEightBarSections(int barIndex, string first, string second, string third, string fourth) =>
        barIndex switch
        {
            < 8 => first,
            < 16 => second,
            < 24 => third,
            _ => fourth
        };
}
