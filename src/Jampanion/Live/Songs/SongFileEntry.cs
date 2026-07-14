using System.IO;
using Jampanion.Core.Music;

namespace Jampanion.Live.Songs;

public sealed class SongFileEntry
{
    public SongFileEntry(string filePath, TuneForm? tune, string? error)
    {
        FilePath = filePath;
        Tune = tune;
        Error = error;
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public TuneForm? Tune { get; }
    public string? Error { get; }
    public bool IsValid => Tune is not null && string.IsNullOrWhiteSpace(Error);
    public string DisplayName => IsValid
        ? string.IsNullOrWhiteSpace(Tune!.Key) ? Tune.Title : $"{Tune.Title} — {Tune.Key}"
        : $"⚠ {Path.GetFileNameWithoutExtension(FilePath)}";
}
