using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private AudioSettingsWindow? _audioSettingsWindow;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, MainWindow_KeyDown, RoutingStrategies.Tunnel);
        // Let the native window paint before constructing the ViewModel. Its
        // startup work includes song parsing and optional MIDI setup; doing it
        // in the constructor can leave macOS showing only a bouncing Dock icon
        // when a native service is slow or unavailable.
        Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(
                InitializeViewModelAfterFirstPaint,
                DispatcherPriority.Background);
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _audioSettingsWindow?.Close();
        _audioSettingsWindow = null;

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

    private void InitializeViewModelAfterFirstPaint()
    {
        if (_closed || DataContext is not null)
        {
            return;
        }

        var viewModel = new MainWindowViewModel();
        viewModel.ChordSheetRowChanged += ViewModel_ChordSheetRowChanged;
        DataContext = viewModel;
        viewModel.StartBackgroundInitialization();
    }

    private void OpenAudioSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.IsAsioSettingsVisible)
        {
            return;
        }

        if (_audioSettingsWindow is not null)
        {
            _audioSettingsWindow.Activate();
            return;
        }

        _audioSettingsWindow = new AudioSettingsWindow
        {
            DataContext = viewModel
        };
        _audioSettingsWindow.Closed += (_, _) => _audioSettingsWindow = null;
        _audioSettingsWindow.Show(this);
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
            ClearTitleSearchBox(titleSearchBox, openSuggestions: true);
        }, DispatcherPriority.Input);
    }

    private void TitleSearchBox_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is AutoCompleteBox titleSearchBox &&
            e.Source is TextBox &&
            !string.IsNullOrWhiteSpace(titleSearchBox.Text))
        {
            // This click can happen while the box is already focused, so the
            // GotFocus handler will not run again to open the empty-list view.
            Dispatcher.UIThread.Post(
                () => ClearTitleSearchBox(titleSearchBox, openSuggestions: true),
                DispatcherPriority.Input);
        }
    }

    private void TitleSearchBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not AutoCompleteBox titleSearchBox ||
            titleSearchBox.SelectedItem is not TuneOption tune)
        {
            return;
        }

        // AutoCompleteBox clears its editable text during the focus/search cycle.
        // Restore the chosen title after the binding update has settled, then a
        // later click can intentionally clear it for the next search.
        Dispatcher.UIThread.Post(
            () =>
            {
                SetTitleSearchText(titleSearchBox, tune.Title);
                TopLevel.GetTopLevel(titleSearchBox)?.FocusManager.Focus(null);
            },
            DispatcherPriority.Background);
    }

    private static void ClearTitleSearchBox(AutoCompleteBox titleSearchBox, bool openSuggestions = false)
    {
        SetTitleSearchText(titleSearchBox, string.Empty);
        if (openSuggestions)
        {
            titleSearchBox.IsDropDownOpen = true;
        }
    }

    private static void SetTitleSearchText(AutoCompleteBox titleSearchBox, string text)
    {
        titleSearchBox.Text = text;
        if (titleSearchBox.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } textBox)
        {
            textBox.Text = text;
            textBox.CaretIndex = text.Length;
            textBox.SelectionStart = text.Length;
            textBox.SelectionEnd = text.Length;
        }
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space &&
            e.Source is not TextBox &&
            e.Source is not AutoCompleteBox &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ToggleSessionOrQueueHead();
            e.Handled = true;
        }
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
