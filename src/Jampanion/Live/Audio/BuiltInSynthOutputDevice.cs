using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace Jampanion.Live.Audio;

/// <summary>
/// DryWetMIDI output endpoint backed by the app's small built-in trio synthesizer.
/// </summary>
public sealed class BuiltInSynthOutputDevice : IOutputDevice
{
    private readonly BuiltInTrioSynth _synth = new();
    private bool _disposed;

    public event EventHandler<MidiEventSentEventArgs>? EventSent;

    public void PrepareForEventsSending()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _synth.Start();
    }

    public void SendEvent(MidiEvent midiEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(midiEvent);

        _synth.Enqueue(midiEvent);
        EventSent?.Invoke(this, new MidiEventSentEventArgs(midiEvent));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _synth.Dispose();
        GC.SuppressFinalize(this);
    }
}
