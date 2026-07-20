using Avalonia;
using System.Runtime.InteropServices;

namespace Jampanion;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupTrace.Write($"Main entered; arch={RuntimeInformation.ProcessArchitecture}; os={RuntimeInformation.OSDescription}");
        try
        {
            StartupTrace.Write("Building Avalonia app");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            StartupTrace.Write("Classic desktop lifetime returned");
        }
        catch (Exception ex)
        {
            StartupTrace.Write($"Unhandled startup exception: {ex}");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

internal static class StartupTrace
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "Logs",
        "Jampanion",
        "startup.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(
                    LogPath,
                    $"{DateTimeOffset.Now:O} [pid={Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Startup tracing must never prevent the application from opening.
        }
    }
}
