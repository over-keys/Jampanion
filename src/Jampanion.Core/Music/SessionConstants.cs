namespace Jampanion.Core.Music;

public static class SessionConstants
{
    public const int Ppq = 480;

    // Legacy 4/4 constants remain for the existing Swing and Bossa engines.
    public const int BeatsPerBar = 4;
    public const int BarTicks = Ppq * BeatsPerBar;
    public const int MinimumSupportedBeatsPerBar = 3;
    public const int MaximumSupportedBeatsPerBar = 4;

    public const int CountInBars = 2;
    public const int ChorusBars = 32;
    public const int BarsPerSegment = 4;
    public const int SegmentsPerChorus = ChorusBars / BarsPerSegment;

    // DryWetMIDI channels are zero-based. Display channels are 1, 2, 3 and 10.
    public const byte VibraphoneChannel = 0;
    public const byte BassChannel = 1;
    public const byte PianoChannel = 2;
    public const byte DrumsChannel = 9;

    public static long GetBarTicks(int beatsPerBar)
    {
        if (beatsPerBar is < MinimumSupportedBeatsPerBar or > MaximumSupportedBeatsPerBar)
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerBar));
        }

        return (long)Ppq * beatsPerBar;
    }
}
