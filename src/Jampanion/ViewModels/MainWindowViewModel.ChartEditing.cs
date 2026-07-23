using Jampanion.Core.Music;

namespace Jampanion.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool EnsureChartEditingAvailable()
    {
        if (_playbackController.IsRunning)
        {
            StatusText = "Stop the session before editing chords or rehearsal marks.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SelectedTune.FilePath))
        {
            StatusText = "Save or import this tune as a .cho file before editing the chart.";
            return false;
        }

        return true;
    }

    public bool TryResolveEditableBar(
        ChordSheetRowViewModel row,
        ChordSheetCellViewModel cell,
        out int barIndex,
        out bool endingForm)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(cell);
        endingForm = CodaRows.Contains(row);
        if (!endingForm)
        {
            barIndex = cell.DisplayIndex;
            return barIndex >= 0 && barIndex < _activeTune.Bars.Count;
        }

        if (_activeTune.CodaStartIndex is not int codaStartIndex)
        {
            barIndex = -1;
            return false;
        }

        barIndex = _chordSheetUsesEndingForm
            ? cell.DisplayIndex
            : codaStartIndex + cell.DisplayIndex;
        return barIndex >= 0 && barIndex < _activeTune.EndingFormBars.Count;
    }

    public bool CanEditRehearsalMark(ChordSheetRowViewModel row) =>
        row.Cells.Count > 0;

    public int BeatsPerBarForChartEditing => _activeTune.BeatsPerBar;

    public int? GetChordInsertionBeat(
        ChordSheetCellViewModel cell,
        ChordSheetChordViewModel chord,
        double pointerX,
        double actualWidth)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(chord);
        if (actualWidth <= 0d || pointerX < actualWidth / 2d)
        {
            return null;
        }

        var nextStartBeat = chord.Index + 1 < cell.Chords.Count
            ? cell.Chords[chord.Index + 1].StartBeat
            : _activeTune.BeatsPerBar;
        var span = nextStartBeat - chord.StartBeat;
        if (span <= 1)
        {
            return null;
        }

        return chord.StartBeat + (span + 1) / 2;
    }

    public string GetEditableChordSymbol(int barIndex, int chordIndex, bool endingForm)
    {
        var bars = endingForm ? _activeTune.EndingFormBars : _activeTune.Bars;
        if (barIndex < 0 || barIndex >= bars.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(barIndex));
        }

        var changes = bars[barIndex].ChordChanges;
        if (chordIndex < 0 || chordIndex >= changes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(chordIndex));
        }

        return changes[chordIndex].Chord.IsNoChord ? "N.C." : changes[chordIndex].Chord.Symbol;
    }

    public bool InsertChord(int barIndex, int startBeat, bool endingForm, string chordSymbol)
    {
        if (!EnsureChartEditingAvailable())
        {
            return false;
        }

        try
        {
            var sourceSymbol = ConvertDisplayedChordToSource(chordSymbol);
            var updatedTune = _songLibraryService.InsertChordInMemory(
                SelectedTune.Tune,
                barIndex,
                startBeat,
                sourceSymbol,
                endingForm);
            ReplaceSelectedTuneAfterChartEdit(updatedTune);
            StatusText = $"Chord {ChordSymbolDisplay.Format(chordSymbol.Trim())} added at beat {startBeat + 1}. Click CHORD SHEET Save to update the .cho file.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or ChordProSongParseException)
        {
            StatusText = $"Could not add the chord: {ex.Message}";
            return false;
        }
    }

    public bool EditChord(int barIndex, int chordIndex, bool endingForm, string chordSymbol)
    {
        if (!EnsureChartEditingAvailable())
        {
            return false;
        }

        try
        {
            var deleting = string.IsNullOrWhiteSpace(chordSymbol);
            var sourceSymbol = deleting ? string.Empty : ConvertDisplayedChordToSource(chordSymbol);
            var updatedTune = _songLibraryService.ReplaceChordInMemory(
                SelectedTune.Tune,
                barIndex,
                chordIndex,
                sourceSymbol,
                endingForm);
            ReplaceSelectedTuneAfterChartEdit(updatedTune);
            StatusText = deleting
                ? "Chord change removed; the adjacent chord now continues through that region. Click CHORD SHEET Save to update the .cho file."
                : $"Chord changed to {ChordSymbolDisplay.Format(chordSymbol.Trim())}. Click CHORD SHEET Save to update the .cho file.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or ChordProSongParseException)
        {
            StatusText = $"Could not edit the chord: {ex.Message}";
            return false;
        }
    }

    public bool EditRehearsalMark(
        int barIndex,
        bool endingForm,
        string existingLabel,
        string newLabel)
    {
        if (!EnsureChartEditingAvailable())
        {
            return false;
        }

        var normalizedExisting = existingLabel.Trim();
        var normalizedNew = newLabel.Trim().Trim('[', ']', ' ');
        if (normalizedExisting.Length == 0 && normalizedNew.Length == 0)
        {
            StatusText = "No rehearsal mark was added.";
            return true;
        }

        try
        {
            AccompanimentStyle? inheritedStyle = null;
            if (normalizedExisting.Length > 0 &&
                SelectedTune.Tune.SectionStyles.TryGetValue(normalizedExisting, out var assignedStyle))
            {
                inheritedStyle = assignedStyle;
            }

            var updatedTune = _songLibraryService.SetRehearsalMarkInMemory(
                SelectedTune.Tune,
                barIndex,
                normalizedNew,
                endingForm);

            var labelChanged = !string.Equals(
                normalizedExisting,
                normalizedNew,
                StringComparison.OrdinalIgnoreCase);
            if (labelChanged &&
                normalizedNew.Length > 0 &&
                inheritedStyle is AccompanimentStyle style)
            {
                updatedTune = updatedTune.WithSectionStyle(normalizedNew, style);
            }

            if (labelChanged &&
                normalizedExisting.Length > 0 &&
                inheritedStyle is not null)
            {
                var allBars = updatedTune.HasSeparateEndingForm
                    ? updatedTune.Bars.Concat(updatedTune.EndingFormBars)
                    : updatedTune.Bars;
                var oldMarkStillExists = allBars.Any(bar =>
                    string.Equals(bar.Section, normalizedExisting, StringComparison.OrdinalIgnoreCase));
                if (!oldMarkStillExists)
                {
                    updatedTune = updatedTune.WithSectionStyle(normalizedExisting, null);
                }
            }

            ReplaceSelectedTuneAfterChartEdit(updatedTune);
            StatusText = normalizedNew.Length == 0
                ? $"Rehearsal mark {normalizedExisting} removed. Click CHORD SHEET Save to update the .cho file."
                : normalizedExisting.Length == 0
                    ? $"Rehearsal mark {normalizedNew} added. Click CHORD SHEET Save to update the .cho file."
                    : $"Rehearsal mark {normalizedExisting} changed to {normalizedNew}. Click CHORD SHEET Save to update the .cho file.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or ChordProSongParseException)
        {
            StatusText = $"Could not edit the rehearsal mark: {ex.Message}";
            return false;
        }
    }

    private void ApplyAccidentalSpellingChange()
    {
        RebuildKeyOptions(preserveCurrentTarget: true);
        if (_playbackController.IsRunning)
        {
            // Accidental spelling is display-only. Keep the running playback plan
            // untouched, rebuild only the tune used by the chart and status text.
            _activeTune = ResolveSelectedTune();
            BuildChordSheetRows(_chordSheetUsesEndingForm);
            RefreshPlaybackSnapshot();
            OnPropertyChanged(nameof(KeyText));
            OnPropertyChanged(nameof(DefaultKeyText));
            OnPropertyChanged(nameof(SelectedKeyOption));
            StatusText = $"Accidental spelling changed to {_selectedAccidentalOption.DisplayName}; playback continues.";
            OnPropertyChanged();
            return;
        }

        ApplySelectedStyleToPlayback();
        RefreshTuneDetails(clearPreview: true);
        StatusText = $"Accidental spelling set to {_selectedAccidentalOption.DisplayName}.";
        OnPropertyChanged();
    }

    private string FormatChordForCurrentAccidental(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol == "-")
        {
            return "-";
        }

        try
        {
            var preferFlats = _selectedAccidentalOption.PreferFlats ??
                TuneTransposer.GetAutoPreferFlats(_activeTune);
            return ChordSymbolDisplay.Format(TuneTransposer.RespellChordSymbol(symbol, preferFlats));
        }
        catch (FormatException)
        {
            return ChordSymbolDisplay.Format(symbol);
        }
    }

    private string ConvertDisplayedChordToSource(string symbol)
    {
        var normalized = symbol.Trim()
            .Replace('♯', '#')
            .Replace('♭', 'b');
        if (normalized is "N.C." or "N.C" or "NC")
        {
            return "N.C.";
        }

        _ = ChordSymbolParser.Parse(normalized);
        var displayedKey = TuneTransposer.GetKeyInfo(_activeTune);
        var sourceKey = TuneTransposer.GetKeyInfo(SelectedTune.Tune);
        var semitones = SignedPitchDistance(displayedKey.PitchClass, sourceKey.PitchClass);
        var preferFlats = TuneTransposer.GetAutoPreferFlats(SelectedTune.Tune);
        return TuneTransposer.TransposeChordSymbol(normalized, semitones, preferFlats);
    }

    public void SaveChartChanges()
    {
        if (_playbackController.IsRunning)
        {
            StatusText = "Stop the session before saving the chord sheet.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedTune.FilePath))
        {
            StatusText = "This tune is not backed by an editable .cho file.";
            return;
        }

        if (!SelectedTune.HasUnsavedChartChanges)
        {
            StatusText = "There are no unsaved chord-sheet changes.";
            NotifySaveAvailability();
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedTune.SourceFileFingerprint))
        {
            StatusText = "Refresh the song before saving the chord sheet.";
            return;
        }

        try
        {
            var savedTune = _songLibraryService.SaveChartChanges(
                SelectedTune.FilePath,
                SelectedTune.Tune,
                SelectedTune.SourceFileFingerprint);
            var savedFingerprint = _songLibraryService.GetFileFingerprint(
                SelectedTune.FilePath);
            _selectedTune.AcceptChartSave(
                savedTune,
                savedFingerprint);

            RebuildStyleOptions(preserveSelection: true);
            RebuildKeyOptions(preserveCurrentTarget: true);
            ApplySelectedStyleToPlayback();
            RefreshTuneDetails(clearPreview: false);
            OnPropertyChanged(nameof(SelectedTune));
            NotifySaveAvailability();
            StatusText = "Chord-sheet changes saved to the .cho file.";
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or
            FormatException or ChordProSongParseException)
        {
            StatusText = $"Could not save the chord sheet: {ex.Message}";
        }
    }

    public void SaveSongSettings()
    {
        if (_playbackController.IsRunning)
        {
            StatusText = "Stop the session before saving song settings.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedTune.FilePath))
        {
            StatusText = "This tune is not backed by an editable .cho file.";
            return;
        }

        if (!IsSongSaveEnabled)
        {
            StatusText = "There are no unsaved song-setting changes.";
            NotifySaveAvailability();
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedTune.SourceFileFingerprint))
        {
            StatusText = "Refresh the song before saving song settings.";
            return;
        }

        try
        {
            var style = _selectedStyleOption.OverrideStyle ??
                SelectedTune.Tune.AccompanimentStyle;
            var targetKey = _selectedKeyOption.DisplayName;
            var preferFlats = _selectedAccidentalOption.PreferFlats;

            var updatedWorkingTune =
                _songLibraryService.ApplySongSettingsInMemory(
                    SelectedTune.Tune,
                    TempoBpm,
                    style,
                    targetKey,
                    preferFlats);

            var persistedSavedTune = _songLibraryService.SaveSongSettings(
                SelectedTune.FilePath,
                TempoBpm,
                style,
                targetKey,
                preferFlats,
                SelectedTune.SourceFileFingerprint);
            var savedFingerprint = _songLibraryService.GetFileFingerprint(
                SelectedTune.FilePath);

            // The chart baseline comes from the file that was actually saved,
            // while the working tune retains any unsaved chart draft.
            _selectedTune.AcceptSongSettingsSave(
                updatedWorkingTune,
                persistedSavedTune,
                savedFingerprint);

            RebuildStyleOptions(preserveSelection: false);
            RebuildKeyOptions(preserveCurrentTarget: true);
            ApplySelectedStyleToPlayback();
            RefreshTuneDetails(clearPreview: false);
            CaptureSongSettingsBaseline();
            OnPropertyChanged(nameof(SelectedTune));
            NotifySaveAvailability();
            StatusText =
                "Tempo, style, key, and chord spelling saved to the .cho file.";
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or
            FormatException or ChordProSongParseException)
        {
            StatusText = $"Could not save the song settings: {ex.Message}";
        }
    }

    private void ReplaceSelectedTuneAfterChartEdit(TuneForm updatedTune)
    {
        _selectedTune.LoadDraft(updatedTune);
        RebuildStyleOptions(preserveSelection: true);
        RebuildKeyOptions(preserveCurrentTarget: true);
        ApplySelectedStyleToPlayback();
        RefreshTuneDetails(clearPreview: false);
        OnPropertyChanged(nameof(SelectedTune));
        NotifySaveAvailability();
    }

    private static int SignedPitchDistance(int source, int target)
    {
        var distance = (target - source + 12) % 12;
        return distance > 6 ? distance - 12 : distance;
    }
}
