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

    public SongFileEntry? TryLoadStartupEntry(string? preferredFileName, string? preferredTuneId)
    {
        try
        {
            EnsureInitialized();

            if (!string.IsNullOrWhiteSpace(preferredFileName))
            {
                var preferredPath = Path.Combine(LibraryFolder, Path.GetFileName(preferredFileName));
                if (File.Exists(preferredPath))
                {
                    var preferredEntry = LoadEntry(preferredPath);
                    if (preferredEntry.IsValid)
                    {
                        return preferredEntry;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(preferredTuneId))
            {
                return null;
            }

            var normalizedTuneId = NormalizeSongIdentity(preferredTuneId);
            var matchingPath = EnumerateSongPaths().FirstOrDefault(path =>
                string.Equals(
                    NormalizeSongIdentity(Path.GetFileNameWithoutExtension(path)),
                    normalizedTuneId,
                    StringComparison.Ordinal));
            if (matchingPath is null)
            {
                return null;
            }

            var matchingEntry = LoadEntry(matchingPath);
            return matchingEntry.IsValid ? matchingEntry : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
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

    public IReadOnlyList<SongFileMetadata> ScanMetadata()
    {
        EnsureInitialized();
        return EnumerateSongPaths()
            .Select(ReadMetadata)
            .OrderBy(entry => entry.IsValid ? 0 : 1)
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<SongFileEntry> Scan()
    {
        EnsureInitialized();
        return EnumerateSongPaths()
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
            var content = File.ReadAllText(path);
            var tune = ChordProSongParser.Parse(
                content,
                Path.GetFileNameWithoutExtension(path));
            return new SongFileEntry(
                path,
                tune,
                null,
                SongDraftEditor.GetContentFingerprint(content));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            ChordProSongParseException or FormatException or ArgumentException)
        {
            return new SongFileEntry(path, null, ex.Message);
        }
    }

    private SongFileMetadata ReadMetadata(string path)
    {
        var fallbackTitle = Path.GetFileNameWithoutExtension(path);
        try
        {
            var title = fallbackTitle;
            string? tuneId = null;
            using var reader = new StreamReader(path);
            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                if (!TryReadDirective(trimmed, out var name, out var value))
                {
                    if (trimmed.Contains('|'))
                    {
                        break;
                    }

                    continue;
                }

                switch (name)
                {
                    case "title":
                    case "t":
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            title = value;
                        }
                        break;
                    case "x-ai-jam-id":
                        tuneId = string.IsNullOrWhiteSpace(value) ? null : value;
                        break;
                    case "start_of_grid":
                    case "sog":
                        return new SongFileMetadata(path, title, tuneId, null);
                }
            }

            return new SongFileMetadata(path, title, tuneId, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SongFileMetadata(path, fallbackTitle, null, ex.Message);
        }
    }

    private IEnumerable<string> EnumerateSongPaths() =>
        Directory.EnumerateFiles(LibraryFolder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)));

    private static bool TryReadDirective(string line, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        if (line.Length < 2 || line[0] != '{' || line[^1] != '}')
        {
            return false;
        }

        var inner = line[1..^1];
        var separator = inner.IndexOf(':');
        name = (separator >= 0 ? inner[..separator] : inner).Trim().ToLowerInvariant();
        value = separator >= 0 ? inner[(separator + 1)..].Trim() : string.Empty;
        return name.Length > 0;
    }

    private static string NormalizeSongIdentity(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

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

    public TuneForm ReplaceChordInMemory(
        TuneForm tune,
        int barIndex,
        int chordIndex,
        string chordSymbol,
        bool endingForm) =>
        SongDraftEditor.ReplaceChord(tune, barIndex, chordIndex, chordSymbol, endingForm);

    public TuneForm InsertChordInMemory(
        TuneForm tune,
        int barIndex,
        int startBeat,
        string chordSymbol,
        bool endingForm) =>
        SongDraftEditor.InsertChord(tune, barIndex, startBeat, chordSymbol, endingForm);

    public TuneForm SetRehearsalMarkInMemory(
        TuneForm tune,
        int barIndex,
        string rehearsalMark,
        bool endingForm) =>
        SongDraftEditor.SetRehearsalMark(tune, barIndex, rehearsalMark, endingForm);

    public TuneForm ApplySongSettingsInMemory(
        TuneForm tune,
        int tempoBpm,
        AccompanimentStyle style,
        string targetKey,
        bool? preferFlats) =>
        SongDraftEditor.ApplySongSettings(tune, tempoBpm, style, targetKey, preferFlats);

    public string GetFileFingerprint(string path) =>
        SongDraftEditor.GetFileFingerprint(path);

    public string? TryGetFileFingerprint(string path)
    {
        try
        {
            return GetFileFingerprint(path);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public TuneForm SaveChartChanges(
        string path,
        TuneForm tune,
        string expectedFingerprint) =>
        SongDraftEditor.SaveChart(
            path,
            tune,
            expectedFingerprint);

    public TuneForm SaveSongSettings(
        string path,
        int tempoBpm,
        AccompanimentStyle style,
        string targetKey,
        bool? preferFlats,
        string expectedFingerprint) =>
        SongDraftEditor.SaveSongSettings(
            path,
            tempoBpm,
            style,
            targetKey,
            preferFlats,
            expectedFingerprint);

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
