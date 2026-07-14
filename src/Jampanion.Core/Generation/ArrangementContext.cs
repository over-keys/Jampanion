namespace Jampanion.Core.Generation;

public sealed record ArrangementContext(
    byte? PreviousBassNote,
    IReadOnlyList<byte>? PreviousPianoVoicing,
    int PreviousPianoCellIndex,
    int PreviousDrumPatternIndex,
    int PreviousFillVariant,
    bool PreviousSectionEndedWithFill,
    IReadOnlyList<byte>? RecentBassNotes = null,
    int PreviousBassDirection = 0,
    int PreviousBassDirectionRun = 0,
    int PreviousRidePhraseIndex = -1,
    int PreviousDrumCompPatternIndex = -1)
{
    public static ArrangementContext Initial { get; } = new(
        PreviousBassNote: null,
        PreviousPianoVoicing: null,
        PreviousPianoCellIndex: -1,
        PreviousDrumPatternIndex: -1,
        PreviousFillVariant: -1,
        PreviousSectionEndedWithFill: false,
        RecentBassNotes: Array.Empty<byte>(),
        PreviousBassDirection: 0,
        PreviousBassDirectionRun: 0,
        PreviousRidePhraseIndex: -1,
        PreviousDrumCompPatternIndex: -1);
}
