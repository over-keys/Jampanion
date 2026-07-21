using System.Diagnostics;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;
using Jampanion.Live.Audio;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using MidiPlayback = Melanchall.DryWetMidi.Multimedia.Playback;
using Melanchall.DryWetMidi.Interaction;

namespace Jampanion.Live.Midi;

public sealed class MidiPortService : IDisposable
{
    public const string BuiltInTrioOutputName = "Built-in Trio (Bass / Piano / Drums)";
    public const string MicrosoftGsWavetableSynthName = MidiOutputInitializationPlan.MicrosoftGsWavetableSynthName;
    private readonly object _gate = new();
    private readonly HashSet<(byte Channel, byte Note)> _activeTestNotes = new();
    private readonly SerializedOutputDevice _outputDevice = new();
    private readonly Func<AsioAudioSettings>? _asioSettingsProvider;

    private InputDevice? _inputDevice;
    private CancellationTokenSource _sessionCancellation = new();
    private bool _midiThruEnabled;
    private bool _disposed;

    public MidiPortService()
    {
    }

    internal MidiPortService(Func<AsioAudioSettings> asioSettingsProvider)
    {
        _asioSettingsProvider = asioSettingsProvider;
    }

    public event EventHandler<MidiInputMessage>? MessageReceived;
    public event EventHandler<string>? DeviceError;

    public bool IsInputOpen
    {
        get
        {
            lock (_gate)
            {
                return _inputDevice is not null;
            }
        }
    }

    public bool IsOutputOpen
    {
        get
        {
            lock (_gate)
            {
                return _outputDevice.HasOutput;
            }
        }
    }

    public static IReadOnlyList<string> GetInputPortNames()
    {
        var devices = InputDevice.GetAll();
        try
        {
            return devices
                .Select(device => device.Name)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            foreach (var device in devices)
            {
                device.Dispose();
            }
        }
    }

    public static IReadOnlyList<string> GetOutputPortNames()
    {
        var result = new List<string> { BuiltInTrioOutputName };
        ICollection<OutputDevice>? devices = null;

        try
        {
            devices = OutputDevice.GetAll();
            result.AddRange(devices
                .Select(device => device.Name)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase));
        }
        catch (MidiDeviceException)
        {
            // The internal synth must remain usable even when a Windows MIDI
            // driver fails during external-port enumeration.
        }
        finally
        {
            if (devices is not null)
            {
                foreach (var device in devices)
                {
                    device.Dispose();
                }
            }
        }

        return result;
    }

    public static bool IsMicrosoftGsWavetableSynth(string? outputPortName) =>
        MidiOutputInitializationPlan.AppliesToMicrosoftGsWavetableSynth(outputPortName);

    public static bool IsFluidSynth(string? outputPortName) =>
        !string.IsNullOrWhiteSpace(outputPortName) &&
        outputPortName.Contains("fluid", StringComparison.OrdinalIgnoreCase) &&
        outputPortName.Contains("synth", StringComparison.OrdinalIgnoreCase);

    public void Open(
        string? inputPortName,
        string? outputPortName,
        bool sendProgramChanges,
        bool midiThruEnabled)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(inputPortName) && string.IsNullOrWhiteSpace(outputPortName))
        {
            throw new ArgumentException("Select at least one MIDI input or output port.");
        }

        Close();

        InputDevice? input = null;
        IOutputDevice? output = null;
        var attached = false;

        try
        {
            if (!string.IsNullOrWhiteSpace(outputPortName))
            {
                output = string.Equals(outputPortName, BuiltInTrioOutputName, StringComparison.Ordinal)
                    ? CreateBuiltInSynthOutputDevice()
                    : OutputDevice.GetByName(outputPortName);
                output.PrepareForEventsSending();

                if (sendProgramChanges)
                {
                    SendProgramChanges(output);
                }
            }

            if (!string.IsNullOrWhiteSpace(inputPortName))
            {
                input = InputDevice.GetByName(inputPortName);
                input.EventReceived += OnInputEventReceived;
            }

            lock (_gate)
            {
                _sessionCancellation.Dispose();
                _sessionCancellation = new CancellationTokenSource();
                _inputDevice = input;
                if (output is not null)
                {
                    _outputDevice.Attach(output);
                }
                _midiThruEnabled = midiThruEnabled;
                attached = true;
            }

            input?.StartEventsListening();
            input = null;
            output = null;
        }
        catch
        {
            if (attached)
            {
                Close();
            }
            else
            {
                if (input is not null)
                {
                    input.EventReceived -= OnInputEventReceived;
                    input.Dispose();
                }

                output?.Dispose();
            }
            throw;
        }
    }

    public void SendProgramChanges()
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            EnsureOutputOpen();
            SendProgramChanges(_outputDevice);
        }
    }

    public void SwitchOutput(string? outputPortName, bool sendProgramChanges)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(outputPortName))
        {
            throw new ArgumentException("Select a MIDI output port.", nameof(outputPortName));
        }

        IOutputDevice? replacement = null;
        IOutputDevice? previous = null;
        try
        {
            replacement = string.Equals(outputPortName, BuiltInTrioOutputName, StringComparison.Ordinal)
                ? CreateBuiltInSynthOutputDevice()
                : OutputDevice.GetByName(outputPortName);
            replacement.PrepareForEventsSending();
            if (sendProgramChanges)
            {
                SendProgramChanges(replacement);
            }

            lock (_gate)
            {
                if (_outputDevice.HasOutput)
                {
                    PanicLocked();
                }

                previous = _outputDevice.Detach();
                _outputDevice.Attach(replacement);
                replacement = null;
                _sessionCancellation.Dispose();
                _sessionCancellation = new CancellationTokenSource();
            }

            previous?.Dispose();
            previous = null;
        }
        catch
        {
            // Opening the replacement happens before detaching the current
            // output, so ordinary device-open failures leave playback intact.
            if (previous is not null)
            {
                lock (_gate)
                {
                    if (!_outputDevice.HasOutput)
                    {
                        _outputDevice.Attach(previous);
                        previous = null;
                    }
                }
            }

            throw;
        }
        finally
        {
            replacement?.Dispose();
            previous?.Dispose();
        }
    }

    private BuiltInSynthOutputDevice CreateBuiltInSynthOutputDevice() =>
        new(_asioSettingsProvider?.Invoke());

    public void SetMidiThruEnabled(bool enabled)
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            if (_midiThruEnabled == enabled)
            {
                return;
            }

            _midiThruEnabled = enabled;
            if (!enabled && _outputDevice.HasOutput)
            {
                TrySendControlChangeLocked(SessionConstants.VibraphoneChannel, 64, 0);
                TrySendControlChangeLocked(SessionConstants.VibraphoneChannel, 123, 0);
                TrySendControlChangeLocked(SessionConstants.VibraphoneChannel, 120, 0);
            }
        }
    }

    public void SetChannelVolume(byte channel, byte volume)
    {
        ThrowIfDisposed();
        _outputDevice.SetChannelVolume(channel, volume);
    }

    public void SetChannelMute(byte channel, bool muted)
    {
        ThrowIfDisposed();
        _outputDevice.SetChannelMute(channel, muted);
    }

    public MidiPlayback CreatePlayback(
        IEnumerable<ITimedObject> timedObjects,
        TempoMap tempoMap)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(timedObjects);
        ArgumentNullException.ThrowIfNull(tempoMap);

        lock (_gate)
        {
            EnsureOutputOpen();
            return new MidiPlayback(
                timedObjects,
                tempoMap,
                _outputDevice,
                CreateHighPrecisionPlaybackSettings());
        }
    }

    public void PrimeOutput()
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            EnsureOutputOpen();
            // A zero-velocity Note On is a silent Note Off. Sending it before the
            // count-in gives external MIDI drivers one synchronous event to wake
            // their output path, so the first audible count is not cold-started.
            _outputDevice.SendEvent(new NoteOnEvent(
                (SevenBitNumber)37,
                (SevenBitNumber)0)
            {
                Channel = (FourBitNumber)SessionConstants.DrumsChannel
            });
        }
    }

    private static PlaybackSettings CreateHighPrecisionPlaybackSettings() => new()
    {
        ClockSettings = new MidiClockSettings
        {
            CreateTickGeneratorCallback = () => new HighPrecisionTickGenerator()
        }
    };

    public async Task SendTestNoteAsync(
        byte displayChannel,
        byte noteNumber,
        byte velocity,
        int durationMilliseconds)
    {
        ThrowIfDisposed();

        if (displayChannel is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(displayChannel));
        }

        if (noteNumber > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(noteNumber));
        }

        if (velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(velocity));
        }

        if (durationMilliseconds is < 20 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        }

        var channel = (byte)(displayChannel - 1);
        CancellationToken cancellationToken;

        lock (_gate)
        {
            EnsureOutputOpen();
            cancellationToken = _sessionCancellation.Token;
            SendEventLocked(new NoteOnEvent(
                (SevenBitNumber)noteNumber,
                (SevenBitNumber)velocity)
            {
                Channel = (FourBitNumber)channel
            });
            _activeTestNotes.Add((channel, noteNumber));
        }

        try
        {
            await Task.Delay(durationMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Close or Panic will send the required Note Off / all-notes-off messages.
        }
        finally
        {
            lock (_gate)
            {
                if (_outputDevice.HasOutput && _activeTestNotes.Remove((channel, noteNumber)))
                {
                    TrySendEventLocked(new NoteOffEvent(
                        (SevenBitNumber)noteNumber,
                        (SevenBitNumber)0)
                    {
                        Channel = (FourBitNumber)channel
                    });
                }
            }
        }
    }

    public void Panic()
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            if (!_outputDevice.HasOutput)
            {
                return;
            }

            _sessionCancellation.Cancel();

            foreach (var (channel, note) in _activeTestNotes.ToArray())
            {
                TrySendEventLocked(new NoteOffEvent(
                    (SevenBitNumber)note,
                    (SevenBitNumber)0)
                {
                    Channel = (FourBitNumber)channel
                });
            }

            _activeTestNotes.Clear();

            for (byte channel = 0; channel < 16; channel++)
            {
                TrySendControlChangeLocked(channel, 64, 0);   // Sustain off
                TrySendControlChangeLocked(channel, 123, 0);  // All Notes Off
                TrySendControlChangeLocked(channel, 120, 0);  // All Sound Off
            }

            _sessionCancellation.Dispose();
            _sessionCancellation = new CancellationTokenSource();
        }
    }

    public void Close()
    {
        InputDevice? input;
        IOutputDevice? output;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_outputDevice.HasOutput)
            {
                PanicLocked();
            }

            _sessionCancellation.Cancel();
            input = _inputDevice;
            output = _outputDevice.Detach();
            _inputDevice = null;
            _midiThruEnabled = false;
        }

        if (input is not null)
        {
            try
            {
                input.EventReceived -= OnInputEventReceived;
                if (input.IsListeningForEvents)
                {
                    input.StopEventsListening();
                }
            }
            catch (Exception ex)
            {
                RaiseDeviceError($"Could not stop MIDI input: {ex.Message}");
            }
            finally
            {
                input.Dispose();
            }
        }

        output?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close();

        lock (_gate)
        {
            _disposed = true;
            _sessionCancellation.Dispose();
            _outputDevice.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void OnInputEventReceived(object? sender, MidiEventReceivedEventArgs e)
    {
        lock (_gate)
        {
            if (_midiThruEnabled && _outputDevice.HasOutput && MidiThruEventRouter.RouteToVibraphone(e.Event) is MidiEvent thruEvent)
            {
                TrySendEventLocked(thruEvent);
            }
        }

        var timestampMilliseconds = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
        var isNoteOn = e.Event is NoteOnEvent noteOn && noteOn.Velocity > 0;
        var isNoteOff = e.Event is NoteOffEvent || e.Event is NoteOnEvent noteOnZero && noteOnZero.Velocity == 0;
        var channelEvent = e.Event as ChannelEvent;
        byte? channel = channelEvent is null ? null : (byte)((byte)channelEvent.Channel + 1);
        byte? noteNumber = e.Event switch
        {
            NoteOnEvent note => (byte)note.NoteNumber,
            NoteOffEvent note => (byte)note.NoteNumber,
            _ => null
        };
        byte? velocity = e.Event switch
        {
            NoteOnEvent note => (byte)note.Velocity,
            NoteOffEvent note => (byte)note.Velocity,
            _ => null
        };

        MessageReceived?.Invoke(
            this,
            new MidiInputMessage(
                DateTimeOffset.Now,
                timestampMilliseconds,
                e.Event.EventType.ToString(),
                e.Event.ToString() ?? e.Event.EventType.ToString(),
                isNoteOn,
                isNoteOff,
                channel,
                noteNumber,
                velocity));
    }

    private static void SendProgramChanges(IOutputDevice output)
    {
        foreach (var assignment in MidiOutputInitializationPlan.GeneralMidi)
        {
            output.SendEvent(new ProgramChangeEvent((SevenBitNumber)assignment.Program)
            {
                Channel = (FourBitNumber)assignment.Channel
            });
        }
    }

    private void PanicLocked()
    {
        _sessionCancellation.Cancel();

        foreach (var (channel, note) in _activeTestNotes.ToArray())
        {
            TrySendEventLocked(new NoteOffEvent(
                (SevenBitNumber)note,
                (SevenBitNumber)0)
            {
                Channel = (FourBitNumber)channel
            });
        }

        _activeTestNotes.Clear();

        for (byte channel = 0; channel < 16; channel++)
        {
            TrySendControlChangeLocked(channel, 64, 0);
            TrySendControlChangeLocked(channel, 123, 0);
            TrySendControlChangeLocked(channel, 120, 0);
        }

    }

    private void TrySendControlChangeLocked(byte channel, byte controller, byte value)
    {
        TrySendEventLocked(new ControlChangeEvent(
            (SevenBitNumber)controller,
            (SevenBitNumber)value)
        {
            Channel = (FourBitNumber)channel
        });
    }

    private void SendEventLocked(MidiEvent midiEvent)
    {
        EnsureOutputOpen();
        _outputDevice.SendEvent(midiEvent);
    }

    private void TrySendEventLocked(MidiEvent midiEvent)
    {
        try
        {
            _outputDevice.SendEvent(midiEvent);
        }
        catch (Exception ex)
        {
            RaiseDeviceError($"MIDI output error: {ex.Message}");
        }
    }

    private void EnsureOutputOpen()
    {
        if (!_outputDevice.HasOutput)
        {
            throw new InvalidOperationException("No MIDI output port is open.");
        }
    }

    private void RaiseDeviceError(string message)
    {
        DeviceError?.Invoke(this, message);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
