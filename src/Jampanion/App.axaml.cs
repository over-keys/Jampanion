using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Jampanion;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        StartupTrace.Write("Application.Initialize");
        AvaloniaXamlLoader.Load(this);
        StartupTrace.Write("Application.Initialize completed");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupTrace.Write($"Framework initialization completed; lifetime={ApplicationLifetime?.GetType().FullName ?? "null"}");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupTrace.Write("Creating MainWindow");
            desktop.MainWindow = new MainWindow();
            StartupTrace.Write("MainWindow assigned");
        }

        base.OnFrameworkInitializationCompleted();
        StartupTrace.Write("OnFrameworkInitializationCompleted returned");
    }
}
