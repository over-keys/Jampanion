namespace Jampanion.Live.Midi;

public sealed record MidiInputMessage(
    DateTimeOffset ReceivedAt,
    long TimestampMilliseconds,
    string EventType,
    string Description,
    bool IsNoteOn,
    bool IsNoteOff,
    byte? Channel,
    byte? NoteNumber,
    byte? Velocity);
