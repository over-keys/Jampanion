using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace Jampanion.Live.Audio;

/// <summary>
/// DryWetMIDI output endpoint backed by the embedded FluidSynth trio on
/// Windows and macOS, with the sample-free synth retained as a platform-safe
/// fallback when native assets are not present.
/// </summary>
public sealed class BuiltInSynthOutputDevice : IOutputDevice
{
    private readonly BuiltInTrioSynth? _synth;
    private readonly FluidSynthOutputDevice? _fluidSynth;
    private bool _disposed;

    public BuiltInSynthOutputDevice(AsioAudioSettings? asioSettings = null)
    {
        if (FluidSynthOutputDevice.IsAvailable())
        {
            try
            {
                _fluidSynth = FluidSynthOutputDevice.CreateFromApplicationAssets(asioSettings);
                return;
            }
            catch
            {
                // A damaged or incomplete native bundle should not make the
                // built-in output unusable. Keep the legacy fallback available.
            }
        }

        _synth = new BuiltInTrioSynth();
    }

    public event EventHandler<MidiEventSentEventArgs>? EventSent;

    public void PrepareForEventsSending()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fluidSynth is not null)
        {
            _fluidSynth.Start();
        }
        else
        {
            _synth!.Start();
        }
    }

    public void SendEvent(MidiEvent midiEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(midiEvent);

        if (_fluidSynth is not null)
        {
            _fluidSynth.Enqueue(midiEvent);
        }
        else
        {
            _synth!.Enqueue(midiEvent);
        }
        EventSent?.Invoke(this, new MidiEventSentEventArgs(midiEvent));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_fluidSynth is not null)
        {
            _fluidSynth.Dispose();
        }

        if (_synth is not null)
        {
            _synth.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
