namespace Jampanion.Live.Songs;

public sealed record IRealProFileImportResult(
    IReadOnlyList<string> ImportedFilePaths,
    IReadOnlyList<string> Warnings);
