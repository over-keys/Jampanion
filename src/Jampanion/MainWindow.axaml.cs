using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jampanion.Core.Music;
using Jampanion.ViewModels;

namespace Jampanion;

public sealed partial class MainWindow : Window
{
    private TextBox? _inlineChartEditor;
    private Grid? _inlineChartEditorHost;
    private Control? _inlineChartDisplay;
    private Control? _inlineChartFocusAnchor;
    private Func<string, bool>? _inlineChartCommit;
    private bool _inlineChartDisplayHidden;
    private bool _inlineChartFinishing;
    private bool _inlineChartEmptyCancels;
    private bool _inlineChartCloseAfterEmptyCommitFailure;

    private void MainWindow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_inlineChartFinishing ||
            _inlineChartEditor is not TextBox editor ||
            IsWithinInlineEditor(e, editor))
        {
            return;
        }

        // LostFocus is not raised when a user clicks a non-focusable part of the
        // chart. Catch the pointer before focus routing so every outside click
        // finishes the edit and the removed editor cannot return focus to SONG.
        CloseSongSearchDropDown();
        if (!CommitInlineChartEdit(restoreChartFocus: true))
        {
            // Invalid input stays in place. Do not let the attempted click
            // activate another control or the SONG search box.
            e.Handled = true;
        }
    }

    private static bool IsWithinInlineEditor(PointerPressedEventArgs e, TextBox editor)
    {
        var position = e.GetPosition(editor);
        return position.X >= 0d &&
               position.Y >= 0d &&
               position.X <= editor.Bounds.Width &&
               position.Y <= editor.Bounds.Height;
    }
    private int _currentChordSheetRow;
    private AudioSettingsWindow? _audioSettingsWindow;
    private bool _closed;
    private bool _initialChordSheetViewportPending;

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

        LayoutUpdated -= MainWindow_LayoutUpdated;

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

        // The first SizeChanged event can occur before the ViewModel is
        // assigned, leaving the chord cells at their fallback 158px width.
        // Reapply the viewport once Avalonia has completed the first layout;
        // later resizes continue to use the normal SizeChanged handler.
        _initialChordSheetViewportPending = true;
        LayoutUpdated += MainWindow_LayoutUpdated;
        Dispatcher.UIThread.Post(
            ApplyInitialChordSheetViewport,
            DispatcherPriority.Background);
    }

    private void MainWindow_LayoutUpdated(object? sender, EventArgs e) =>
        ApplyInitialChordSheetViewport();

    private void ApplyInitialChordSheetViewport()
    {
        if (!_initialChordSheetViewportPending ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var scrollViewer = this.FindControl<ScrollViewer>("ChordSheetScrollViewer");
        if (scrollViewer is null || scrollViewer.Bounds.Width <= 0)
        {
            return;
        }

        _initialChordSheetViewportPending = false;
        LayoutUpdated -= MainWindow_LayoutUpdated;
        viewModel.SetChordSheetViewportWidth(scrollViewer.Bounds.Width);
        ScheduleChordSheetScroll(_currentChordSheetRow);
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

    private void InlineChordSymbol_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is TextBox)
        {
            return;
        }

        if (sender is not Border
            {
                DataContext: ChordSheetChordViewModel chord,
                Child: Grid editorHost
            } control ||
            e.ClickCount != 2 ||
            !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        e.Handled = true;
        CloseSongSearchDropDown();

        if (_inlineChartEditor is not null)
        {
            _ = CommitInlineChartEdit(restoreChartFocus: false);
            return;
        }

        var ancestors = control.GetVisualAncestors().OfType<Control>().ToArray();
        var cell = ancestors.Select(item => item.DataContext).OfType<ChordSheetCellViewModel>().FirstOrDefault();
        var row = ancestors.Select(item => item.DataContext).OfType<ChordSheetRowViewModel>().FirstOrDefault();
        if (cell is null || row is null ||
            !viewModel.EnsureChartEditingAvailable() ||
            !viewModel.TryResolveEditableBar(row, cell, out var barIndex, out var endingForm))
        {
            FocusChartSurface(control);
            return;
        }

        var display = editorHost.Children.OfType<TextBlock>().FirstOrDefault();
        if (display is null)
        {
            FocusChartSurface(control);
            return;
        }

        var point = e.GetCurrentPoint(control);
        var insertionBeat = e.Source is TextBlock
            ? null
            : viewModel.GetChordInsertionBeat(
                cell,
                chord,
                point.Position.X,
                control.Bounds.Width);
        if (insertionBeat is int beat)
        {
            var nextStartBeat = chord.Index + 1 < cell.Chords.Count
                ? cell.Chords[chord.Index + 1].StartBeat
                : viewModel.BeatsPerBarForChartEditing;
            var span = Math.Max(1, nextStartBeat - chord.StartBeat);
            var leftFraction = Math.Clamp(
                (beat - chord.StartBeat) / (double)span,
                0d,
                1d);
            BeginInlineChartEdit(
                editorHost,
                display,
                control,
                string.Empty,
                $"{beat + 1}",
                new Thickness(control.Bounds.Width * leftFraction, 0, 0, 0),
                TextAlignment.Left,
                chord.FontSize,
                hideDisplay: false,
                value => viewModel.InsertChord(barIndex, beat, endingForm, value),
                editorWidth: Math.Clamp(
                    control.Bounds.Width * (1d - leftFraction) - 2d,
                    28d,
                    88d),
                editorHorizontalAlignment: HorizontalAlignment.Left,
                emptyCancels: true);
            return;
        }

        var initialValue = viewModel.GetEditableChordSymbol(barIndex, chord.Index, endingForm);
        BeginInlineChartEdit(
            editorHost,
            display,
            control,
            initialValue,
            "Chord",
            new Thickness(0),
            TextAlignment.Left,
            chord.FontSize,
            hideDisplay: true,
            value => viewModel.EditChord(barIndex, chord.Index, endingForm, value),
            closeAfterEmptyCommitFailure: cell.Chords.Count > 1);
    }

    private void InlineRehearsalMarkArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is TextBox)
        {
            return;
        }

        if (sender is not Border
            {
                DataContext: ChordSheetRowViewModel row,
                Child: Grid editorHost
            } control ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (point.Properties.IsRightButtonPressed)
        {
            if (_inlineChartEditor is not null &&
                !CommitInlineChartEdit(restoreChartFocus: false))
            {
                e.Handled = true;
                return;
            }

            if (row.CanEditSectionStyle && viewModel.EnsureChartEditingAvailable())
            {
                CloseSongSearchDropDown();
                control.Focusable = true;
                _ = control.Focus();
                OpenSectionStyleMenu(control, row, viewModel);
            }

            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || e.ClickCount != 2)
        {
            return;
        }

        e.Handled = true;
        CloseSongSearchDropDown();

        if (_inlineChartEditor is not null)
        {
            _ = CommitInlineChartEdit(restoreChartFocus: false);
            return;
        }

        if (!viewModel.EnsureChartEditingAvailable() ||
            !viewModel.CanEditRehearsalMark(row) ||
            row.Cells.Count == 0 ||
            !viewModel.TryResolveEditableBar(row, row.Cells[0], out var barIndex, out var endingForm))
        {
            FocusChartSurface(control);
            return;
        }

        var display = editorHost.Children.OfType<StackPanel>().FirstOrDefault();
        if (display is null)
        {
            FocusChartSurface(control);
            return;
        }

        var existingLabel = row.SectionStyleKey;
        BeginInlineChartEdit(
            editorHost,
            display,
            control,
            existingLabel,
            "Mark",
            new Thickness(0, 1, 0, 1),
            TextAlignment.Center,
            row.SectionLabelFontSize,
            hideDisplay: true,
            value => viewModel.EditRehearsalMark(barIndex, endingForm, existingLabel, value),
            editorWidth: 49d,
            editorHeight: 40d,
            editorPadding: new Thickness(1, 0),
            editorHorizontalAlignment: HorizontalAlignment.Right);
    }

    private void BeginInlineChartEdit(
        Grid editorHost,
        Control display,
        Control focusAnchor,
        string initialValue,
        string placeholder,
        Thickness margin,
        TextAlignment textAlignment,
        double fontSize,
        bool hideDisplay,
        Func<string, bool> commit,
        double? editorWidth = null,
        double? editorHeight = null,
        Thickness? editorPadding = null,
        HorizontalAlignment? editorHorizontalAlignment = null,
        bool emptyCancels = false,
        bool closeAfterEmptyCommitFailure = false)
    {
        CancelInlineChartEdit(restoreChartFocus: false);
        CloseSongSearchDropDown();
        focusAnchor.Focusable = true;
        _ = focusAnchor.Focus();

        var editor = new TextBox
        {
            Text = initialValue,
            PlaceholderText = placeholder,
            Margin = margin,
            Padding = editorPadding ?? new Thickness(4, 0),
            HorizontalAlignment = editorHorizontalAlignment ??
                (editorWidth is null ? HorizontalAlignment.Stretch : HorizontalAlignment.Center),
            VerticalAlignment = editorHeight is null ? VerticalAlignment.Stretch : VerticalAlignment.Center,
            MinWidth = 0d,
            MinHeight = 0d,
            Width = editorWidth ?? double.NaN,
            Height = editorHeight ?? double.NaN,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextAlignment = textAlignment,
            FontSize = Math.Max(10d, fontSize),
            AcceptsReturn = false,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x0B, 0x6E, 0x69)),
            BorderThickness = new Thickness(1.5)
        };

        _inlineChartEditor = editor;
        _inlineChartEditorHost = editorHost;
        _inlineChartDisplay = display;
        _inlineChartFocusAnchor = focusAnchor;
        _inlineChartCommit = commit;
        _inlineChartDisplayHidden = hideDisplay;
        _inlineChartEmptyCancels = emptyCancels;
        _inlineChartCloseAfterEmptyCommitFailure = closeAfterEmptyCommitFailure;
        if (hideDisplay)
        {
            display.IsVisible = false;
        }

        editorHost.Children.Add(editor);
        editor.KeyDown += InlineChartEditor_KeyDown;
        editor.LostFocus += InlineChartEditor_LostFocus;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_inlineChartEditor, editor))
            {
                return;
            }

            editor.Focus();
            if (initialValue.Length > 0)
            {
                editor.SelectAll();
            }
            else
            {
                editor.CaretIndex = 0;
            }
        }, DispatcherPriority.Input);
    }

    private void InlineChartEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(sender, _inlineChartEditor))
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            _ = CommitInlineChartEdit(restoreChartFocus: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelInlineChartEdit(restoreChartFocus: true);
            e.Handled = true;
        }
    }

    private void InlineChartEditor_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_inlineChartFinishing ||
            sender is not TextBox editor ||
            !ReferenceEquals(editor, _inlineChartEditor))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_inlineChartFinishing &&
                ReferenceEquals(editor, _inlineChartEditor) &&
                !editor.IsFocused)
            {
                _ = CommitInlineChartEdit(restoreChartFocus: false);
            }
        }, DispatcherPriority.Background);
    }

    private bool CommitInlineChartEdit(bool restoreChartFocus)
    {
        if (_inlineChartEditor is not TextBox editor ||
            _inlineChartCommit is not Func<string, bool> commit)
        {
            return true;
        }

        var value = editor.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) && _inlineChartEmptyCancels)
        {
            CancelInlineChartEdit(restoreChartFocus);
            return true;
        }

        _inlineChartFinishing = true;
        var focusAnchor = _inlineChartFocusAnchor;
        bool succeeded;
        try
        {
            succeeded = commit(value);
        }
        catch
        {
            _inlineChartFinishing = false;
            throw;
        }

        if (!succeeded)
        {
            if (string.IsNullOrWhiteSpace(value) && _inlineChartCloseAfterEmptyCommitFailure)
            {
                // Keep the status text from the failed delete, but do not trap
                // the user in an editor that can no longer make progress.
                ClearInlineChartEditor();
                _inlineChartFinishing = false;
                if (restoreChartFocus)
                {
                    FocusChartSurface(focusAnchor);
                }
                return true;
            }

            editor.BorderBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18));
            editor.BorderThickness = new Thickness(2);
            _inlineChartFinishing = false;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_inlineChartEditor, editor))
                {
                    editor.Focus();
                    editor.SelectAll();
                }
            }, DispatcherPriority.Input);
            return false;
        }

        ClearInlineChartEditor();
        _inlineChartFinishing = false;
        if (restoreChartFocus)
        {
            FocusChartSurface(focusAnchor);
        }

        return true;
    }

    private void CancelInlineChartEdit(bool restoreChartFocus)
    {
        if (_inlineChartEditor is null)
        {
            if (restoreChartFocus)
            {
                FocusChartSurface(null);
            }

            return;
        }

        _inlineChartFinishing = true;
        var focusAnchor = _inlineChartFocusAnchor;
        ClearInlineChartEditor();
        _inlineChartFinishing = false;
        if (restoreChartFocus)
        {
            FocusChartSurface(focusAnchor);
        }
    }

    private void ClearInlineChartEditor()
    {
        var editor = _inlineChartEditor;
        var host = _inlineChartEditorHost;
        var display = _inlineChartDisplay;
        var displayHidden = _inlineChartDisplayHidden;
        _inlineChartEditor = null;
        _inlineChartEditorHost = null;
        _inlineChartDisplay = null;
        _inlineChartFocusAnchor = null;
        _inlineChartCommit = null;
        _inlineChartDisplayHidden = false;
        _inlineChartEmptyCancels = false;
        _inlineChartCloseAfterEmptyCommitFailure = false;

        if (editor is not null)
        {
            editor.KeyDown -= InlineChartEditor_KeyDown;
            editor.LostFocus -= InlineChartEditor_LostFocus;
            host?.Children.Remove(editor);
        }

        if (displayHidden && display is not null)
        {
            display.IsVisible = true;
        }
    }

    private void FocusChartSurface(Control? fallback)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CloseSongSearchDropDown();
            var chart = this.FindControl<ScrollViewer>("ChordSheetScrollViewer");
            if (chart is not null)
            {
                chart.Focusable = true;
                if (chart.Focus())
                {
                    return;
                }
            }

            if (fallback is not null)
            {
                fallback.Focusable = true;
                _ = fallback.Focus();
                return;
            }

            TopLevel.GetTopLevel(this)?.FocusManager.Focus(null);
        }, DispatcherPriority.Input);
    }

    private void CloseSongSearchDropDown()
    {
        if (this.FindControl<AutoCompleteBox>("TitleSearchBox") is { } titleSearch)
        {
            titleSearch.IsDropDownOpen = false;
        }
    }

    private static void OpenSectionStyleMenu(
        Control anchor,
        ChordSheetRowViewModel row,
        MainWindowViewModel viewModel)
    {
        var items = new List<object>();
        void AddStyleItem(string label, AccompanimentStyle? style)
        {
            var selected = Nullable.Equals(row.SectionOverrideStyle, style);
            var item = new MenuItem { Header = selected ? $"✓ {label}" : label };
            item.Click += (_, _) => viewModel.SetSectionStyle(row.SectionStyleKey, style);
            items.Add(item);
        }

        AddStyleItem("Use song default", null);
        items.Add(new Separator());
        if (viewModel.TimeSignatureText == "3/4")
        {
            AddStyleItem("Jazz Waltz", AccompanimentStyle.JazzWaltz);
        }
        else
        {
            AddStyleItem("Swing", AccompanimentStyle.Swing);
            AddStyleItem("Ballad", AccompanimentStyle.JazzBallad);
            AddStyleItem("Bossa Nova", AccompanimentStyle.BossaNova);
            AddStyleItem("Latin", AccompanimentStyle.AfroCubanLatin);
        }

        var menu = new ContextMenu { ItemsSource = items };
        anchor.ContextMenu = menu;
        menu.Open(anchor);
    }

    private void SectionStyleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChordSheetRowViewModel row } button ||
            DataContext is not MainWindowViewModel viewModel ||
            !row.CanEditSectionStyle)
        {
            return;
        }

        var items = new List<object>();
        void AddStyleItem(string label, AccompanimentStyle? style)
        {
            var selected = Nullable.Equals(row.SectionOverrideStyle, style);
            var item = new MenuItem { Header = selected ? $"✓ {label}" : label };
            item.Click += (_, _) => viewModel.SetSectionStyle(row.SectionStyleKey, style);
            items.Add(item);
        }

        AddStyleItem("Use song default", null);
        items.Add(new Separator());
        if (viewModel.TimeSignatureText == "3/4")
        {
            AddStyleItem("Jazz Waltz", AccompanimentStyle.JazzWaltz);
        }
        else
        {
            AddStyleItem("Swing", AccompanimentStyle.Swing);
            AddStyleItem("Ballad", AccompanimentStyle.JazzBallad);
            AddStyleItem("Bossa Nova", AccompanimentStyle.BossaNova);
            AddStyleItem("Latin", AccompanimentStyle.AfroCubanLatin);
        }

        var menu = new ContextMenu { ItemsSource = items };
        button.ContextMenu = menu;
        menu.Open(button);
        e.Handled = true;
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
        AddHandler(InputElement.PointerPressedEvent, MainWindow_PointerPressed, RoutingStrategies.Tunnel);
    }
}
