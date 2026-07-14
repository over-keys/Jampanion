using System.Reflection;

namespace Jampanion.Core.Music;

public sealed record DefaultSongFile(string FileName, string Content, TuneForm Tune);

public static class DefaultSongCatalog
{
    private static readonly Lazy<IReadOnlyList<DefaultSongFile>> Files = new(LoadFiles);

    public static IReadOnlyList<DefaultSongFile> All => Files.Value;

    private static IReadOnlyList<DefaultSongFile> LoadFiles()
    {
        var assembly = typeof(DefaultSongCatalog).Assembly;
        const string prefix = "Jampanion.Core.Songs.";
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".cho", StringComparison.OrdinalIgnoreCase))
            .Select(name => LoadFile(assembly, name, prefix))
            .OrderBy(file => file.Tune.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DefaultSongFile LoadFile(Assembly assembly, string resourceName, string prefix)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded song resource was not found: {resourceName}");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        var fileName = resourceName[prefix.Length..];
        var tune = ChordProSongParser.Parse(content, Path.GetFileNameWithoutExtension(fileName));
        return new DefaultSongFile(fileName, content, tune);
    }
}
