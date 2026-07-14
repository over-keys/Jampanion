using System.IO;
using System.Text.Json;

namespace Jampanion.Live.Settings;

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jampanion",
        "settings.json");

    private static string LegacySettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jampanion",
        "MidiProbe",
        "settings.json");

    private static string OldLegacySettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JazzSession",
        "MidiProbe",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = File.Exists(SettingsPath)
                ? SettingsPath
                : File.Exists(LegacySettingsPath) ? LegacySettingsPath : OldLegacySettingsPath;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static bool TrySave(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
