namespace Jampanion.Live.Settings;

public sealed class AppSettings
{
    public string? InputPortName { get; set; }
    public string? OutputPortName { get; set; }
    public string TuneId { get; set; } = "autumn-leaves";
    public string? SongFileName { get; set; }
    public string? SongLibraryFolder { get; set; }
    public byte TestChannel { get; set; } = 1;
    public byte TestNote { get; set; } = 60;
    public byte TestVelocity { get; set; } = 90;
    public int TestDurationMilliseconds { get; set; } = 400;
    public int TempoBpm { get; set; } = 140;
    public bool FixedChorusPlanEnabled { get; set; } = true;
    public bool AdaptiveCompingEnabled { get; set; } = true;
    public bool DetectThemeReturnEnabled { get; set; } = true;
    public bool SendProgramChangesEnabled { get; set; } = true;
    public bool MidiThruToVibraphoneEnabled { get; set; }
    public int HeadOutSensitivity { get; set; } = 50;
}
