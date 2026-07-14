namespace Jampanion.Core.Playback;

public readonly record struct FormPosition(
    int Chorus,
    int Bar,
    int Beat,
    int BarIndex,
    long TickWithinChorus);
