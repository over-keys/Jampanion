using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Jampanion.ViewModels;

namespace Jampanion;

public sealed partial class AudioSettingsWindow : Window
{
    public AudioSettingsWindow()
    {
        InitializeComponent();
    }

    private async void ImportIRealProButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import iReal Pro song",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("iReal Pro files")
                {
                    Patterns = ["*.html", "*.htm", "*.txt"]
                },
                FilePickerFileTypes.All
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.ImportIRealProFile(path);
        }
    }

    private async void SelectSongLibraryFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose song folder",
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.SetSongLibraryFolder(path);
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
