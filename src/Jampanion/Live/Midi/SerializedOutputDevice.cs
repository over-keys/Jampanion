using Jampanion.Core.Music;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace Jampanion.Live.Midi;

/// <summary>
/// Gives accompaniment playback and optional MIDI thru one ordered path to the
/// physical or virtual output. DryWetMIDI playback runs on its own clock thread,
/// while input callbacks arrive on a separate device thread.
/// </summary>
internal sealed class SerializedOutputDevice : IOutputDevice
{
    private readonly object _gate = new();
    private readonly byte[] _channelVolumes = Enumerable.Repeat((byte)127, 16).ToArray();
    private readonly bool[] _channelMutes = new bool[16];
    private IOutputDevice? _inner;
    private bool _disposed;

    public bool HasOutput
    {
        get
        {
            lock (_gate)
            {
                return !_disposed && _inner is not null;
            }
        }
    }

    public event EventHandler<MidiEventSentEventArgs>? EventSent;

    public void PrepareForEventsSending()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _inner?.PrepareForEventsSending();
        }
    }

    public void SendEvent(MidiEvent midiEvent)
    {
        ArgumentNullException.ThrowIfNull(midiEvent);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (midiEvent is NoteOnEvent noteOn && noteOn.Velocity > 0)
            {
                var channel = (byte)noteOn.Channel;
                if (_channelMutes[channel] || _channelVolumes[channel] == 0)
                {
                    return;
                }
            }

            // Playback objects retain this router while the user changes ports.
            // Events during the very short detached interval are intentionally
            // dropped instead of reaching a disposed device.
            _inner?.SendEvent(midiEvent);
        }
    }

    public void SetChannelVolume(byte channel, byte volume)
    {
        ValidateChannel(channel);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _channelVolumes[channel] = volume;
            if (_inner is null)
            {
                return;
            }

            SendControlChangeLocked(_inner, channel, 7, volume);
            if (volume == 0)
            {
                SilenceChannelLocked(_inner, channel);
            }
        }
    }

    public void SetChannelMute(byte channel, bool muted)
    {
        ValidateChannel(channel);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_channelMutes[channel] == muted)
            {
                return;
            }

            _channelMutes[channel] = muted;
            if (_inner is null)
            {
                return;
            }

            if (muted)
            {
                SilenceChannelLocked(_inner, channel);
            }
            else
            {
                SendControlChangeLocked(_inner, channel, 7, _channelVolumes[channel]);
            }
        }
    }

    public void Attach(IOutputDevice inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inner is not null)
            {
                throw new InvalidOperationException("Detach the current MIDI output before attaching another one.");
            }

            _inner = inner;
            _inner.EventSent += Inner_EventSent;
            for (byte channel = 0; channel < 16; channel++)
            {
                SendControlChangeLocked(_inner, channel, 7, _channelVolumes[channel]);
                if (_channelMutes[channel] || _channelVolumes[channel] == 0)
                {
                    SilenceChannelLocked(_inner, channel);
                }
            }
        }
    }

    public IOutputDevice? Detach()
    {
        lock (_gate)
        {
            if (_inner is null)
            {
                return null;
            }

            var detached = _inner;
            // Keep the final all-notes-off and the detach under the same router
            // lock, so a playback clock cannot insert a new Note On between
            // panic and port replacement.
            for (byte channel = 0; channel < 16; channel++)
            {
                foreach (var (controller, value) in new[] { (64, 0), (123, 0), (120, 0) })
                {
                    try
                    {
                        detached.SendEvent(new ControlChangeEvent(
                            (SevenBitNumber)controller,
                            (SevenBitNumber)value)
                        {
                            Channel = (FourBitNumber)channel
                        });
                    }
                    catch
                    {
                        // A failed device must still be detachable.
                    }
                }
            }
            detached.EventSent -= Inner_EventSent;
            _inner = null;
            return detached;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_inner is not null)
            {
                _inner.EventSent -= Inner_EventSent;
                _inner.Dispose();
                _inner = null;
            }
        }

        GC.SuppressFinalize(this);
    }

    private void Inner_EventSent(object? sender, MidiEventSentEventArgs e) =>
        EventSent?.Invoke(this, e);

    private static void ValidateChannel(byte channel)
    {
        if (channel > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }

    private static void SilenceChannelLocked(IOutputDevice output, byte channel)
    {
        SendControlChangeLocked(output, channel, 64, 0);
        SendControlChangeLocked(output, channel, 123, 0);
        SendControlChangeLocked(output, channel, 120, 0);
    }

    private static void SendControlChangeLocked(IOutputDevice output, byte channel, byte controller, byte value)
    {
        output.SendEvent(new ControlChangeEvent(
            (SevenBitNumber)controller,
            (SevenBitNumber)value)
        {
            Channel = (FourBitNumber)channel
        });
    }
}
