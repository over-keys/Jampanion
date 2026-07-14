namespace Jampanion.Core.Music;

public sealed record IRealProImportedSong(
    string Title,
    string Composer,
    string Style,
    string Key,
    string ChordProText,
    IReadOnlyList<string> Warnings);

public sealed record IRealProImportDocument(
    IReadOnlyList<IRealProImportedSong> Songs,
    IReadOnlyList<string> Warnings);
