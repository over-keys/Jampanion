using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Jampanion.ViewModels;
using Jampanion.Core.Music;

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

    private void ToggleNewSongEditorButton_Click(object? sender, RoutedEventArgs e)
    {
        var panel = this.FindControl<Border>("NewSongEditorPanel");
        var titleBox = this.FindControl<TextBox>("NewSongTitle");
        var barCountBox = this.FindControl<NumericUpDown>("NewSongBarCount");
        var validationText = this.FindControl<TextBlock>("NewSongValidationText");
        if (panel is null || titleBox is null || barCountBox is null)
        {
            return;
        }

        panel.IsVisible = !panel.IsVisible;
        if (!panel.IsVisible)
        {
            return;
        }

        titleBox.Text = string.Empty;
        barCountBox.Value = 32m;
        if (validationText is not null)
        {
            validationText.Text = string.Empty;
            validationText.IsVisible = false;
        }

        Dispatcher.UIThread.Post(
            () => titleBox.Focus(),
            DispatcherPriority.Input);
    }

    private void CancelNewSongButton_Click(object? sender, RoutedEventArgs e) =>
        CloseNewSongEditor();

    private void CreateNewSongButton_Click(object? sender, RoutedEventArgs e) =>
        CreateNewSongFromEditor();

    private void NewSongInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseNewSongEditor();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CreateNewSongFromEditor();
        }
    }

    private void CreateNewSongFromEditor()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var titleBox = this.FindControl<TextBox>("NewSongTitle");
        var barCountBox = this.FindControl<NumericUpDown>("NewSongBarCount");
        var validationText = this.FindControl<TextBlock>("NewSongValidationText");
        if (titleBox is null || barCountBox is null)
        {
            return;
        }

        try
        {
            var title = NewSongTemplate.NormalizeTitle(titleBox.Text ?? string.Empty);
            var value = barCountBox.Value ?? 32m;
            if (decimal.Truncate(value) != value)
            {
                throw new ArgumentException("Enter a whole number of bars.");
            }

            var barCount = checked((int)value);
            NewSongTemplate.ValidateBarCount(barCount);
            if (viewModel.CreateNewSong(title, barCount))
            {
                CloseNewSongEditor();
                return;
            }

            ShowNewSongValidation(
                string.IsNullOrWhiteSpace(viewModel.StatusText)
                    ? "The song could not be created."
                    : viewModel.StatusText);
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException)
        {
            ShowNewSongValidation(exception.Message);
        }
    }

    private void ShowNewSongValidation(string message)
    {
        var validationText = this.FindControl<TextBlock>("NewSongValidationText");
        var titleBox = this.FindControl<TextBox>("NewSongTitle");
        if (validationText is not null)
        {
            validationText.Text = message;
            validationText.IsVisible = true;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                titleBox?.Focus();
                titleBox?.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void CloseNewSongEditor()
    {
        if (this.FindControl<Border>("NewSongEditorPanel") is { } panel)
        {
            panel.IsVisible = false;
        }
        if (this.FindControl<TextBox>("NewSongTitle") is { } titleBox)
        {
            titleBox.Text = string.Empty;
        }
        if (this.FindControl<NumericUpDown>("NewSongBarCount") is { } barCountBox)
        {
            barCountBox.Value = 32m;
        }
        if (this.FindControl<TextBlock>("NewSongValidationText") is { } validationText)
        {
            validationText.Text = string.Empty;
            validationText.IsVisible = false;
        }
    }

    private async void ImportChordProButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        CloseNewSongEditor();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import ChordPro song",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("ChordPro song files")
                {
                    Patterns = ["*.cho", "*.chordpro", "*.chopro"]
                },
                FilePickerFileTypes.All
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.ImportChordProFile(path);
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
