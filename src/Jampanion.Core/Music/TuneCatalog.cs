namespace Jampanion.Core.Music;

public static class TuneCatalog
{
    private static readonly IReadOnlyList<TuneForm> Tunes = DefaultSongCatalog.All
        .Select(file => file.Tune)
        .OrderBy(tune => string.Equals(tune.Id, "autumn-leaves", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(tune => tune.Title, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static IReadOnlyList<TuneForm> All => Tunes;
    public static TuneForm Default => Tunes[0];

    public static TuneForm GetById(string? id) =>
        Tunes.FirstOrDefault(tune => string.Equals(tune.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;
}
