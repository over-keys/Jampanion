using Jampanion.Core.Music;

namespace Jampanion.Core.Playback;

public readonly record struct MidiProgramAssignment(byte Channel, byte Program);

public static class MidiOutputInitializationPlan
{
    public const string MicrosoftGsWavetableSynthName = "Microsoft GS Wavetable Synth";

    public static bool AppliesToMicrosoftGsWavetableSynth(string? outputPortName) =>
        !string.IsNullOrWhiteSpace(outputPortName) &&
        outputPortName.Contains(MicrosoftGsWavetableSynthName, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<MidiProgramAssignment> GeneralMidi { get; } =
    [
        // DryWetMIDI channels and program numbers are zero-based.
        // Ch.1 = Vibraphone (GM 12), Ch.2 = Acoustic Bass (GM 33),
        // Ch.3 = Acoustic Grand Piano (GM 1).
        new MidiProgramAssignment(SessionConstants.VibraphoneChannel, 11),
        new MidiProgramAssignment(SessionConstants.BassChannel, 32),
        new MidiProgramAssignment(SessionConstants.PianoChannel, 0)
    ];
}
