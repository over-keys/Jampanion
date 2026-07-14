using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jampanion.ViewModels;

namespace Jampanion;

public sealed partial class MainWindow : Window
{
    private int _currentChordSheetRow;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainWindowViewModel();
        viewModel.ChordSheetRowChanged += ViewModel_ChordSheetRowChanged;
        DataContext = viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ChordSheetRowChanged -= ViewModel_ChordSheetRowChanged;
        }

        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
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

    private void ChordSheetScrollViewer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && e.NewSize.Width > 0)
        {
            viewModel.SetChordSheetViewportWidth(e.NewSize.Width);
            ScheduleChordSheetScroll(_currentChordSheetRow);
        }
    }

    private void TitleSearchBox_GotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not AutoCompleteBox titleSearchBox)
        {
            return;
        }

        // Preserve the proven AutoCompleteBox selection behavior; only replace
        // the old select-all focus action with a clean search field.
        Dispatcher.UIThread.Post(() =>
        {
            titleSearchBox.Text = string.Empty;
            if (titleSearchBox.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } textBox)
            {
                textBox.Text = string.Empty;
                textBox.CaretIndex = 0;
                textBox.SelectionStart = 0;
                textBox.SelectionEnd = 0;
            }
        }, DispatcherPriority.Input);
    }

    private void ViewModel_ChordSheetRowChanged(int rowIndex)
    {
        _currentChordSheetRow = Math.Max(0, rowIndex);
        ScheduleChordSheetScroll(_currentChordSheetRow);
    }

    private void ScheduleChordSheetScroll(int rowIndex)
    {
        Dispatcher.UIThread.Post(() => ScrollChordSheetToRow(rowIndex), DispatcherPriority.Background);
    }

    private void ScrollChordSheetToRow(int rowIndex)
    {
        var scrollViewer = this.FindControl<ScrollViewer>("ChordSheetScrollViewer");
        if (scrollViewer is null ||
            DataContext is not MainWindowViewModel viewModel ||
            viewModel.ChordSheetRowCount == 0 ||
            scrollViewer.Extent.Height <= scrollViewer.Viewport.Height + 1)
        {
            return;
        }

        var rowHeight = scrollViewer.Extent.Height / viewModel.ChordSheetRowCount;
        var rowTop = Math.Clamp(rowIndex, 0, viewModel.ChordSheetRowCount - 1) * rowHeight;
        var highlightCenter = rowTop + rowHeight / 2;
        var currentOffset = scrollViewer.Offset.Y;
        var safeTop = currentOffset + scrollViewer.Viewport.Height * 0.20;
        var safeBottom = currentOffset + scrollViewer.Viewport.Height * 0.80;
        if (highlightCenter >= safeTop && highlightCenter <= safeBottom)
        {
            return;
        }

        var desiredOffset = rowIndex == 0
            ? 0
            : highlightCenter - scrollViewer.Viewport.Height * 0.20;
        var maximumOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Clamp(desiredOffset, 0, maximumOffset));
    }

    private void StyleComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: StyleOption styleOption } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SelectStyleOption(styleOption);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
