using System.Collections.Concurrent;
using Melanchall.DryWetMidi.Core;

namespace Jampanion.Live.Audio;

/// <summary>
/// Deliberately small, sample-free preview instrument. It is not intended to
/// replace a dedicated piano/bass/drum library; it makes the application usable
/// without a DAW or an external synth.
/// </summary>
internal sealed class BuiltInTrioSynth : IDisposable
{
    private const int SampleRate = 48_000;
    private const int VibraphoneChannel = 0; // Display channel 1.
    private const int BassChannel = 1;       // Display channel 2.
    private const int PianoChannel = 2;  // Display channel 3.
    private const int DrumsChannel = 9;  // Display channel 10.
    private const int MaximumVoices = 72;
    private const int HardMaximumVoices = 88;
    private const int SineTableBits = 14;
    private const int SineTableSize = 1 << SineTableBits;
    private const int SineTableMask = SineTableSize - 1;
    private const double TwoPi = Math.PI * 2.0;
    private static readonly double[] SineTable = BuildSineTable();

    private readonly ConcurrentQueue<SynthCommand> _commands = new();
    private readonly byte[] _channelVolumes = Enumerable.Repeat((byte)127, 16).ToArray();
    private readonly List<SynthVoice> _voices = new();
    private readonly IAudioOutput _audioOutput;
    private bool _started;
    private bool _disposed;

    public BuiltInTrioSynth()
    {
        _audioOutput = CreateAudioOutput(RenderPcm16);
    }

    internal static bool IsAudioOutputSupportedOnCurrentPlatform =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _audioOutput.Start();
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
        }
    }

    internal void RenderForTest(short[] buffer) => RenderPcm16(buffer, buffer.Length);

    private static IAudioOutput CreateAudioOutput(Action<short[], int> render)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WinMmAudioOutput(render);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacAudioQueueOutput(render);
        }

        throw new PlatformNotSupportedException("The built-in trio synth currently supports live audio output on Windows and macOS.");
    }

    private void RenderPcm16(short[] buffer, int sampleCount)
    {
        Array.Clear(buffer, 0, sampleCount);
        DrainCommands();

        var frames = sampleCount / 2;
        for (var frame = 0; frame < frames; frame++)
        {
            double left = 0;
            double right = 0;

            for (var i = _voices.Count - 1; i >= 0; i--)
            {
                var voice = _voices[i];
                var sample = voice.NextSample();
                if (voice.IsFinished)
                {
                    _voices.RemoveAt(i);
                    continue;
                }

                var channelGain = GetChannelGain(voice);
                left += sample * voice.LeftGain * channelGain;
                right += sample * voice.RightGain * channelGain;
            }

            var limitedLeft = SoftClip(left * 0.82);
            var limitedRight = SoftClip(right * 0.82);
            buffer[frame * 2] = (short)Math.Clamp((int)Math.Round(limitedLeft * short.MaxValue), short.MinValue, short.MaxValue);
            buffer[frame * 2 + 1] = (short)Math.Clamp((int)Math.Round(limitedRight * short.MaxValue), short.MinValue, short.MaxValue);
        }
    }

    private static double SoftClip(double value)
    {
        var magnitude = Math.Abs(value);
        return value / (1.0 + 0.42 * magnitude);
    }

    private static double FastSin(double phase)
    {
        phase -= Math.Floor(phase / TwoPi) * TwoPi;
        var tablePosition = phase * (SineTableSize / TwoPi);
        var index = (int)tablePosition;
        var fraction = tablePosition - index;
        var first = SineTable[index & SineTableMask];
        var second = SineTable[(index + 1) & SineTableMask];
        return first + (second - first) * fraction;
    }

    private static double[] BuildSineTable()
    {
        var table = new double[SineTableSize];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = Math.Sin(i * TwoPi / table.Length);
        }

        return table;
    }

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
            _voices.Clear();
            while (_commands.TryDequeue(out _))
            {
            }
        }

        GC.SuppressFinalize(this);
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            switch (command.Kind)
            {
                case SynthCommandKind.NoteOn:
                    StartVoice(command.Channel, command.Data1, command.Data2);
                    break;

                case SynthCommandKind.NoteOff:
                    ReleaseVoice(command.Channel, command.Data1);
                    break;

                case SynthCommandKind.ControlChange:
                    HandleControlChange(command.Channel, command.Data1, command.Data2);
                    break;
            }
        }
    }

    private void StartVoice(byte channel, byte note, byte velocity)
    {
        // Re-articulating the same pitched note first gives the previous voice a
        // very short release. This avoids unbounded stacking of repeated piano
        // voicings and, unlike abrupt removal, does not create a waveform jump.
        if (channel != DrumsChannel)
        {
            foreach (var existing in _voices)
            {
                if (existing.ChannelMatches(channel) && existing.Note == note)
                {
                    existing.FastRelease();
                }
            }
        }

        if (_voices.Count >= MaximumVoices)
        {
            var candidate = _voices
                .Where(voice => !voice.IsFastReleasing)
                .OrderByDescending(voice => voice.Age)
                .FirstOrDefault();
            candidate?.FastRelease();
        }

        if (_voices.Count >= HardMaximumVoices)
        {
            _voices.RemoveAt(0);
        }

        SynthVoice voice = channel switch
        {
            VibraphoneChannel => new VibraphoneVoice(note, velocity, SampleRate),
            BassChannel => new BassVoice(note, velocity, SampleRate),
            DrumsChannel => DrumVoice.Create(note, velocity, SampleRate),
            _ => new PianoVoice(note, velocity, SampleRate)
        };

        _voices.Add(voice);
    }

    private void ReleaseVoice(byte channel, byte note)
    {
        // Drums are one-shot sounds; Note Off does not truncate cymbals or drums.
        if (channel == DrumsChannel)
        {
            return;
        }

        foreach (var voice in _voices)
        {
            if (voice.ChannelMatches(channel) && voice.Note == note)
            {
                voice.Release();
            }
        }
    }

    private void HandleControlChange(byte channel, byte controller, byte value)
    {
        switch (controller)
        {
            case 7: // Channel volume.
                _channelVolumes[channel] = value;
                break;

            case 64 when value == 0: // Sustain off. No sustained-pedal model yet.
            case 123:                // All Notes Off.
                foreach (var voice in _voices)
                {
                    if (voice.ChannelMatches(channel))
                    {
                        voice.Release();
                    }
                }
                break;

            case 120: // All Sound Off.
                _voices.RemoveAll(voice => voice.ChannelMatches(channel));
                break;
        }
    }

    private double GetChannelGain(SynthVoice voice)
    {
        var channel = voice.ChannelMatches(PianoChannel)
            ? PianoChannel
            : voice.ChannelMatches(BassChannel)
                ? BassChannel
                : voice.ChannelMatches(DrumsChannel)
                    ? DrumsChannel
                    : VibraphoneChannel;
        return _channelVolumes[channel] / 127.0;
    }

    private enum SynthCommandKind
    {
        NoteOn,
        NoteOff,
        ControlChange
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
    }

    private abstract class SynthVoice
    {
        protected readonly int SampleRate;
        protected readonly double Velocity;
        protected long AgeSamples;
        protected long ReleaseSamples;
        protected bool IsReleased;
        protected uint NoiseState;
        private double _releaseGain = 1.0;
        private double _releaseMultiplier;
        private bool _fastRelease;

        protected SynthVoice(byte note, byte velocity, int sampleRate, double pan)
        {
            Note = note;
            SampleRate = sampleRate;
            Velocity = Math.Pow(velocity / 127.0, 1.25);
            var angle = (pan + 1.0) * Math.PI / 4.0;
            LeftGain = Math.Cos(angle);
            RightGain = Math.Sin(angle);
            NoiseState = (uint)(note * 1_103_515_245u + velocity * 12_345u + 1u);
        }

        public byte Note { get; }
        public double LeftGain { get; }
        public double RightGain { get; }
        public bool IsFinished { get; protected set; }
        public long Age => AgeSamples;
        public bool IsFastReleasing => _fastRelease;

        public abstract double NextSample();
        public abstract bool ChannelMatches(byte channel);

        public virtual void Release()
        {
            IsReleased = true;
        }

        public void FastRelease()
        {
            IsReleased = true;
            _fastRelease = true;
            _releaseMultiplier = Math.Exp(-1.0 / (0.004 * SampleRate));
        }

        protected double Time => AgeSamples / (double)SampleRate;
        protected double ReleaseTime => ReleaseSamples / (double)SampleRate;

        protected double Noise()
        {
            NoiseState = NoiseState * 1_664_525u + 1_013_904_223u;
            return ((NoiseState >> 8) / 8_388_607.5) - 1.0;
        }

        protected static double MidiFrequency(byte note) =>
            440.0 * Math.Pow(2.0, (note - 69) / 12.0);

        protected double ApplyRelease(double sample, double releaseSeconds)
        {
            if (!IsReleased)
            {
                return sample;
            }

            if (_releaseMultiplier == 0)
            {
                _releaseMultiplier = Math.Exp(-1.0 / (releaseSeconds * SampleRate));
            }

            _releaseGain *= _releaseMultiplier;
            ReleaseSamples++;
            if (_releaseGain < 0.0008)
            {
                IsFinished = true;
            }

            return sample * _releaseGain;
        }
    }

    private sealed class VibraphoneVoice : SynthVoice
    {
        private readonly double _phaseIncrement;
        private readonly double _decayMultiplier;
        private readonly double _attackStep;
        private readonly double _tremoloIncrement;
        private double _phase;
        private double _tremoloPhase;
        private double _envelope = 1.0;
        private double _attackGain;

        public VibraphoneVoice(byte note, byte velocity, int sampleRate)
            : base(note, velocity, sampleRate, pan: ((note % 12) - 5.5) / 22.0)
        {
            var frequency = MidiFrequency(note);
            _phaseIncrement = TwoPi * frequency / SampleRate;
            _decayMultiplier = Math.Exp(-1.0 / (2.6 * SampleRate));
            _attackStep = 1.0 / (0.003 * SampleRate);
            _tremoloIncrement = TwoPi * 5.4 / SampleRate;
        }

        public override bool ChannelMatches(byte channel) => channel == VibraphoneChannel;

        public override double NextSample()
        {
            _attackGain = Math.Min(1.0, _attackGain + _attackStep);
            var tremolo = 0.86 + 0.14 * FastSin(_tremoloPhase);
            var sample =
                FastSin(_phase) * 0.64 +
                FastSin(_phase * 3.98 + 0.18) * 0.21 +
                FastSin(_phase * 10.04 + 0.51) * 0.10 +
                FastSin(_phase * 12.02 + 0.86) * 0.045;

            _phase += _phaseIncrement;
            if (_phase >= TwoPi)
            {
                _phase -= TwoPi;
            }

            _tremoloPhase += _tremoloIncrement;
            if (_tremoloPhase >= TwoPi)
            {
                _tremoloPhase -= TwoPi;
            }

            _envelope *= _decayMultiplier;
            AgeSamples++;
            return ApplyRelease(sample * _envelope * tremolo * _attackGain * Velocity * 0.31, 0.28);
        }
    }

    private sealed class PianoVoice : SynthVoice
    {
        private readonly double _phaseIncrement;
        private readonly double _bodyMultiplier;
        private readonly double _hammerMultiplier;
        private readonly double _attackStep;
        private double _phase;
        private double _bodyEnvelope = 1.0;
        private double _hammerEnvelope = 1.0;
        private double _attackGain;

        public PianoVoice(byte note, byte velocity, int sampleRate)
            : base(note, velocity, sampleRate, pan: ((note % 12) - 5.5) / 18.0)
        {
            var frequency = MidiFrequency(note);
            _phaseIncrement = TwoPi * frequency / SampleRate;
            var bodySeconds = 1.45 + Math.Max(0, 60 - Note) * 0.018;
            _bodyMultiplier = Math.Exp(-1.0 / (bodySeconds * SampleRate));
            _hammerMultiplier = Math.Exp(-1.0 / (0.018 * SampleRate));
            _attackStep = 1.0 / (0.0045 * SampleRate);
        }

        public override bool ChannelMatches(byte channel) =>
            channel == PianoChannel || channel is not VibraphoneChannel and not BassChannel and not DrumsChannel;

        public override double NextSample()
        {
            _attackGain = Math.Min(1.0, _attackGain + _attackStep);
            var body = 0.14 + 0.86 * _bodyEnvelope;
            var hammer = Noise() * _hammerEnvelope * 0.13;

            var sample =
                FastSin(_phase) * 0.64 +
                FastSin(_phase * 2.003 + 0.2) * 0.20 +
                FastSin(_phase * 3.012 + 0.4) * 0.09 +
                FastSin(_phase * 4.027 + 0.7) * 0.045 +
                FastSin(_phase * 5.06 + 1.1) * 0.02;

            _phase += _phaseIncrement;
            if (_phase >= TwoPi)
            {
                _phase -= TwoPi;
            }

            _bodyEnvelope *= _bodyMultiplier;
            _hammerEnvelope *= _hammerMultiplier;
            AgeSamples++;
            return ApplyRelease((sample * body + hammer) * _attackGain * Velocity * 0.29, 0.16);
        }
    }

    private sealed class BassVoice : SynthVoice
    {
        private readonly double _phaseIncrement;
        private readonly double _bodyMultiplier;
        private readonly double _fingerMultiplier;
        private readonly double _attackStep;
        private double _phase;
        private double _bodyEnvelope = 1.0;
        private double _fingerEnvelope = 1.0;
        private double _attackGain;

        public BassVoice(byte note, byte velocity, int sampleRate)
            : base(note, velocity, sampleRate, pan: -0.08)
        {
            var frequency = MidiFrequency(note);
            _phaseIncrement = TwoPi * frequency / SampleRate;
            _bodyMultiplier = Math.Exp(-1.0 / (0.95 * SampleRate));
            _fingerMultiplier = Math.Exp(-1.0 / (0.014 * SampleRate));
            _attackStep = 1.0 / (0.006 * SampleRate);
        }

        public override bool ChannelMatches(byte channel) => channel == BassChannel;

        public override double NextSample()
        {
            _attackGain = Math.Min(1.0, _attackGain + _attackStep);
            var body = 0.24 + 0.76 * _bodyEnvelope;
            var finger = Noise() * _fingerEnvelope * 0.10;
            var sample =
                FastSin(_phase) * 0.78 +
                FastSin(_phase * 2.0 + 0.08) * 0.18 +
                FastSin(_phase * 3.0 + 0.21) * 0.07 +
                FastSin(_phase * 4.0 + 0.44) * 0.025;

            _phase += _phaseIncrement;
            if (_phase >= TwoPi)
            {
                _phase -= TwoPi;
            }

            _bodyEnvelope *= _bodyMultiplier;
            _fingerEnvelope *= _fingerMultiplier;
            AgeSamples++;
            return ApplyRelease((sample * body + finger) * _attackGain * Velocity * 0.42, 0.085);
        }
    }

    private sealed class DrumVoice : SynthVoice
    {
        private readonly DrumKind _kind;
        private readonly double _baseFrequency;
        private readonly double _envelopeMultiplier;
        private readonly double _secondaryMultiplier;
        private readonly double _pitchMultiplier;
        private readonly double _phaseIncrement;
        private readonly double _metalIncrement1;
        private readonly double _metalIncrement2;
        private readonly double _metalIncrement3;
        private readonly long _lifetimeSamples;
        private double _phase;
        private double _metalPhase1;
        private double _metalPhase2;
        private double _metalPhase3;
        private double _previousNoise;
        private double _smoothedNoise;
        private double _envelope = 1.0;
        private double _secondaryEnvelope = 1.0;
        private double _pitchEnvelope = 1.0;

        private DrumVoice(byte note, byte velocity, int sampleRate, DrumKind kind, double pan, double baseFrequency)
            : base(note, velocity, sampleRate, pan)
        {
            _kind = kind;
            _baseFrequency = baseFrequency;
            _phaseIncrement = TwoPi * baseFrequency / SampleRate;
            _metalIncrement1 = TwoPi * 2_490 / SampleRate;
            _metalIncrement2 = TwoPi * 3_710 / SampleRate;
            _metalIncrement3 = TwoPi * 5_830 / SampleRate;

            var (decay, secondaryDecay, pitchDecay, lifetime) = kind switch
            {
                DrumKind.Kick => (0.115, 0.009, 0.028, 0.42),
                DrumKind.SideStick => (0.025, 0.025, 0.028, 0.12),
                DrumKind.Brush => (0.34, 0.34, 0.028, 0.72),
                DrumKind.Snare => (0.085, 0.085, 0.028, 0.34),
                DrumKind.ClosedHat => (0.028, 0.028, 0.028, 0.13),
                DrumKind.OpenHat => (0.23, 0.23, 0.028, 0.85),
                DrumKind.Ride => (0.46, 0.46, 0.028, 1.45),
                DrumKind.Crash => (0.92, 0.92, 0.028, 2.8),
                DrumKind.Cowbell => (0.19, 0.19, 0.028, 0.62),
                DrumKind.Clave => (0.032, 0.032, 0.028, 0.14),
                DrumKind.Cabasa => (0.055, 0.030, 0.028, 0.22),
                DrumKind.Conga => (0.16, 0.08, 0.038, 0.52),
                DrumKind.Timbale => (0.11, 0.055, 0.032, 0.42),
                _ => (0.18, 0.18, 0.028, 0.65)
            };
            _envelopeMultiplier = Math.Exp(-1.0 / (decay * SampleRate));
            _secondaryMultiplier = Math.Exp(-1.0 / (secondaryDecay * SampleRate));
            _pitchMultiplier = Math.Exp(-1.0 / (pitchDecay * SampleRate));
            _lifetimeSamples = (long)Math.Ceiling(lifetime * SampleRate);
        }

        public static DrumVoice Create(byte note, byte velocity, int sampleRate)
        {
            if (note == 38 && velocity <= 36)
            {
                return new DrumVoice(note, velocity, sampleRate, DrumKind.Brush, -0.02, 0);
            }

            return note switch
            {
                35 or 36 => new DrumVoice(note, velocity, sampleRate, DrumKind.Kick, 0, 52),
                37 => new DrumVoice(note, velocity, sampleRate, DrumKind.SideStick, 0.18, 920),
                38 or 40 => new DrumVoice(note, velocity, sampleRate, DrumKind.Snare, 0.12, 185),
                42 or 44 => new DrumVoice(note, velocity, sampleRate, DrumKind.ClosedHat, 0.38, 0),
                46 => new DrumVoice(note, velocity, sampleRate, DrumKind.OpenHat, 0.38, 0),
                49 or 57 => new DrumVoice(note, velocity, sampleRate, DrumKind.Crash, 0.22, 0),
                51 or 53 or 59 => new DrumVoice(note, velocity, sampleRate, DrumKind.Ride, 0.28, 0),
                56 => new DrumVoice(note, velocity, sampleRate, DrumKind.Cowbell, 0.24, 560),
                62 => new DrumVoice(note, velocity, sampleRate, DrumKind.Conga, -0.12, 250),
                63 => new DrumVoice(note, velocity, sampleRate, DrumKind.Conga, -0.08, 310),
                64 => new DrumVoice(note, velocity, sampleRate, DrumKind.Conga, -0.18, 205),
                65 => new DrumVoice(note, velocity, sampleRate, DrumKind.Timbale, 0.12, 390),
                66 => new DrumVoice(note, velocity, sampleRate, DrumKind.Timbale, 0.18, 520),
                69 or 70 => new DrumVoice(note, velocity, sampleRate, DrumKind.Cabasa, 0.34, 0),
                75 => new DrumVoice(note, velocity, sampleRate, DrumKind.Clave, 0.08, 1_640),
                41 or 43 or 45 or 47 or 48 or 50 => new DrumVoice(note, velocity, sampleRate, DrumKind.Tom, -0.18, TomFrequency(note)),
                _ => new DrumVoice(note, velocity, sampleRate, DrumKind.Snare, 0, 170)
            };
        }

        public override bool ChannelMatches(byte channel) => channel == DrumsChannel;

        public override double NextSample()
        {
            var noise = Noise();
            var highNoise = noise - _previousNoise * 0.82;
            _previousNoise = noise;

            double sample;
            switch (_kind)
            {
                case DrumKind.Kick:
                {
                    var frequency = 42 + 72 * _pitchEnvelope;
                    _phase += TwoPi * frequency / SampleRate;
                    sample = FastSin(_phase) * _envelope + noise * _secondaryEnvelope * 0.10;
                    _pitchEnvelope *= _pitchMultiplier;
                    _secondaryEnvelope *= _secondaryMultiplier;
                    break;
                }

                case DrumKind.SideStick:
                    _phase += _phaseIncrement;
                    sample = (FastSin(_phase) * 0.6 + highNoise * 0.7) * _envelope;
                    break;

                case DrumKind.Brush:
                    _smoothedNoise = _smoothedNoise * 0.94 + noise * 0.06;
                    sample = (_smoothedNoise * 0.78 + noise * 0.22) * _envelope;
                    break;

                case DrumKind.Snare:
                    _phase += _phaseIncrement;
                    sample = (highNoise * 0.78 + FastSin(_phase) * 0.22) * _envelope;
                    break;

                case DrumKind.ClosedHat:
                case DrumKind.OpenHat:
                    sample = highNoise * _envelope;
                    break;

                case DrumKind.Ride:
                    sample = Metallic(highNoise) * _envelope;
                    break;

                case DrumKind.Crash:
                    sample = Metallic(highNoise) * _envelope * 1.12;
                    break;

                case DrumKind.Cowbell:
                    _phase += _phaseIncrement;
                    _metalPhase1 += TwoPi * (_baseFrequency * 1.48) / SampleRate;
                    sample = (FastSin(_phase) * 0.62 + FastSin(_metalPhase1) * 0.42 + highNoise * 0.08) * _envelope;
                    break;

                case DrumKind.Clave:
                    _phase += _phaseIncrement;
                    sample = (FastSin(_phase) * 0.78 + highNoise * 0.30) * _envelope;
                    break;

                case DrumKind.Cabasa:
                    _smoothedNoise = _smoothedNoise * 0.72 + highNoise * 0.28;
                    sample = (highNoise * 0.68 + _smoothedNoise * 0.42) * _envelope;
                    _secondaryEnvelope *= _secondaryMultiplier;
                    break;

                case DrumKind.Conga:
                case DrumKind.Timbale:
                    _phase += _phaseIncrement * (0.86 + 0.22 * _pitchEnvelope);
                    sample = (FastSin(_phase) * 0.84 + noise * (_kind == DrumKind.Conga ? 0.16 : 0.28)) * _envelope;
                    _pitchEnvelope *= _pitchMultiplier;
                    break;

                default:
                    _phase += _phaseIncrement;
                    sample = (FastSin(_phase) * 0.88 + noise * 0.12) * _envelope;
                    break;
            }

            _envelope *= _envelopeMultiplier;
            AgeSamples++;
            if (AgeSamples >= _lifetimeSamples)
            {
                IsFinished = true;
            }

            var gain = _kind switch
            {
                DrumKind.Ride or DrumKind.Crash => 0.18,
                DrumKind.Cowbell or DrumKind.Clave => 0.22,
                DrumKind.Cabasa => 0.055,
                DrumKind.Conga or DrumKind.Timbale => 0.27,
                DrumKind.Brush => 0.20,
                _ => 0.30
            };
            return sample * Velocity * gain;
        }

        private double Metallic(double highNoise)
        {
            _metalPhase1 += _metalIncrement1;
            _metalPhase2 += _metalIncrement2;
            _metalPhase3 += _metalIncrement3;
            var metal = FastSin(_metalPhase1) * 0.30 + FastSin(_metalPhase2) * 0.22 + FastSin(_metalPhase3) * 0.13;
            return highNoise * 0.58 + metal;
        }

        private static double TomFrequency(byte note) => note switch
        {
            <= 43 => 95,
            <= 45 => 125,
            <= 47 => 155,
            <= 48 => 185,
            _ => 220
        };

        private enum DrumKind
        {
            Kick,
            SideStick,
            Brush,
            Snare,
            ClosedHat,
            OpenHat,
            Ride,
            Crash,
            Cowbell,
            Clave,
            Cabasa,
            Conga,
            Timbale,
            Tom
        }
    }
}
