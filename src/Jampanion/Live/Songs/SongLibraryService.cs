using System.IO;
using System.Diagnostics;
using System.Text;
using Jampanion.Core.Music;

namespace Jampanion.Live.Songs;

public sealed class SongLibraryService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cho",
        ".chordpro",
        ".chopro"
    };

    private const string InitializationMarkerFileName = ".jampanion-library-initialized";
    private const string LegacyInitializationMarkerFileName = ".ai-jam-library-initialized";

    public SongLibraryService(string? libraryFolder = null)
    {
        LibraryFolder = string.IsNullOrWhiteSpace(libraryFolder)
            ? ResolveDefaultLibraryFolder()
            : Path.GetFullPath(libraryFolder);
    }

    public string LibraryFolder { get; private set; }

    private static string ResolveDefaultLibraryFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var current = Path.Combine(documents, "Jampanion", "Songs");
        var legacy = Path.Combine(documents, "AI Jam", "Songs");

        // Keep an existing library in place when upgrading from AI Jam.
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }

    public void SetLibraryFolder(string libraryFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryFolder);
        LibraryFolder = Path.GetFullPath(libraryFolder);
    }

    public void EnsureInitialized()
    {
        Directory.CreateDirectory(LibraryFolder);
        var markerPath = Path.Combine(LibraryFolder, InitializationMarkerFileName);
        var legacyMarkerPath = Path.Combine(LibraryFolder, LegacyInitializationMarkerFileName);

        foreach (var defaultSong in DefaultSongCatalog.All)
        {
            var destination = Path.Combine(LibraryFolder, defaultSong.FileName);
            if (!File.Exists(destination))
            {
                WriteNewFileSafely(destination, defaultSong.Content);
            }
        }

        if (!File.Exists(markerPath) && !File.Exists(legacyMarkerPath))
        {
            File.WriteAllText(
                markerPath,
                "Jampanion initialized this song library. Song files are plain-text ChordPro .cho files.\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public IReadOnlyList<SongFileEntry> Scan()
    {
        EnsureInitialized();
        return Directory.EnumerateFiles(LibraryFolder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .Select(LoadEntry)
            .OrderBy(entry => entry.IsValid ? 0 : 1)
            .ThenBy(entry => entry.IsValid ? entry.Tune!.Title : Path.GetFileNameWithoutExtension(entry.FilePath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public SongFileEntry LoadEntry(string path)
    {
        try
        {
            return new SongFileEntry(path, ChordProSongParser.ParseFile(path), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ChordProSongParseException or FormatException or ArgumentException)
        {
            return new SongFileEntry(path, null, ex.Message);
        }
    }

    public TuneForm SaveChordSymbol(
        string path,
        int barIndex,
        int chordIndex,
        string chordSymbol,
        bool endingForm) =>
        ChordProSongEditor.ReplaceChord(path, barIndex, chordIndex, chordSymbol, endingForm);

    public TuneForm InsertChordSymbol(
        string path,
        int barIndex,
        int startBeat,
        string chordSymbol,
        bool endingForm) =>
        ChordProSongEditor.InsertChord(path, barIndex, startBeat, chordSymbol, endingForm);

    public TuneForm SaveRehearsalMark(
        string path,
        int barIndex,
        string rehearsalMark,
        bool endingForm) =>
        ChordProSongEditor.SetRehearsalMark(path, barIndex, rehearsalMark, endingForm);

    public void SaveSectionStyle(
        string path,
        string sectionLabel,
        AccompanimentStyle? style)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionLabel);
        if (!SupportedExtensions.Contains(Path.GetExtension(path)))
        {
            throw new ArgumentException("Section styles can be saved only to a supported ChordPro song file.", nameof(path));
        }

        var original = File.ReadAllText(path);
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = original
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var hadTrailingNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (hadTrailingNewline && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var normalizedLabel = sectionLabel.Trim();
        lines.RemoveAll(line =>
            TryGetSectionStyleLabel(line, out var existingLabel) &&
            string.Equals(existingLabel, normalizedLabel, StringComparison.OrdinalIgnoreCase));

        if (style is AccompanimentStyle selectedStyle)
        {
            var insertionIndex = lines.FindIndex(line =>
                IsNamedDirective(line, "start_of_grid") ||
                IsNamedDirective(line, "sog") ||
                (!line.TrimStart().StartsWith('{') && line.Contains('|')));
            if (insertionIndex < 0)
            {
                insertionIndex = lines.Count;
            }

            lines.Insert(
                insertionIndex,
                $"{{x-jampanion-section-style: {normalizedLabel}|{AccompanimentStyleNames.StorageName(selectedStyle)}}}");
        }

        var updated = string.Join(newline, lines);
        if (hadTrailingNewline)
        {
            updated += newline;
        }

        WriteExistingFileSafely(path, updated);
    }

    public IRealProFileImportResult ImportIRealProFile(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        EnsureInitialized();

        var source = File.ReadAllText(sourcePath);
        var document = IRealProSongParser.Parse(source);
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingFiles = document.Songs
            .Select(song => new
            {
                Song = song,
                Path = GetUniquePath(GetSafeFileName(song.Title), ".cho", reservedPaths)
            })
            .ToArray();

        foreach (var pending in pendingFiles)
        {
            _ = ChordProSongParser.Parse(pending.Song.ChordProText, Path.GetFileName(pending.Path));
        }

        foreach (var pending in pendingFiles)
        {
            WriteNewFileSafely(pending.Path, pending.Song.ChordProText);
        }

        var warnings = document.Warnings
            .Concat(document.Songs.SelectMany(song => song.Warnings.Select(warning => $"{song.Title}: {warning}")))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new IRealProFileImportResult(pendingFiles.Select(file => file.Path).ToArray(), warnings);
    }

    public string CreateNewSongFile()
    {
        EnsureInitialized();
        var path = GetUniquePath("New Song", ".cho");
        const string template = """
{title: New Song}
{key: C}
{time: 4/4}
{tempo: 120}

{start_of_grid}
A | C | C | C | C |
  | C | C | C | C |
{end_of_grid}
""";
        WriteNewFileSafely(path, template);
        return path;
    }

    public void OpenSongFile(string path) => OpenWithShell(path);

    public void OpenLibraryFolder()
    {
        EnsureInitialized();
        OpenWithShell(LibraryFolder);
    }

    private static string GetSafeFileName(string title)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = new string(title
            .Trim()
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim('.', ' ');
        return safeName.Length == 0 ? "Imported Song" : safeName;
    }

    private string GetUniquePath(string baseName, string extension, ISet<string>? reservedPaths = null)
    {
        var candidate = Path.Combine(LibraryFolder, baseName + extension);
        for (var suffix = 2; File.Exists(candidate) || reservedPaths?.Contains(candidate) == true; suffix++)
        {
            candidate = Path.Combine(LibraryFolder, $"{baseName} ({suffix}){extension}");
        }

        reservedPaths?.Add(candidate);
        return candidate;
    }

    private static bool TryGetSectionStyleLabel(string line, out string label)
    {
        label = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.Length < 3 || trimmed[0] != '{' || trimmed[^1] != '}')
        {
            return false;
        }

        var inner = trimmed[1..^1];
        var separator = inner.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        var name = inner[..separator].Trim();
        if (!name.Equals("x-jampanion-section-style", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("x_jampanion_section_style", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("x-ai-jam-section-style", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("x_ai_jam_section_style", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = inner[(separator + 1)..].Trim();
        var styleSeparator = value.IndexOf('|');
        if (styleSeparator <= 0)
        {
            return false;
        }

        label = value[..styleSeparator].Trim();
        return label.Length > 0;
    }

    private static bool IsNamedDirective(string line, string expectedName)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
        {
            return false;
        }

        var inner = trimmed[1..^1];
        var separator = inner.IndexOf(':');
        var name = (separator >= 0 ? inner[..separator] : inner).Trim();
        return name.Equals(expectedName, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteExistingFileSafely(string destination, string content)
    {
        var temporaryPath = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // A stale temporary file is harmless and is never scanned as a song.
            }
        }
    }

    private static void WriteNewFileSafely(string destination, string content)
    {
        var temporaryPath = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // Another app instance completed the same create-only operation first.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // A stale temporary file is harmless and is never scanned as a song.
            }
        }
    }

    private static void OpenWithShell(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }
}
