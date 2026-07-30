using Jampanion.Core.Music;

namespace Jampanion.Web.Models;

// Keep the song selector lightweight. A choice contains metadata only; the
// selected chart is loaded from its embedded ChordPro source or localStorage on demand.
public sealed record WebSongChoice(string Id, string Title, bool IsBuiltIn);

public sealed record WebStyleChoice(string Value, string DisplayName, AccompanimentStyle? Style)
{
    public static WebStyleChoice SongDefault { get; } = new("default", "Use song default", null);
}

public sealed record MidiInputChoice(string Id, string Name);

public sealed record WebChordSegment(int StartBeat, int BeatSpan, string Symbol);
