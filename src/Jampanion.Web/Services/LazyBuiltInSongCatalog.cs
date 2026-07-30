using System.Reflection;
using Jampanion.Core.Music;
using Jampanion.Web.Models;

namespace Jampanion.Web.Services;

/// <summary>
/// Lists embedded songs without reading every chart body. Startup reads only
/// the header of each .cho resource; the complete source is opened only for
/// the song selected by the user.
/// </summary>
public static class LazyBuiltInSongCatalog
{
    private const string ResourcePrefix = "Jampanion.Core.Songs.";
    private static readonly Assembly SongAssembly = typeof(DefaultSongCatalog).Assembly;
    private static readonly Lazy<IReadOnlyList<BuiltInSongMetadata>> Metadata = new(LoadMetadata);

    public static IReadOnlyList<BuiltInSongMetadata> All => Metadata.Value;

    public static string ReadSource(BuiltInSongMetadata song)
    {
        ArgumentNullException.ThrowIfNull(song);
        using var stream = SongAssembly.GetManifestResourceStream(song.ResourceName)
            ?? throw new InvalidOperationException($"Embedded song resource was not found: {song.ResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<BuiltInSongMetadata> LoadMetadata() =>
        SongAssembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                           name.EndsWith(".cho", StringComparison.OrdinalIgnoreCase))
            .Select(ReadMetadata)
            .OrderBy(song => string.Equals(song.Id, "autumn-leaves", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(song => song.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static BuiltInSongMetadata ReadMetadata(string resourceName)
    {
        var fileName = resourceName[ResourcePrefix.Length..];
        var title = Path.GetFileNameWithoutExtension(fileName);
        var id = WebSongDocument.CreateId(title);

        using var stream = SongAssembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded song resource was not found: {resourceName}");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("{start_of_grid", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("{sog", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (TryReadDirective(line, "title", out var titleValue) ||
                TryReadDirective(line, "t", out titleValue))
            {
                title = titleValue;
            }
            else if (TryReadDirective(line, "x-ai-jam-id", out var idValue))
            {
                id = WebSongDocument.CreateId(idValue);
            }
        }

        return new BuiltInSongMetadata(id, title, fileName, resourceName);
    }

    private static bool TryReadDirective(string line, string name, out string value)
    {
        value = string.Empty;
        if (line.Length < name.Length + 3 || line[0] != '{' || line[^1] != '}')
        {
            return false;
        }

        var separator = line.IndexOf(':');
        if (separator <= 1 || !line[1..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = line[(separator + 1)..^1].Trim();
        return value.Length > 0;
    }
}

public sealed record BuiltInSongMetadata(string Id, string Title, string FileName, string ResourceName);
