using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Melanchall.DryWetMidi.Core;

namespace Jampanion.Live.Audio;

/// <summary>
/// Built-in output backed by the redistributable FluidSynth library. On
/// Windows FluidSynth renders into ASIO (with WinMM as a fallback); on macOS
/// the same renderer is fed to the CoreAudio AudioQueue output.
/// </summary>
internal sealed class FluidSynthOutputDevice : IDisposable
{
    private const int SampleRate = 48_000;
    private const int RenderBufferFrames = 2_048;
    private const int DrumsChannel = 9;

    private readonly ConcurrentQueue<SynthCommand> _commands = new();
    private IAudioOutput _audioOutput;
    private readonly short[] _left = new short[RenderBufferFrames];
    private readonly short[] _right = new short[RenderBufferFrames];
    private readonly GCHandle _leftHandle;
    private readonly GCHandle _rightHandle;
    private readonly IntPtr _settings;
    private readonly IntPtr _synth;
    private readonly int _soundFontId;
    private bool _started;
    private bool _disposed;

    private FluidSynthOutputDevice(string soundFontPath)
    {
        _leftHandle = GCHandle.Alloc(_left, GCHandleType.Pinned);
        _rightHandle = GCHandle.Alloc(_right, GCHandleType.Pinned);

        try
        {
            _settings = Native.new_fluid_settings();
            if (_settings == IntPtr.Zero)
            {
                throw new InvalidOperationException("FluidSynth could not create its settings object.");
            }

            SetSetting(Native.fluid_settings_setnum(_settings, "synth.sample-rate", SampleRate), "sample rate");
            SetSetting(Native.fluid_settings_setnum(_settings, "synth.gain", 0.72), "synth gain");
            SetSetting(Native.fluid_settings_setint(_settings, "synth.polyphony", 128), "polyphony");
            SetSetting(Native.fluid_settings_setint(_settings, "synth.reverb.active", 1), "reverb");
            SetSetting(Native.fluid_settings_setint(_settings, "synth.chorus.active", 1), "chorus");

            _synth = Native.new_fluid_synth(_settings);
            if (_synth == IntPtr.Zero)
            {
                throw new InvalidOperationException("FluidSynth could not create its synthesizer.");
            }

            _soundFontId = Native.fluid_synth_sfload(_synth, soundFontPath, 1);
            if (_soundFontId < 0)
            {
                throw new InvalidOperationException($"FluidSynth could not load the SoundFont: {soundFontPath}");
            }

            // General MIDI defaults used by Jampanion.  ProgramChange events
            // sent later by the MIDI service are still passed through.
            SelectProgram(channel: 0, bank: 0, program: 11); // Vibraphone / MIDI thru
            SelectProgram(channel: 1, bank: 0, program: 32); // Acoustic bass
            SelectProgram(channel: 2, bank: 0, program: 0);  // Acoustic grand piano
            SelectProgram(DrumsChannel, bank: 128, program: 0); // Standard kit

            _audioOutput = CreateAudioOutput();
        }
        catch
        {
            if (_synth != IntPtr.Zero)
            {
                Native.delete_fluid_synth(_synth);
            }

            if (_settings != IntPtr.Zero)
            {
                Native.delete_fluid_settings(_settings);
            }

            _leftHandle.Free();
            _rightHandle.Free();
            throw;
        }
    }

    internal static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return false;
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
        {
            return File.Exists(Path.Combine(baseDirectory, "libfluidsynth-3.dll")) &&
                   File.Exists(Path.Combine(baseDirectory, "sndfile.dll")) &&
                   File.Exists(Path.Combine(baseDirectory, "SDL3.dll")) &&
                   File.Exists(GetSoundFontPath(baseDirectory));
        }

        return File.Exists(Path.Combine(baseDirectory, "libfluidsynth.3.dylib")) &&
               File.Exists(GetSoundFontPath(baseDirectory));
    }

    internal static FluidSynthOutputDevice CreateFromApplicationAssets()
    {
        if (!IsAvailable())
        {
            throw new InvalidOperationException(
                "The embedded FluidSynth assets are not available in the application directory.");
        }

        return new FluidSynthOutputDevice(GetSoundFontPath(AppContext.BaseDirectory));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        try
        {
            _audioOutput.Start();
        }
        catch when (_audioOutput is AsioAudioOutput)
        {
            // An installed ASIO driver can still reject the requested sample
            // rate or fail to start after another application claimed it.
            // Keep the built-in output usable through the WinMM fallback.
            _audioOutput.Dispose();
            _audioOutput = new WinMmAudioOutput(RenderPcm16);
            _audioOutput.Start();
        }
        _started = true;
    }

    public void Enqueue(MidiEvent midiEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        switch (midiEvent)
        {
            case NoteOnEvent noteOn when noteOn.Velocity == 0:
                _commands.Enqueue(SynthCommand.NoteOff((byte)noteOn.Channel, (byte)noteOn.NoteNumber));
                break;

            case NoteOnEvent noteOn:
                _commands.Enqueue(SynthCommand.NoteOn(
                    (byte)noteOn.Channel,
                    (byte)noteOn.NoteNumber,
                    (byte)noteOn.Velocity));
                break;

            case NoteOffEvent noteOff:
                _commands.Enqueue(SynthCommand.NoteOff((byte)noteOff.Channel, (byte)noteOff.NoteNumber));
                break;

            case ControlChangeEvent control:
                _commands.Enqueue(SynthCommand.ControlChange(
                    (byte)control.Channel,
                    (byte)control.ControlNumber,
                    (byte)control.ControlValue));
                break;

            case ProgramChangeEvent program:
                _commands.Enqueue(SynthCommand.ProgramChange(
                    (byte)program.Channel,
                    (byte)program.ProgramNumber));
                break;
        }
    }

    internal void RenderForTest(short[] buffer) => RenderPcm16(buffer, buffer.Length);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _audioOutput.Dispose();
        }
        finally
        {
            while (_commands.TryDequeue(out _))
            {
            }

            if (_synth != IntPtr.Zero)
            {
                Native.delete_fluid_synth(_synth);
            }

            if (_settings != IntPtr.Zero)
            {
                Native.delete_fluid_settings(_settings);
            }

            if (_leftHandle.IsAllocated)
            {
                _leftHandle.Free();
            }

            if (_rightHandle.IsAllocated)
            {
                _rightHandle.Free();
            }
        }

        GC.SuppressFinalize(this);
    }

    private static string GetSoundFontPath(string baseDirectory) =>
        Path.Combine(baseDirectory, "SoundFonts", "FluidR3_Jampanion.sf2");

    private IAudioOutput CreateAudioOutput()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacAudioQueueOutput(RenderPcm16);
        }

        return (IAudioOutput?)AsioAudioOutput.TryCreate(RenderPcm16)
            ?? new WinMmAudioOutput(RenderPcm16);
    }

    private void RenderPcm16(short[] buffer, int sampleCount)
    {
        Array.Clear(buffer, 0, sampleCount);
        DrainCommands();

        var frames = sampleCount / 2;
        if (frames <= 0 || frames > RenderBufferFrames)
        {
            return;
        }

        var result = Native.fluid_synth_write_s16(
            _synth,
            frames,
            _leftHandle.AddrOfPinnedObject(),
            0,
            1,
            _rightHandle.AddrOfPinnedObject(),
            0,
            1);

        if (result < 0)
        {
            return;
        }

        for (var frame = 0; frame < frames; frame++)
        {
            buffer[frame * 2] = _left[frame];
            buffer[frame * 2 + 1] = _right[frame];
        }
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            switch (command.Kind)
            {
                case SynthCommandKind.NoteOn:
                    Native.fluid_synth_noteon(_synth, command.Channel, command.Data1, command.Data2);
                    break;

                case SynthCommandKind.NoteOff:
                    Native.fluid_synth_noteoff(_synth, command.Channel, command.Data1);
                    break;

                case SynthCommandKind.ControlChange:
                    Native.fluid_synth_cc(_synth, command.Channel, command.Data1, command.Data2);
                    break;

                case SynthCommandKind.ProgramChange:
                    Native.fluid_synth_program_change(_synth, command.Channel, command.Data1);
                    break;
            }
        }
    }

    private void SelectProgram(int channel, int bank, int program)
    {
        if (Native.fluid_synth_program_select(_synth, channel, _soundFontId, bank, program) < 0)
        {
            throw new InvalidOperationException($"FluidSynth could not select bank {bank}, program {program} on channel {channel}.");
        }
    }

    private static void SetSetting(int result, string setting)
    {
        // FluidSynth uses FLUID_OK (0) for success and FLUID_FAILED (-1) for
        // failure; this differs from many Win32 APIs.
        if (result < 0)
        {
            throw new InvalidOperationException($"FluidSynth rejected the {setting} setting.");
        }
    }

    private enum SynthCommandKind
    {
        NoteOn,
        NoteOff,
        ControlChange,
        ProgramChange
    }

    private readonly record struct SynthCommand(
        SynthCommandKind Kind,
        byte Channel,
        byte Data1,
        byte Data2)
    {
        public static SynthCommand NoteOn(byte channel, byte note, byte velocity) =>
            new(SynthCommandKind.NoteOn, channel, note, velocity);

        public static SynthCommand NoteOff(byte channel, byte note) =>
            new(SynthCommandKind.NoteOff, channel, note, 0);

        public static SynthCommand ControlChange(byte channel, byte controller, byte value) =>
            new(SynthCommandKind.ControlChange, channel, controller, value);

        public static SynthCommand ProgramChange(byte channel, byte program) =>
            new(SynthCommandKind.ProgramChange, channel, program, 0);
    }

    private static class Native
    {
        internal static IntPtr new_fluid_settings() => IsWindows ? Windows.new_fluid_settings() : Mac.new_fluid_settings();
        internal static void delete_fluid_settings(IntPtr settings)
        {
            if (IsWindows) Windows.delete_fluid_settings(settings);
            else Mac.delete_fluid_settings(settings);
        }
        internal static int fluid_settings_setnum(IntPtr settings, string name, double value) =>
            IsWindows ? Windows.fluid_settings_setnum(settings, name, value) : Mac.fluid_settings_setnum(settings, name, value);
        internal static int fluid_settings_setint(IntPtr settings, string name, int value) =>
            IsWindows ? Windows.fluid_settings_setint(settings, name, value) : Mac.fluid_settings_setint(settings, name, value);
        internal static IntPtr new_fluid_synth(IntPtr settings) => IsWindows ? Windows.new_fluid_synth(settings) : Mac.new_fluid_synth(settings);
        internal static void delete_fluid_synth(IntPtr synth)
        {
            if (IsWindows) Windows.delete_fluid_synth(synth);
            else Mac.delete_fluid_synth(synth);
        }
        internal static int fluid_synth_sfload(IntPtr synth, string filename, int resetPresets) =>
            IsWindows ? Windows.fluid_synth_sfload(synth, filename, resetPresets) : Mac.fluid_synth_sfload(synth, filename, resetPresets);
        internal static int fluid_synth_program_select(IntPtr synth, int channel, int soundFontId, int bank, int program) =>
            IsWindows ? Windows.fluid_synth_program_select(synth, channel, soundFontId, bank, program) : Mac.fluid_synth_program_select(synth, channel, soundFontId, bank, program);
        internal static int fluid_synth_program_change(IntPtr synth, int channel, int program) =>
            IsWindows ? Windows.fluid_synth_program_change(synth, channel, program) : Mac.fluid_synth_program_change(synth, channel, program);
        internal static int fluid_synth_noteon(IntPtr synth, int channel, int key, int velocity) =>
            IsWindows ? Windows.fluid_synth_noteon(synth, channel, key, velocity) : Mac.fluid_synth_noteon(synth, channel, key, velocity);
        internal static int fluid_synth_noteoff(IntPtr synth, int channel, int key) =>
            IsWindows ? Windows.fluid_synth_noteoff(synth, channel, key) : Mac.fluid_synth_noteoff(synth, channel, key);
        internal static int fluid_synth_cc(IntPtr synth, int channel, int controller, int value) =>
            IsWindows ? Windows.fluid_synth_cc(synth, channel, controller, value) : Mac.fluid_synth_cc(synth, channel, controller, value);
        internal static int fluid_synth_write_s16(IntPtr synth, int length, IntPtr left, int leftOffset, int leftIncrement, IntPtr right, int rightOffset, int rightIncrement) =>
            IsWindows
                ? Windows.fluid_synth_write_s16(synth, length, left, leftOffset, leftIncrement, right, rightOffset, rightIncrement)
                : Mac.fluid_synth_write_s16(synth, length, left, leftOffset, leftIncrement, right, rightOffset, rightIncrement);

        private static bool IsWindows => OperatingSystem.IsWindows();

        private static class Windows
        {
            private const string Library = "libfluidsynth-3.dll";

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr new_fluid_settings();
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void delete_fluid_settings(IntPtr settings);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int fluid_settings_setnum(IntPtr settings, string name, double value);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int fluid_settings_setint(IntPtr settings, string name, int value);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr new_fluid_synth(IntPtr settings);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void delete_fluid_synth(IntPtr synth);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int fluid_synth_sfload(IntPtr synth, string filename, int resetPresets);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int fluid_synth_program_select(IntPtr synth, int channel, int soundFontId, int bank, int program);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int fluid_synth_program_change(IntPtr synth, int channel, int program);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int fluid_synth_noteon(IntPtr synth, int channel, int key, int velocity);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int fluid_synth_noteoff(IntPtr synth, int channel, int key);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int fluid_synth_cc(IntPtr synth, int channel, int controller, int value);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int fluid_synth_write_s16(IntPtr synth, int length, IntPtr left, int leftOffset, int leftIncrement, IntPtr right, int rightOffset, int rightIncrement);
        }

        private static class Mac
        {
            private const string Library = "libfluidsynth.3.dylib";

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr new_fluid_settings();
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void delete_fluid_settings(IntPtr settings);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int fluid_settings_setnum(IntPtr settings, string name, double value);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int fluid_settings_setint(IntPtr settings, string name, int value);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr new_fluid_synth(IntPtr settings);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void delete_fluid_synth(IntPtr synth);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int fluid_synth_sfload(IntPtr synth, string filename, int resetPresets);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int fluid_synth_program_select(IntPtr synth, int channel, int soundFontId, int bank, int program);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int fluid_synth_program_change(IntPtr synth, int channel, int program);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int fluid_synth_noteon(IntPtr synth, int channel, int key, int velocity);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int fluid_synth_noteoff(IntPtr synth, int channel, int key);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int fluid_synth_cc(IntPtr synth, int channel, int controller, int value);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int fluid_synth_write_s16(IntPtr synth, int length, IntPtr left, int leftOffset, int leftIncrement, IntPtr right, int rightOffset, int rightIncrement);
        }
    }
}
