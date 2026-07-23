using System.Reflection;

namespace Jampanion.Core.Music;

public sealed class DefaultSongFile
{
    private readonly Lazy<TuneForm> _tune;

    public DefaultSongFile(string fileName, string content)
    {
        FileName = fileName;
        Content = content;
        _tune = new Lazy<TuneForm>(() =>
            ChordProSongParser.Parse(Content, Path.GetFileNameWithoutExtension(FileName)));
    }

    public string FileName { get; }
    public string Content { get; }
    public TuneForm Tune => _tune.Value;
}

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
            .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DefaultSongFile LoadFile(Assembly assembly, string resourceName, string prefix)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded song resource was not found: {resourceName}");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        var fileName = resourceName[prefix.Length..];
        return new DefaultSongFile(fileName, content);
    }
}
