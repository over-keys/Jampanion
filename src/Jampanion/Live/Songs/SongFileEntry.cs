using System.IO;
using Jampanion.Core.Music;

namespace Jampanion.Live.Songs;

public sealed class SongFileMetadata
{
    public SongFileMetadata(string filePath, string title, string? tuneId, string? error)
    {
        FilePath = filePath;
        Title = title;
        TuneId = tuneId;
        Error = error;
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public string Title { get; }
    public string? TuneId { get; }
    public string? Error { get; }
    public bool IsValid => string.IsNullOrWhiteSpace(Error);
}

public sealed class SongFileEntry
{
    public SongFileEntry(
        string filePath,
        TuneForm? tune,
        string? error,
        string? fingerprint = null)
    {
        FilePath = filePath;
        Tune = tune;
        Error = error;
        Fingerprint = fingerprint;
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public TuneForm? Tune { get; }
    public string? Error { get; }
    public string? Fingerprint { get; }
    public bool IsValid => Tune is not null && string.IsNullOrWhiteSpace(Error);
    public string DisplayName => IsValid
        ? string.IsNullOrWhiteSpace(Tune!.Key) ? Tune.Title : $"{Tune.Title} — {Tune.Key}"
        : $"⚠ {Path.GetFileNameWithoutExtension(FilePath)}";
}
