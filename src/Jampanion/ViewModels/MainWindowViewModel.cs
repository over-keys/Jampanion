using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;
using Jampanion.Live.Audio;
using Jampanion.Live.Midi;
using Jampanion.Live.Playback;
using Jampanion.Live.Settings;
using Jampanion.Live.Songs;

namespace Jampanion.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private const string NoMidiInputName = "(no MIDI input)";
    private readonly MidiPortService _midiPortService;
    private readonly HumanPerformanceAnalyzer _performanceAnalyzer = new();
    private readonly HeadOutDetector _headOutDetector = new();
    private readonly SongLibraryService _songLibraryService;
    private readonly AppSettings _settings;
    private readonly SessionPlaybackController _playbackController;
    private readonly DispatcherTimer _snapshotTimer;
    private TuneOption _selectedTune;
    private TuneForm _activeTune;
    private StyleOption _selectedStyleOption;
    private KeyOption _selectedKeyOption = new("C", 0, false, false);
    private AccidentalOption _selectedAccidentalOption = AccidentalOption.Auto;
    private string? _selectedInputPort = NoMidiInputName;
    private string? _selectedOutputPort = MidiPortService.BuiltInTrioOutputName;
    private string? _preferredInputPort;
    private string? _preferredOutputPort;
    private bool _suppressMidiPortPersistence;
    private int _tempoBpm;
    private string _statusText = "Ready.";
    private string _previewSummary = "No preview generated yet.";
    private string _liveStatusText = "MIDI is not open.";
    private string _playbackStatusText = "Stopped.";
    private string _arrangementStageText = "Stopped";
    private string _themeReturnStatusText = string.Empty;
    private double _referenceEnergyPercent;
    private double _endingEnergyPercent;
    private double _returnThresholdPercent;
    private double _cancellationThresholdPercent;
    private bool _isHeadOutRecheckActive;
    private string _currentChordText = "-";
    private string _nextChordText = "-";
    private bool _midiThruEnabled;
    private int _vibraphoneVolume = 100;
    private bool _portsOpen;
    private bool _isSessionRunning;
    private bool _automaticThemeReturnEnabled;
    private int _themeReturnSensitivity = 50;
    private bool _pianoEnabled = true;
    private bool _bassEnabled = true;
    private bool _drumsEnabled = true;
    private int _pianoVolume = 100;
    private int _bassVolume = 100;
    private int _drumsVolume = 100;
    private bool _chordSheetUsesEndingForm;
    private int _chordSheetScale = 4;
    private double _chordSheetViewportWidth;
    private double _chordSheetBarWidth = 158d;
    private int _lastChordSheetBar = int.MinValue;
    private int _lastChordSheetBeat = int.MinValue;
    private SessionPlaybackPhase _lastChordSheetPhase = (SessionPlaybackPhase)(-1);
    private int _energyScopeChorus;
    private long _energyScopeAttackFloor = long.MinValue;
    private long _lastEnergyAttackMilliseconds = long.MinValue;
    private bool _disposed;
    private bool _deviceRefreshRunning;
    private bool _asioSettingsRefreshRunning;
    private bool _suppressAsioSettingPersistence;
    private WindowsAudioBackendOption _selectedWindowsAudioBackendOption = WindowsAudioBackendOption.Automatic;
    private AsioDriverOption _selectedAsioDriverOption = AsioDriverOption.Automatic;
    private AsioSampleRateOption _selectedAsioSampleRateOption = new(48_000);
    private AsioBufferSizeOption _selectedAsioBufferSizeOption = new(0);
    private AsioOutputChannelOption _selectedAsioOutputChannelOption = new(0, "Outputs 1/2");

    public MainWindowViewModel()
    {
        _settings = AppSettingsStore.Load();
        _selectedWindowsAudioBackendOption = WindowsAudioBackendOption.FromName(_settings.WindowsAudioBackend);
        _selectedAsioDriverOption = AsioDriverOption.FromName(_settings.AsioDriverName);
        _selectedAsioSampleRateOption = new(
            _settings.AsioSampleRate is >= 8_000 and <= 384_000 ? _settings.AsioSampleRate : 48_000);
        _selectedAsioBufferSizeOption = new(Math.Max(0, _settings.AsioBufferSize));
        _selectedAsioOutputChannelOption = new(
            Math.Max(0, _settings.AsioOutputChannelOffset),
            "Outputs 1/2");
        _midiPortService = new MidiPortService(CreateAsioAudioSettings);
        _automaticThemeReturnEnabled = _settings.ThemeReturnPreferenceSet && _settings.DetectThemeReturnEnabled;
        _themeReturnSensitivity = Math.Clamp(_settings.HeadOutSensitivity, 0, 100);
        _pianoEnabled = _settings.PianoEnabled;
        _bassEnabled = _settings.BassEnabled;
        _drumsEnabled = _settings.DrumsEnabled;
        _pianoVolume = Math.Clamp(_settings.PianoVolume, 0, 100);
        _bassVolume = Math.Clamp(_settings.BassVolume, 0, 100);
        _drumsVolume = Math.Clamp(_settings.DrumsVolume, 0, 100);
        _midiThruEnabled = _settings.MidiThruToVibraphoneEnabled;
        _vibraphoneVolume = Math.Clamp(_settings.VibraphoneVolume, 0, 100);
        _songLibraryService = new SongLibraryService(_settings.SongLibraryFolder);
        _preferredInputPort = _settings.InputPortName;
        _preferredOutputPort = _settings.OutputPortName;
        _selectedInputPort = string.IsNullOrWhiteSpace(_preferredInputPort)
            ? NoMidiInputName
            : _preferredInputPort;
        _selectedOutputPort = string.IsNullOrWhiteSpace(_preferredOutputPort)
            ? MidiPortService.BuiltInTrioOutputName
            : _preferredOutputPort;
        Tunes = new ObservableCollection<TuneOption>();
        _selectedTune = new TuneOption(TuneCatalog.Default);
        _activeTune = _selectedTune.Tune;
        _selectedStyleOption = StyleOption.DefaultFor(_selectedTune.Tune);
        _tempoBpm = _activeTune.DefaultTempoBpm;
        _playbackController = new SessionPlaybackController(_midiPortService, _activeTune);
        ApplyMixerSettings();

        StyleOptions = new ObservableCollection<StyleOption>();
        KeyOptions = new ObservableCollection<KeyOption>();
        AccidentalOptions = new ObservableCollection<AccidentalOption>
        {
            AccidentalOption.Auto,
            AccidentalOption.Flats,
            AccidentalOption.Sharps
        };
        // Keep the saved choices present while external MIDI discovery runs.
        // This gives the ComboBoxes a valid item from their first binding pass.
        InputPorts = new ObservableCollection<string>(new[] { _selectedInputPort ?? NoMidiInputName });
        OutputPorts = new ObservableCollection<string>(new[]
        {
            _selectedOutputPort ?? MidiPortService.BuiltInTrioOutputName
        });
        AsioDriverOptions = new ObservableCollection<AsioDriverOption>
        {
            AsioDriverOption.Automatic
        };
        WindowsAudioBackendOptions = new ObservableCollection<WindowsAudioBackendOption>
        {
            WindowsAudioBackendOption.Automatic,
            WindowsAudioBackendOption.Asio,
            WindowsAudioBackendOption.WinMm
        };
        AsioSampleRateOptions = new ObservableCollection<AsioSampleRateOption>
        {
            _selectedAsioSampleRateOption
        };
        AsioBufferSizeOptions = new ObservableCollection<AsioBufferSizeOption>
        {
            _selectedAsioBufferSizeOption
        };
        AsioOutputChannelOptions = new ObservableCollection<AsioOutputChannelOption>
        {
            _selectedAsioOutputChannelOption
        };
        ChordRows = new ObservableCollection<ChordSheetRowViewModel>();
        CodaRows = new ObservableCollection<ChordSheetRowViewModel>();
        ChannelRows = new ObservableCollection<string>();

        GeneratePreviewCommand = new RelayCommand(GeneratePreview);
        RefreshDevicesCommand = new RelayCommand(() =>
        {
            _ = RefreshDevicesAsync();
            _ = RefreshAsioSettingsAsync();
        });
        StartSessionCommand = new RelayCommand(StartSession);
        StopSessionCommand = new RelayCommand(StopSession);
        PanicCommand = new RelayCommand(Panic);
        ReturnToThemeCommand = new RelayCommand(ReturnToTheme);
        RefreshSongsCommand = new RelayCommand(() => RefreshSongLibrary(null, applyDefaultTempo: false, showStatus: true));

        _midiPortService.MessageReceived += MidiPortService_MessageReceived;
        _midiPortService.DeviceError += MidiPortService_DeviceError;
        _playbackController.PlaybackError += PlaybackController_PlaybackError;
        _playbackController.SessionCompleted += PlaybackController_SessionCompleted;

        _snapshotTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _snapshotTimer.Tick += (_, _) =>
        {
            UpdatePerformanceInteraction();
            RefreshPlaybackSnapshot();
        };

        // Do not scan a user's entire song library before Avalonia has shown the
        // window. A library imported from iReal Pro can contain thousands of
        // files, and parsing those files on the UI thread makes macOS appear to
        // hang with a continuously bouncing Dock icon. Start with the built-in
        // tune and replace it asynchronously after the window is visible.
        Tunes.Add(_selectedTune);
        RebuildStyleOptions(preserveSelection: false);
        RebuildKeyOptions();
        ApplySelectedStyleToPlayback();
        RefreshTuneDetails(clearPreview: true);
        OnPropertyChanged(nameof(SelectedTune));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<int>? ChordSheetRowChanged;

    public ObservableCollection<TuneOption> Tunes { get; }
    public ObservableCollection<StyleOption> StyleOptions { get; }
    public ObservableCollection<KeyOption> KeyOptions { get; }
    public ObservableCollection<AccidentalOption> AccidentalOptions { get; }
    public ObservableCollection<string> InputPorts { get; }
    public ObservableCollection<string> OutputPorts { get; }
    public ObservableCollection<WindowsAudioBackendOption> WindowsAudioBackendOptions { get; }
    public ObservableCollection<AsioDriverOption> AsioDriverOptions { get; }
    public ObservableCollection<AsioSampleRateOption> AsioSampleRateOptions { get; }
    public ObservableCollection<AsioBufferSizeOption> AsioBufferSizeOptions { get; }
    public ObservableCollection<AsioOutputChannelOption> AsioOutputChannelOptions { get; }
    public ObservableCollection<ChordSheetRowViewModel> ChordRows { get; }
    public ObservableCollection<ChordSheetRowViewModel> CodaRows { get; }
    public ObservableCollection<string> ChannelRows { get; }
    public ICommand GeneratePreviewCommand { get; }
    public ICommand RefreshDevicesCommand { get; }
    public ICommand StartSessionCommand { get; }
    public ICommand StopSessionCommand { get; }
    public ICommand PanicCommand { get; }
    public ICommand ReturnToThemeCommand { get; }
    public ICommand RefreshSongsCommand { get; }

    public TuneOption SelectedTune
    {
        get => _selectedTune;
        set
        {
            if (value is null)
            {
                return;
            }

            if (ReferenceEquals(_selectedTune, value))
            {
                return;
            }

            if (_playbackController.IsRunning)
            {
                StatusText = "Stop the session before changing tunes.";
                OnPropertyChanged();
                return;
            }

            _selectedTune = value;
            RebuildStyleOptions(preserveSelection: false);
            RebuildKeyOptions();
            ApplySelectedStyleToPlayback();
            TempoBpm = _activeTune.DefaultTempoBpm;
            RefreshTuneDetails(clearPreview: true);
            OnPropertyChanged();
        }
    }

    public StyleOption SelectedStyleOption
    {
        get => _selectedStyleOption;
        set
        {
            if (value is null)
            {
                return;
            }

            if (ReferenceEquals(_selectedStyleOption, value))
            {
                return;
            }

            if (_playbackController.IsRunning)
            {
                StatusText = "Stop the session before changing the accompaniment style.";
                OnPropertyChanged();
                return;
            }

            _selectedStyleOption = value;
            ApplySelectedStyleToPlayback();
            RefreshTuneDetails(clearPreview: true);
            StatusText = $"Accompaniment style set to {StyleText}.";
            OnPropertyChanged();
        }
    }

    public void SelectStyleOption(StyleOption styleOption)
    {
        ArgumentNullException.ThrowIfNull(styleOption);
        SelectedStyleOption = styleOption;
    }

    public KeyOption SelectedKeyOption
    {
        get => _selectedKeyOption;
        set
        {
            if (value is null)
            {
                return;
            }

            if (ReferenceEquals(_selectedKeyOption, value))
            {
                return;
            }

            if (_playbackController.IsRunning)
            {
                StatusText = "Stop the session before changing the key.";
                OnPropertyChanged();
                return;
            }

            _selectedKeyOption = value;
            ApplySelectedStyleToPlayback();
            RefreshTuneDetails(clearPreview: true);
            StatusText = $"Key set to {value.DisplayName}.";
            OnPropertyChanged();
        }
    }

    public AccidentalOption SelectedAccidentalOption
    {
        get => _selectedAccidentalOption;
        set
        {
            if (value is null)
            {
                return;
            }

            if (ReferenceEquals(_selectedAccidentalOption, value))
            {
                return;
            }

            if (_playbackController.IsRunning)
            {
                StatusText = "Stop the session before changing accidental spelling.";
                OnPropertyChanged();
                return;
            }

            _selectedAccidentalOption = value;
            RebuildKeyOptions(preserveCurrentTarget: true);
            ApplySelectedStyleToPlayback();
            RefreshTuneDetails(clearPreview: true);
            StatusText = $"Accidental spelling set to {value.DisplayName}.";
            OnPropertyChanged();
        }
    }

    public string? SelectedInputPort
    {
        get => _selectedInputPort;
        set
        {
            if (value is null)
            {
                return;
            }

            if (!SetField(ref _selectedInputPort, value) || _suppressMidiPortPersistence)
            {
                return;
            }

            _preferredInputPort = value;
            _settings.InputPortName = value;
            AppSettingsStore.TrySave(_settings);
            ApplyMidiPortChange(inputChanged: true);
        }
    }

    public string? SelectedOutputPort
    {
        get => _selectedOutputPort;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!SetField(ref _selectedOutputPort, value) || _suppressMidiPortPersistence)
            {
                return;
            }

            _preferredOutputPort = value;
            _settings.OutputPortName = value;
            AppSettingsStore.TrySave(_settings);
            ApplyMidiPortChange(inputChanged: false);
        }
    }

    public bool MidiThruEnabled
    {
        get => _midiThruEnabled;
        set
        {
            if (!SetField(ref _midiThruEnabled, value))
            {
                return;
            }

            _settings.MidiThruToVibraphoneEnabled = value;
            AppSettingsStore.TrySave(_settings);
            if (!PortsOpen)
            {
                return;
            }

            try
            {
                _midiPortService.SetMidiThruEnabled(value);
                StatusText = value
                    ? "MIDI Thru is on: incoming notes are routed to Ch.1 Vibraphone."
                    : "MIDI Thru is off.";
            }
            catch (Exception ex)
            {
                StatusText = $"Could not change MIDI Thru: {ex.Message}";
            }
        }
    }

    public int TempoBpm
    {
        get => _tempoBpm;
        set
        {
            var clamped = Math.Clamp(value, 40, 300);
            if (SetField(ref _tempoBpm, clamped))
            {
                OnPropertyChanged(nameof(TempoText));
                if (_playbackController.IsRunning && _playbackController.SetTempo(clamped))
                {
                    StatusText = $"Tempo changed to {clamped} BPM.";
                }
            }
        }
    }

    public string Title => _activeTune.Title;
    public string KeyText => string.IsNullOrWhiteSpace(_activeTune.Key) ? "-" : _activeTune.Key;
    public string DefaultKeyText => TuneTransposer.GetKeyInfo(SelectedTune.Tune).Name;
    public string StyleText => AccompanimentStyleNames.DisplayName(_activeTune.AccompanimentStyle);
    public string DefaultStyleText => AccompanimentStyleNames.DisplayName(SelectedTune.Tune.AccompanimentStyle);
    public string TimeSignatureText => _activeTune.TimeSignature;
    public string TempoText => $"{TempoBpm} BPM";
    public string FormText => $"{_activeTune.Bars.Count} bars, {_activeTune.SegmentCount} segments";
    public string SongLibraryText => _songLibraryService.LibraryFolder;
    public bool ShowCodaPreview => _activeTune.HasCoda && CodaRows.Count > 0;
    public int ChordSheetRowCount => ChordRows.Count + (ShowCodaPreview ? CodaRows.Count : 0);

    public void SetSongLibraryFolder(string path)
    {
        if (_playbackController.IsRunning)
        {
            StatusText = "Stop the session before changing the song folder.";
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(fullPath);
            _songLibraryService.SetLibraryFolder(fullPath);
            _settings.SongLibraryFolder = fullPath;
            AppSettingsStore.TrySave(_settings);
            RefreshSongLibrary(null, applyDefaultTempo: false, showStatus: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = $"Could not change the song folder: {ex.Message}";
        }
    }

    public void SetChordSheetViewportWidth(double viewportWidth)
    {
        _chordSheetViewportWidth = viewportWidth;
        const double sectionLabelWidth = 50d;
        const double scrollViewerHorizontalPadding = 32d;
        const double fourCellHorizontalMargins = 24d;
        const double verticalScrollbarAllowance = 12d;
        var availableForBars = viewportWidth -
            sectionLabelWidth -
            scrollViewerHorizontalPadding -
            fourCellHorizontalMargins -
            verticalScrollbarAllowance -
            (ChordSheetCellViewModel.LoopMarkerGutter * 2d);
        var fittedBarWidth = Math.Max(118d, Math.Floor(availableForBars / 4d));
        if (Math.Abs(fittedBarWidth - _chordSheetBarWidth) < 0.5d)
        {
            return;
        }

        _chordSheetBarWidth = fittedBarWidth;
        foreach (var cell in ChordRows.Concat(CodaRows).SelectMany(row => row.Cells))
        {
            cell.SetBarWidth(_chordSheetBarWidth);
        }
    }

    public void ImportIRealProFile(string path)
    {
        if (_playbackController.IsRunning)
        {
            StatusText = "Stop the session before importing songs.";
            return;
        }

        try
        {
            var result = _songLibraryService.ImportIRealProFile(path);
            var firstImportedPath = result.ImportedFilePaths.FirstOrDefault();
            RefreshSongLibrary(
                firstImportedPath is null ? null : Path.GetFileName(firstImportedPath),
                applyDefaultTempo: true,
                showStatus: false);

            var description = result.ImportedFilePaths.Count == 1
                ? $"Imported {Path.GetFileNameWithoutExtension(result.ImportedFilePaths[0])}."
                : $"Imported {result.ImportedFilePaths.Count} songs from iReal Pro.";
            if (result.Warnings.Count > 0)
            {
                description += $" Note: {result.Warnings[0]}";
                if (result.Warnings.Count > 1)
                {
                    description += $" (+{result.Warnings.Count - 1} more)";
                }
            }

            StatusText = description;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IRealProImportException or ChordProSongParseException or FormatException or ArgumentException)
        {
            StatusText = $"Could not import the iReal Pro file: {ex.Message}";
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string PreviewSummary
    {
        get => _previewSummary;
        private set => SetField(ref _previewSummary, value);
    }

    public string LiveStatusText
    {
        get => _liveStatusText;
        private set => SetField(ref _liveStatusText, value);
    }

    public string PlaybackStatusText
    {
        get => _playbackStatusText;
        private set => SetField(ref _playbackStatusText, value);
    }

    public string ArrangementStageText
    {
        get => _arrangementStageText;
        private set => SetField(ref _arrangementStageText, value);
    }

    public string ThemeReturnStatusText
    {
        get => _themeReturnStatusText;
        private set
        {
            if (SetField(ref _themeReturnStatusText, value))
            {
                OnPropertyChanged(nameof(HasThemeReturnStatus));
            }
        }
    }

    public bool HasThemeReturnStatus => !string.IsNullOrWhiteSpace(ThemeReturnStatusText);

    public double ReferenceEnergyPercent
    {
        get => _referenceEnergyPercent;
        private set => SetField(ref _referenceEnergyPercent, value);
    }

    public double EndingEnergyPercent
    {
        get => _endingEnergyPercent;
        private set => SetField(ref _endingEnergyPercent, value);
    }

    public double ReturnThresholdPercent
    {
        get => _returnThresholdPercent;
        private set => SetField(ref _returnThresholdPercent, value);
    }

    public double CancellationThresholdPercent
    {
        get => _cancellationThresholdPercent;
        private set
        {
            if (SetField(ref _cancellationThresholdPercent, value))
            {
                OnPropertyChanged(nameof(CancellationThresholdMarkerMargin));
            }
        }
    }

    public Thickness CancellationThresholdMarkerMargin => new(
        Math.Clamp(CancellationThresholdPercent, 0, 100) * 2.2 - 1.5,
        0,
        0,
        0);

    public bool IsHeadOutRecheckActive
    {
        get => _isHeadOutRecheckActive;
        private set => SetField(ref _isHeadOutRecheckActive, value);
    }

    public bool AutomaticThemeReturnEnabled
    {
        get => _automaticThemeReturnEnabled;
        set
        {
            if (!SetField(ref _automaticThemeReturnEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ThemeReturnModeText));
            _headOutDetector.Reset();
            ThemeReturnStatusText = string.Empty;
            _settings.DetectThemeReturnEnabled = value;
            _settings.ThemeReturnPreferenceSet = true;
            AppSettingsStore.TrySave(_settings);
        }
    }

    public string ThemeReturnModeText => AutomaticThemeReturnEnabled ? "Auto" : "Manual";

    public int ThemeReturnSensitivity
    {
        get => _themeReturnSensitivity;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetField(ref _themeReturnSensitivity, clamped))
            {
                _headOutDetector.Configure(EffectiveThemeReturnSensitivity());
                _settings.HeadOutSensitivity = clamped;
                AppSettingsStore.TrySave(_settings);
            }
        }
    }

    public int ChordSheetScale
    {
        get => _chordSheetScale;
        set
        {
            var clamped = Math.Clamp(value, 0, 9);
            if (SetField(ref _chordSheetScale, clamped))
            {
                OnPropertyChanged(nameof(ChordSheetScaleText));
                if (_chordSheetViewportWidth > 0d)
                {
                    ApplyChordSheetScale();
                }
            }
        }
    }

    public string ChordSheetScaleText => $"{Math.Round(ChordSheetScaleFactor * 100d):0}%";

    public bool PianoEnabled
    {
        get => _pianoEnabled;
        set
        {
            if (SetField(ref _pianoEnabled, value))
            {
                _settings.PianoEnabled = value;
                AppSettingsStore.TrySave(_settings);
                _midiPortService.SetChannelMute(SessionConstants.PianoChannel, !value);
            }
        }
    }

    public bool BassEnabled
    {
        get => _bassEnabled;
        set
        {
            if (SetField(ref _bassEnabled, value))
            {
                _settings.BassEnabled = value;
                AppSettingsStore.TrySave(_settings);
                _midiPortService.SetChannelMute(SessionConstants.BassChannel, !value);
            }
        }
    }

    public bool DrumsEnabled
    {
        get => _drumsEnabled;
        set
        {
            if (SetField(ref _drumsEnabled, value))
            {
                _settings.DrumsEnabled = value;
                AppSettingsStore.TrySave(_settings);
                _midiPortService.SetChannelMute(SessionConstants.DrumsChannel, !value);
            }
        }
    }

    public int PianoVolume
    {
        get => _pianoVolume;
        set => SetInstrumentVolume(ref _pianoVolume, value, SessionConstants.PianoChannel);
    }

    public int BassVolume
    {
        get => _bassVolume;
        set => SetInstrumentVolume(ref _bassVolume, value, SessionConstants.BassChannel);
    }

    public int DrumsVolume
    {
        get => _drumsVolume;
        set => SetInstrumentVolume(ref _drumsVolume, value, SessionConstants.DrumsChannel);
    }

    public int VibraphoneVolume
    {
        get => _vibraphoneVolume;
        set => SetInstrumentVolume(ref _vibraphoneVolume, value, SessionConstants.VibraphoneChannel);
    }

    public string CurrentChordText
    {
        get => _currentChordText;
        private set => SetField(ref _currentChordText, value);
    }

    public string NextChordText
    {
        get => _nextChordText;
        private set => SetField(ref _nextChordText, value);
    }

    public bool PortsOpen
    {
        get => _portsOpen;
        private set => SetField(ref _portsOpen, value);
    }

    public bool IsSessionRunning
    {
        get => _isSessionRunning;
        private set
        {
            if (SetField(ref _isSessionRunning, value))
            {
                OnPropertyChanged(nameof(PrimarySessionButtonText));
            }
        }
    }

    public string PrimarySessionButtonText => !IsSessionRunning
        ? "Start Session"
        : _playbackController.IsHeadOutQueued
            ? "Head Out Queued"
            : _playbackController.IsHeadOutActive
                ? "Head Out"
                : "Back to Head";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _snapshotTimer.Stop();
        _playbackController.Stop();
        _playbackController.Dispose();
        _midiPortService.Dispose();
    }

    private void RefreshTuneDetails(bool clearPreview)
    {
        BuildChordSheetRows(useEndingForm: false);

        if (clearPreview)
        {
            ChannelRows.Clear();
            PreviewSummary = "No preview generated yet.";
        }

        StatusText = "Ready. Select MIDI input/output, open MIDI, then start the session.";
        RefreshPlaybackSnapshot();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(KeyText));
        OnPropertyChanged(nameof(DefaultKeyText));
        OnPropertyChanged(nameof(StyleText));
        OnPropertyChanged(nameof(DefaultStyleText));
        OnPropertyChanged(nameof(TimeSignatureText));
        OnPropertyChanged(nameof(TempoText));
        OnPropertyChanged(nameof(FormText));
        OnPropertyChanged(nameof(SongLibraryText));
        OnPropertyChanged(nameof(SelectedStyleOption));
        OnPropertyChanged(nameof(SelectedKeyOption));
    }

    private void RefreshSongLibrary(string? preferredFileName, bool applyDefaultTempo, bool showStatus)
    {
        if (_playbackController.IsRunning)
        {
            StatusText = "Stop the session before refreshing songs.";
            return;
        }

        try
        {
            ApplySongLibraryEntries(
                _songLibraryService.Scan(),
                preferredFileName,
                applyDefaultTempo,
                showStatus);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ChordProSongParseException or FormatException or ArgumentException)
        {
            StatusText = $"Could not reload the song library: {ex.Message}";
        }
    }

    private async Task RefreshSongLibraryAsync(string? preferredFileName, bool applyDefaultTempo, bool showStatus)
    {
        if (_disposed || _playbackController.IsRunning)
        {
            return;
        }

        try
        {
            // File enumeration and ChordPro parsing are deliberately kept off
            // Avalonia's UI thread. This is especially important on macOS,
            // where a large iReal library can otherwise delay first window
            // presentation for a long time.
            var entries = await Task.Run(_songLibraryService.Scan);
            if (_disposed)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
                ApplySongLibraryEntries(entries, preferredFileName, applyDefaultTempo, showStatus));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ChordProSongParseException or FormatException or ArgumentException)
        {
            if (!_disposed)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    StatusText = $"Could not reload the song library: {ex.Message}");
            }
        }
    }

    private void ApplySongLibraryEntries(
        IReadOnlyList<SongFileEntry> entries,
        string? preferredFileName,
        bool applyDefaultTempo,
        bool showStatus)
    {
        var options = entries
            .Where(entry => entry.IsValid)
            .Select(entry => new TuneOption(entry.Tune!, entry.FilePath))
            .ToArray();

        if (options.Length == 0)
        {
            options = TuneCatalog.All.Select(tune => new TuneOption(tune)).ToArray();
        }

        var preferred = string.IsNullOrWhiteSpace(preferredFileName)
            ? SelectedTune.FileName
            : preferredFileName;
        var selected = options.FirstOrDefault(option =>
                string.Equals(option.FileName, preferred, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(option =>
                string.Equals(option.Tune.Id, SelectedTune.Tune.Id, StringComparison.OrdinalIgnoreCase))
            ?? options[0];

        Tunes.Clear();
        foreach (var option in options)
        {
            Tunes.Add(option);
        }

        _selectedTune = selected;
        RebuildStyleOptions(preserveSelection: true);
        RebuildKeyOptions();
        ApplySelectedStyleToPlayback();
        if (applyDefaultTempo)
        {
            TempoBpm = _activeTune.DefaultTempoBpm;
        }

        RefreshTuneDetails(clearPreview: true);
        OnPropertyChanged(nameof(SelectedTune));

        if (showStatus)
        {
            StatusText = $"Song library refreshed. {Tunes.Count} playable songs.";
        }
    }

    private void RebuildStyleOptions(bool preserveSelection)
    {
        var previousOverride = preserveSelection ? _selectedStyleOption.OverrideStyle : null;
        var options = new List<StyleOption>
        {
            StyleOption.DefaultFor(SelectedTune.Tune)
        };

        var compatibleStyles = SelectedTune.Tune.BeatsPerBar == 3
            ? new[] { AccompanimentStyle.JazzWaltz }
            : new[]
            {
                AccompanimentStyle.Swing,
                AccompanimentStyle.JazzBallad,
                AccompanimentStyle.BossaNova,
                AccompanimentStyle.AfroCubanLatin
            };

        options.AddRange(compatibleStyles.Select(StyleOption.Override));
        ReplaceItems(StyleOptions, options);
        _selectedStyleOption = previousOverride is null
            ? StyleOptions[0]
            : StyleOptions.FirstOrDefault(option => option.OverrideStyle == previousOverride) ?? StyleOptions[0];
    }

    private void RebuildKeyOptions(bool preserveCurrentTarget = false)
    {
        var sourceKey = TuneTransposer.GetKeyInfo(SelectedTune.Tune);
        if (!preserveCurrentTarget)
        {
            _selectedAccidentalOption = AccidentalOption.Auto;
        }

        var preferFlats = _selectedAccidentalOption.PreferFlats ??
            TuneTransposer.GetAutoPreferFlats(SelectedTune.Tune);
        var canonicalNames = sourceKey.IsMinor
            ? (preferFlats
                ? new[] { "Cm", "Dbm", "Dm", "Ebm", "Em", "Fm", "Gbm", "Gm", "Abm", "Am", "Bbm", "Bm" }
                : new[] { "Cm", "C#m", "Dm", "D#m", "Em", "Fm", "F#m", "Gm", "G#m", "Am", "A#m", "Bm" })
            : (preferFlats
                ? new[] { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" }
                : new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" });
        var options = canonicalNames
            .Select(name => CreateKeyOption(name))
            .ToArray();
        var selectedPitchClass = preserveCurrentTarget
            ? _selectedKeyOption.PitchClass
            : sourceKey.PitchClass;
        var selectedIndex = Array.FindIndex(options, option => option.PitchClass == selectedPitchClass);
        if (selectedIndex < 0)
        {
            selectedIndex = 0;
        }

        ReplaceItems(KeyOptions, options);
        _selectedKeyOption = options[selectedIndex];
        OnPropertyChanged(nameof(SelectedAccidentalOption));
        OnPropertyChanged(nameof(SelectedKeyOption));
    }

    private static KeyOption CreateKeyOption(string name)
    {
        var normalized = name.Trim();
        var isMinor = normalized.EndsWith("m", StringComparison.OrdinalIgnoreCase);
        var root = isMinor ? normalized[..^1] : normalized;
        var pitchClass = ChordSymbolParser.Parse(normalized).RootPitchClass;
        return new KeyOption(
            normalized,
            pitchClass,
            isMinor,
            root.Contains('b', StringComparison.Ordinal));
    }

    private void ApplySelectedStyleToPlayback()
    {
        var styledTune = _selectedStyleOption.Resolve(SelectedTune.Tune);
        _activeTune = _selectedAccidentalOption.IsAuto
            ? TuneTransposer.TransposeAuto(styledTune, _selectedKeyOption.DisplayName)
            : TuneTransposer.Transpose(
                styledTune,
                _selectedKeyOption.DisplayName,
                _selectedAccidentalOption.PreferFlats);
        _playbackController.SelectTune(_activeTune);
    }

    private void GeneratePreview()
    {
        var plan = SessionPlanBuilder.BuildTwoBeat(_activeTune);
        var notes = plan.CountInNotes.Concat(plan.ChorusNotes).ToArray();
        var seconds = plan.ChorusLengthTicks / (double)SessionConstants.Ppq / _activeTune.BeatsPerBar *
            60.0 / TempoBpm;

        PreviewSummary =
            $"{notes.Length} notes generated; count-in {plan.CountInLengthTicks} ticks; " +
            $"chorus {plan.ChorusLengthTicks} ticks; approx. {seconds:0.0}s at {TempoBpm} BPM.";

        ChannelRows.Clear();
        foreach (var group in notes.GroupBy(note => note.Channel).OrderBy(group => group.Key))
        {
            ChannelRows.Add($"{ChannelName(group.Key),-12}  {group.Count(),4} notes");
        }

        StatusText = "Preview generated. Live session playback uses the same Jampanion.Core arrangement engine.";
    }

    public void StartBackgroundInitialization()
    {
        // CoreMIDI enumeration can enter a native wait during the first
        // seconds after a fresh macOS login (and some virtual MIDI drivers can
        // take the process down while they are being discovered). Keep first
        // window presentation independent of that optional enumeration. The
        // built-in trio is already available; the Refresh devices command can
        // still be used when an external MIDI port is needed.
        if (!OperatingSystem.IsMacOS())
        {
            _ = RefreshDevicesAsync();
        }

        _ = RefreshAsioSettingsAsync();
        _ = RefreshSongLibraryAsync(null, applyDefaultTempo: true, showStatus: false);
    }

    private async Task RefreshAsioSettingsAsync()
    {
        if (_disposed || !IsAsioSettingsVisible || _asioSettingsRefreshRunning)
        {
            return;
        }

        _asioSettingsRefreshRunning = true;
        try
        {
            var driverNames = await Task.Run(AsioAudioOutput.GetDriverNames);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyAsioDriverList(driverNames));
        }
        catch
        {
            // The built-in trio remains usable through WinMM even when ASIO
            // enumeration is unavailable or a driver is misbehaving.
        }
        finally
        {
            _asioSettingsRefreshRunning = false;
        }
    }

    private void ApplyAsioDriverList(IReadOnlyList<string> driverNames)
    {
        if (_disposed)
        {
            return;
        }

        var options = new[] { AsioDriverOption.Automatic }
            .Concat(driverNames.Select(name => new AsioDriverOption(name, name)))
            .GroupBy(option => option.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var selected = options.FirstOrDefault(option =>
            string.Equals(option.Name, _settings.AsioDriverName, StringComparison.Ordinal))
            ?? AsioDriverOption.Automatic;

        var previousSuppression = _suppressAsioSettingPersistence;
        _suppressAsioSettingPersistence = true;
        try
        {
            ReplaceItems(AsioDriverOptions, options);
            _selectedAsioDriverOption = selected;
            _settings.AsioDriverName = selected.Name;
            OnPropertyChanged(nameof(SelectedAsioDriverOption));
            RefreshAsioDependentOptions();
        }
        finally
        {
            _suppressAsioSettingPersistence = previousSuppression;
        }
    }

    private void RefreshAsioDependentOptions()
    {
        var driverName = _selectedAsioDriverOption.Name;
        var rates = AsioAudioOutput.GetSupportedSampleRates(driverName);
        var buffers = AsioAudioOutput.GetSupportedBufferSizes(driverName);
        var channels = AsioAudioOutput.GetOutputChannelOptions(driverName);
        var selectedRate = rates.Contains(_settings.AsioSampleRate)
            ? _settings.AsioSampleRate
            : rates.Contains(48_000) ? 48_000 : rates.FirstOrDefault();
        var selectedBuffer = buffers.Contains(_settings.AsioBufferSize)
            ? _settings.AsioBufferSize
            : buffers.FirstOrDefault();
        if (selectedRate <= 0)
        {
            selectedRate = 48_000;
        }

        var rateOptions = rates
            .Distinct()
            .OrderBy(rate => rate)
            .Select(rate => new AsioSampleRateOption(rate))
            .ToArray();
        var bufferOptions = buffers
            .Distinct()
            .OrderBy(size => size == 0 ? int.MinValue : size)
            .Select(size => new AsioBufferSizeOption(size))
            .ToArray();
        var channelOptions = channels
            .Select(channel => new AsioOutputChannelOption(channel.Offset, channel.DisplayName))
            .ToArray();
        if (rateOptions.Length == 0)
        {
            rateOptions = new[] { new AsioSampleRateOption(48_000) };
        }

        if (bufferOptions.Length == 0)
        {
            bufferOptions = new[] { new AsioBufferSizeOption(0) };
        }

        if (channelOptions.Length == 0)
        {
            channelOptions = new[] { new AsioOutputChannelOption(0, "Outputs 1/2") };
        }

        var rateOption = rateOptions.FirstOrDefault(option => option.Hertz == selectedRate) ?? rateOptions[0];
        var bufferOption = bufferOptions.FirstOrDefault(option => option.Frames == selectedBuffer) ?? bufferOptions[0];
        var channelOption = channelOptions.FirstOrDefault(option =>
            option.Offset == _settings.AsioOutputChannelOffset) ?? channelOptions[0];
        ReplaceItems(AsioSampleRateOptions, rateOptions);
        ReplaceItems(AsioBufferSizeOptions, bufferOptions);
        ReplaceItems(AsioOutputChannelOptions, channelOptions);
        _selectedAsioSampleRateOption = rateOption;
        _selectedAsioBufferSizeOption = bufferOption;
        _selectedAsioOutputChannelOption = channelOption;
        _settings.AsioSampleRate = rateOption.Hertz;
        _settings.AsioBufferSize = bufferOption.Frames;
        _settings.AsioOutputChannelOffset = channelOption.Offset;
        OnPropertyChanged(nameof(SelectedAsioSampleRateOption));
        OnPropertyChanged(nameof(SelectedAsioBufferSizeOption));
        OnPropertyChanged(nameof(SelectedAsioOutputChannelOption));
        AppSettingsStore.TrySave(_settings);
    }

    private void ApplyAsioSettingChange()
    {
        if (_disposed || !PortsOpen ||
            !string.Equals(SelectedOutputPort, MidiPortService.BuiltInTrioOutputName, StringComparison.Ordinal))
        {
            return;
        }

        ApplyMidiPortChange(inputChanged: false);
        if (IsSessionRunning)
        {
            StatusText = "Audio settings applied; built-in audio output restarted.";
        }
    }

    private AsioAudioSettings CreateAsioAudioSettings() => new(
        _selectedAsioDriverOption.Name,
        _selectedAsioSampleRateOption.Hertz,
        _selectedAsioBufferSizeOption.Frames,
        _selectedAsioOutputChannelOption.Offset,
        _selectedWindowsAudioBackendOption.Backend);

    private async Task RefreshDevicesAsync()
    {
        if (_disposed || _deviceRefreshRunning)
        {
            return;
        }

        _deviceRefreshRunning = true;

        // DryWetMidi's CoreMIDI enumeration can take an unexpectedly long time
        // on macOS when no MIDI service/device is available. Never perform that
        // native call on Avalonia's UI thread: the window must be shown even if
        // external MIDI discovery is unavailable.
        var enumeration = Task.Run(() =>
        {
            var inputNames = new[] { NoMidiInputName }
                .Concat(MidiPortService.GetInputPortNames())
                .ToArray();
            var outputNames = MidiPortService.GetOutputPortNames().ToArray();
            return (InputNames: inputNames, OutputNames: outputNames);
        });

        try
        {
            if (await Task.WhenAny(enumeration, Task.Delay(TimeSpan.FromSeconds(5))) != enumeration)
            {
                ApplyMidiDeviceLists(
                    new[] { NoMidiInputName },
                    new[] { MidiPortService.BuiltInTrioOutputName },
                    "External MIDI discovery timed out; the built-in trio remains available.");
                return;
            }

            var (inputNames, outputNames) = await enumeration;
            ApplyMidiDeviceLists(inputNames, outputNames, errorMessage: null);
        }
        catch (Exception ex)
        {
            ApplyMidiDeviceLists(
                new[] { NoMidiInputName },
                new[] { MidiPortService.BuiltInTrioOutputName },
                $"Could not enumerate external MIDI ports: {ex.Message}");
        }
        finally
        {
            _deviceRefreshRunning = false;
        }
    }

    public bool IsAsioSettingsVisible => OperatingSystem.IsWindows();

    public bool IsAsioBackendSelected =>
        IsAsioSettingsVisible && _selectedWindowsAudioBackendOption.Backend != AsioAudioBackend.WinMm;

    public WindowsAudioBackendOption SelectedWindowsAudioBackendOption
    {
        get => _selectedWindowsAudioBackendOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetField(ref _selectedWindowsAudioBackendOption, value) || _suppressAsioSettingPersistence)
            {
                return;
            }

            _settings.WindowsAudioBackend = value.Name;
            OnPropertyChanged(nameof(IsAsioBackendSelected));
            AppSettingsStore.TrySave(_settings);
            ApplyAsioSettingChange();
        }
    }

    public AsioDriverOption SelectedAsioDriverOption
    {
        get => _selectedAsioDriverOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetField(ref _selectedAsioDriverOption, value) || _suppressAsioSettingPersistence)
            {
                return;
            }

            _settings.AsioDriverName = value.Name;
            RefreshAsioDependentOptions();
            AppSettingsStore.TrySave(_settings);
            ApplyAsioSettingChange();
        }
    }

    public AsioSampleRateOption SelectedAsioSampleRateOption
    {
        get => _selectedAsioSampleRateOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetField(ref _selectedAsioSampleRateOption, value) || _suppressAsioSettingPersistence)
            {
                return;
            }

            _settings.AsioSampleRate = value.Hertz;
            AppSettingsStore.TrySave(_settings);
            ApplyAsioSettingChange();
        }
    }

    public AsioBufferSizeOption SelectedAsioBufferSizeOption
    {
        get => _selectedAsioBufferSizeOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetField(ref _selectedAsioBufferSizeOption, value) || _suppressAsioSettingPersistence)
            {
                return;
            }

            _settings.AsioBufferSize = value.Frames;
            AppSettingsStore.TrySave(_settings);
            ApplyAsioSettingChange();
        }
    }

    public AsioOutputChannelOption SelectedAsioOutputChannelOption
    {
        get => _selectedAsioOutputChannelOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetField(ref _selectedAsioOutputChannelOption, value) || _suppressAsioSettingPersistence)
            {
                return;
            }

            _settings.AsioOutputChannelOffset = value.Offset;
            AppSettingsStore.TrySave(_settings);
            ApplyAsioSettingChange();
        }
    }

    private void ApplyMidiDeviceLists(
        IReadOnlyList<string> inputNames,
        IReadOnlyList<string> outputNames,
        string? errorMessage)
    {
        if (_disposed)
        {
            return;
        }

        var previousInput = SelectedInputPort;
        var previousOutput = SelectedOutputPort;

        if (errorMessage is not null)
        {
            UpdateMidiPortListsWithoutSaving(
                new[] { NoMidiInputName },
                new[] { MidiPortService.BuiltInTrioOutputName },
                NoMidiInputName,
                MidiPortService.BuiltInTrioOutputName);
            StatusText = errorMessage;
            return;
        }

        var availableInputNames = inputNames.Count == 0
            ? new[] { NoMidiInputName }
            : inputNames;
        var availableOutputNames = outputNames.Count == 0
            ? new[] { MidiPortService.BuiltInTrioOutputName }
            : outputNames;

        var inputSelection = FindMatchingPortName(availableInputNames, _preferredInputPort)
            ?? FindMatchingPortName(availableInputNames, previousInput)
            ?? NoMidiInputName;
        var firstPreferredSynth = availableOutputNames.FirstOrDefault(MidiPortService.IsMicrosoftGsWavetableSynth)
            ?? availableOutputNames.FirstOrDefault(MidiPortService.IsFluidSynth);
        var preferredOutput = FindMatchingPortName(availableOutputNames, _preferredOutputPort);
        var builtInOutput = FindMatchingPortName(availableOutputNames, MidiPortService.BuiltInTrioOutputName);
        // Built-in Trio is the safe default on both Windows and macOS. An
        // explicitly saved external port still wins when it is available.
        var outputSelection = preferredOutput
            ?? builtInOutput
            ?? firstPreferredSynth
            ?? availableOutputNames.FirstOrDefault()
            ?? MidiPortService.BuiltInTrioOutputName;
        var inputChanged = !string.Equals(previousInput, inputSelection, StringComparison.Ordinal);
        var outputChanged = !string.Equals(previousOutput, outputSelection, StringComparison.Ordinal);
        UpdateMidiPortListsWithoutSaving(
            availableInputNames,
            availableOutputNames,
            inputSelection,
            outputSelection);
        if ((inputChanged || outputChanged) && (PortsOpen || IsSessionRunning))
        {
            ApplyMidiPortChange(inputChanged);
        }
        StatusText = "MIDI devices refreshed.";
    }

    private void UpdateMidiPortListsWithoutSaving(
        IEnumerable<string> inputNames,
        IEnumerable<string> outputNames,
        string? input,
        string? output)
    {
        var wasPersistenceSuppressed = _suppressMidiPortPersistence;
        _suppressMidiPortPersistence = true;
        try
        {
            // Clearing a bound collection can make the ComboBox select a temporary
            // fallback item. Keep the whole refresh transaction non-persistent so
            // that this UI transition never replaces the user's saved port names.
            ReplaceItems(InputPorts, inputNames);
            ReplaceItems(OutputPorts, outputNames);
            _selectedInputPort = input;
            _selectedOutputPort = output;

            // Assign the canonical instances taken from the refreshed collections,
            // then explicitly synchronize the controls after all collection events.
            OnPropertyChanged(nameof(SelectedInputPort));
            OnPropertyChanged(nameof(SelectedOutputPort));
        }
        finally
        {
            _suppressMidiPortPersistence = wasPersistenceSuppressed;
        }
    }

    private static string? FindMatchingPortName(IEnumerable<string> portNames, string? requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return null;
        }

        return portNames.FirstOrDefault(name =>
            string.Equals(name, requestedName, StringComparison.Ordinal));
    }

    private void ApplyMidiPortChange(bool inputChanged)
    {
        if (!PortsOpen && !IsSessionRunning)
        {
            return;
        }

        if (!inputChanged)
        {
            try
            {
                var output = string.IsNullOrWhiteSpace(SelectedOutputPort)
                    ? MidiPortService.BuiltInTrioOutputName
                    : SelectedOutputPort;
                _midiPortService.SwitchOutput(output, sendProgramChanges: true);
                PortsOpen = true;
                LiveStatusText = FormatLiveStatus();
                StatusText = IsSessionRunning
                    ? "MIDI output switched; session playback continues on the selected device."
                    : "MIDI output switched.";
            }
            catch (Exception ex)
            {
                StatusText = $"Could not switch MIDI output: {ex.Message}";
            }
            return;
        }

        OpenMidi();
        if (!PortsOpen)
        {
            return;
        }

        if (inputChanged)
        {
            _performanceAnalyzer.Reset();
            _headOutDetector.Reset();
            _headOutDetector.Configure(EffectiveThemeReturnSensitivity());
            ResetEnergyDisplay();
        }

        LiveStatusText = FormatLiveStatus();
        StatusText = IsSessionRunning
            ? "MIDI ports switched; session playback continues on the selected devices."
            : "MIDI ports switched.";
    }

    private void OpenMidi()
    {
        try
        {
            var input = SelectedInputPort == NoMidiInputName ? null : SelectedInputPort;
            var output = string.IsNullOrWhiteSpace(SelectedOutputPort)
                ? MidiPortService.BuiltInTrioOutputName
                : SelectedOutputPort;

            _midiPortService.Open(
                input,
                output,
                sendProgramChanges: true,
                midiThruEnabled: MidiThruEnabled && !string.IsNullOrWhiteSpace(input));
            PortsOpen = true;
            LiveStatusText = FormatLiveStatus();
            StatusText = MidiThruEnabled && !string.IsNullOrWhiteSpace(input)
                ? "MIDI is open. MIDI Thru routes incoming notes to Ch.1 Vibraphone."
                : "MIDI is open.";
        }
        catch (Exception ex)
        {
            PortsOpen = false;
            LiveStatusText = "MIDI could not be opened.";
            StatusText = $"Could not open MIDI: {ex.Message}";
        }
    }

    private void CloseMidi()
    {
        StopSession();
        _midiPortService.Close();
        PortsOpen = false;
        LiveStatusText = "MIDI is closed.";
        StatusText = "MIDI closed.";
    }

    private void StartSession()
    {
        if (IsSessionRunning)
        {
            ReturnToTheme();
            return;
        }

        try
        {
            if (!PortsOpen || !_midiPortService.IsOutputOpen)
            {
                OpenMidi();
            }

            if (!_midiPortService.IsOutputOpen)
            {
                StatusText = "Open a MIDI output before starting the session.";
                return;
            }

            _playbackController.SelectTune(_activeTune);
            _playbackController.SetAutomaticChorusPlanEnabled(true);
            _performanceAnalyzer.Reset();
            ResetEnergyDisplay();
            _headOutDetector.Reset();
            _headOutDetector.Configure(EffectiveThemeReturnSensitivity());
            ThemeReturnStatusText = string.Empty;
            _playbackController.Start(TempoBpm);
            IsSessionRunning = true;
            _snapshotTimer.Start();
            RefreshPlaybackSnapshot();
            StatusText = $"Session started at {TempoBpm} BPM.";
        }
        catch (Exception ex)
        {
            StopSession();
            StatusText = $"Could not start session playback: {ex.Message}";
        }
    }

    public void ToggleSessionOrQueueHead() => StartSession();

    private void StopSession()
    {
        _snapshotTimer.Stop();
        _playbackController.Stop();
        _performanceAnalyzer.Reset();
        ResetEnergyDisplay();
        _headOutDetector.Reset();
        IsSessionRunning = false;
        if (_midiPortService.IsOutputOpen)
        {
            _midiPortService.Panic();
        }

        RefreshPlaybackSnapshot();
        if (PortsOpen)
        {
            LiveStatusText = FormatLiveStatus();
        }
    }

    private void Panic()
    {
        StopSession();
        if (_midiPortService.IsOutputOpen)
        {
            _midiPortService.Panic();
            StatusText = "PANIC sent.";
        }
        else
        {
            StatusText = "No MIDI output is open.";
        }
    }

    private void RefreshPlaybackSnapshot()
    {
        var snapshot = _playbackController.GetSnapshot();
        OnPropertyChanged(nameof(PrimarySessionButtonText));
        var desiredEndingForm = !_activeTune.HasCoda &&
            snapshot.Phase is SessionPlaybackPhase.Playing or SessionPlaybackPhase.Ending &&
            snapshot.UsingEndingForm;
        if (_chordSheetUsesEndingForm != desiredEndingForm)
        {
            BuildChordSheetRows(desiredEndingForm);
        }

        PlaybackStatusText = snapshot.Phase switch
        {
            SessionPlaybackPhase.CountIn =>
                $"COUNT-IN  bar {snapshot.CountInBar}/{(TempoBpm < 80 ? 1 : 2)}  beat {snapshot.CountInBeat}",
            SessionPlaybackPhase.Playing =>
                $"{(snapshot.HeadOutActive ? "HEAD OUT" : "PLAYING")}  chorus {snapshot.Chorus}  bar {snapshot.Bar}/{snapshot.FormBarCount}  beat {snapshot.Beat}  {snapshot.Section}  {ChordSymbolDisplay.Format(snapshot.Chord)}  {snapshot.Arrangement}",
            SessionPlaybackPhase.Ending =>
                $"ENDING  chorus {snapshot.Chorus}  bar {snapshot.Bar}/{snapshot.FormBarCount}  beat {snapshot.Beat}  {ChordSymbolDisplay.Format(snapshot.Chord)}",
            _ => "Stopped."
        };
        ArrangementStageText = DescribeArrangementStage(snapshot);
        CurrentChordText = snapshot.Phase is SessionPlaybackPhase.Playing or SessionPlaybackPhase.Ending
            ? ChordSymbolDisplay.Format(snapshot.Chord)
            : "-";
        NextChordText = GetNextChordText(snapshot);
        UpdateChordSheetHighlight(snapshot);
    }

    private void BuildChordSheetRows(bool useEndingForm)
    {
        ChordRows.Clear();
        CodaRows.Clear();
        _chordSheetUsesEndingForm = useEndingForm;
        _lastChordSheetBar = int.MinValue;
        _lastChordSheetBeat = int.MinValue;
        _lastChordSheetPhase = (SessionPlaybackPhase)(-1);

        var bars = useEndingForm && _activeTune.HasCoda
            ? _activeTune.EndingFormBars.Take(_activeTune.CodaStartIndex!.Value).ToArray()
            : useEndingForm
                ? _activeTune.EndingFormBars
                : _activeTune.Bars;
        AddChordSheetRows(
            ChordRows,
            bars,
            showCodaMarker: !useEndingForm,
            showLoopMarkers: true,
            reserveLoopMarkerSpace: true,
            suppressSectionLabels: false,
            displayIndexOffset: 0,
            loopStartIndex: useEndingForm ? 0 : _activeTune.LoopStartBarIndex,
            firstRowFallbackSectionLabel: "Head");

        if (_activeTune.HasCoda)
        {
            AddChordSheetRows(
                CodaRows,
                _activeTune.CodaBars,
                showCodaMarker: false,
                showLoopMarkers: false,
                reserveLoopMarkerSpace: true,
                suppressSectionLabels: true,
                displayIndexOffset: useEndingForm ? _activeTune.CodaStartIndex!.Value : 0,
                markCodaStartMarker: true,
                firstRowSectionLabel: "Ending");
        }

        ApplyChordSheetScale();
        OnPropertyChanged(nameof(ShowCodaPreview));
        OnPropertyChanged(nameof(ChordSheetRowCount));
    }

    private void AddChordSheetRows(
        ObservableCollection<ChordSheetRowViewModel> target,
        IReadOnlyList<TuneBar> bars,
        bool showCodaMarker,
        bool showLoopMarkers,
        bool reserveLoopMarkerSpace,
        bool suppressSectionLabels,
        int displayIndexOffset,
        int loopStartIndex = 0,
        bool markCodaStartMarker = false,
        string? firstRowSectionLabel = null,
        string? firstRowFallbackSectionLabel = null)
    {
        for (var rowStart = 0; rowStart < bars.Count;)
        {
            var rowLength = Math.Min(4, bars.Count - rowStart);
            if (!suppressSectionLabels)
            {
                for (var offset = 1; offset < rowLength; offset++)
                {
                    if (!string.Equals(
                            bars[rowStart + offset].Section,
                            bars[rowStart + offset - 1].Section,
                            StringComparison.Ordinal))
                    {
                        rowLength = offset;
                        break;
                    }
                }
            }

            var rowBars = bars.Skip(rowStart).Take(rowLength).ToArray();
            var rowSection = rowBars[0].Section;
            var sectionStartsHere = !suppressSectionLabels &&
                (rowStart == 0 ||
                 !string.Equals(rowSection, bars[rowStart - 1].Section, StringComparison.Ordinal));
            var cells = rowBars
                .Select((bar, column) =>
                {
                    var barIndex = rowStart + column;
                    var displayIndex = displayIndexOffset + barIndex;
                    var sectionStartsInsideRow = !suppressSectionLabels && column > 0 &&
                        !string.Equals(bar.Section, bars[barIndex - 1].Section, StringComparison.Ordinal);
                    var hasCodaMarker = showCodaMarker && _activeTune.CodaJumpBarIndex == displayIndex;
                    var hasCodaStartMarker = markCodaStartMarker && rowStart == 0 && column == 0;
                    var reserveLoopStartSpace = reserveLoopMarkerSpace && column == 0;
                    var reserveLoopEndSpace = reserveLoopMarkerSpace && column == rowLength - 1;
                    var hasLoopStartMarker = showLoopMarkers && reserveLoopStartSpace && displayIndex == loopStartIndex;
                    var hasLoopEndMarker = showLoopMarkers && reserveLoopEndSpace && barIndex == bars.Count - 1;
                    return new ChordSheetCellViewModel(
                        displayIndex,
                        bar,
                        sectionStartsInsideRow,
                        _chordSheetBarWidth,
                        hasCodaMarker,
                        hasCodaStartMarker,
                        hasLoopStartMarker,
                        hasLoopEndMarker,
                        reserveLoopStartSpace,
                        reserveLoopEndSpace);
                })
                .ToArray();

            var sectionLabel = sectionStartsHere ? rowSection : string.Empty;
            if (rowStart == 0 && !string.IsNullOrWhiteSpace(firstRowSectionLabel))
            {
                sectionLabel = firstRowSectionLabel;
            }
            else if (rowStart == 0 && string.IsNullOrWhiteSpace(sectionLabel) &&
                     !string.IsNullOrWhiteSpace(firstRowFallbackSectionLabel))
            {
                sectionLabel = firstRowFallbackSectionLabel;
            }

            target.Add(new ChordSheetRowViewModel(sectionLabel, cells));
            rowStart += rowLength;
        }
    }

    private void ApplyChordSheetScale()
    {
        var scaleFactor = ChordSheetScaleFactor;
        foreach (var row in ChordRows.Concat(CodaRows))
        {
            row.SetScaleFactor(scaleFactor);
        }
    }

    private void UpdateChordSheetHighlight(SessionPlaybackSnapshot snapshot)
    {
        var currentBarIndex = snapshot.Phase is SessionPlaybackPhase.Playing or SessionPlaybackPhase.Ending
            ? snapshot.Bar - 1
            : -1;
        var currentBeatIndex = currentBarIndex >= 0 ? Math.Max(0, snapshot.Beat - 1) : -1;

        if (_lastChordSheetBar == currentBarIndex &&
            _lastChordSheetBeat == currentBeatIndex &&
            _lastChordSheetPhase == snapshot.Phase)
        {
            return;
        }

        var previousBarIndex = _lastChordSheetBar;
        _lastChordSheetBar = currentBarIndex;
        _lastChordSheetBeat = currentBeatIndex;
        _lastChordSheetPhase = snapshot.Phase;

        var mainCells = ChordRows.SelectMany(row => row.Cells).ToArray();
        var codaCells = CodaRows.SelectMany(row => row.Cells).ToArray();
        var endingPlaybackOnInitialSheet = _activeTune.HasCoda &&
            !_chordSheetUsesEndingForm &&
            snapshot.Phase is SessionPlaybackPhase.Playing or SessionPlaybackPhase.Ending &&
            snapshot.UsingEndingForm;
        var mainCurrentBar = -1;
        var mainNextBar = -1;
        var codaCurrentBar = -1;
        var codaNextBar = -1;

        if (_chordSheetUsesEndingForm)
        {
            var cells = mainCells.Concat(codaCells).ToArray();
            var nextBarIndex = -1;
            if (currentBarIndex >= 0 && cells.Length > 0)
            {
                nextBarIndex = snapshot.Phase == SessionPlaybackPhase.Ending
                    ? Math.Min(currentBarIndex + 1, cells.Length - 1)
                    : (currentBarIndex + 1) % cells.Length;
            }
            else if (snapshot.Phase == SessionPlaybackPhase.CountIn && cells.Length > 0)
            {
                nextBarIndex = 0;
            }

            mainCurrentBar = currentBarIndex;
            mainNextBar = nextBarIndex;
            codaCurrentBar = currentBarIndex;
            codaNextBar = nextBarIndex;
        }
        else if (endingPlaybackOnInitialSheet)
        {
            var endingBarIndex = currentBarIndex;
            var codaStartIndex = _activeTune.CodaStartIndex!.Value;
            if (endingBarIndex >= 0 && endingBarIndex < codaStartIndex)
            {
                mainCurrentBar = endingBarIndex;
                if (endingBarIndex + 1 < codaStartIndex)
                {
                    mainNextBar = endingBarIndex + 1;
                }
                else if (codaCells.Length > 0)
                {
                    codaNextBar = 0;
                }
            }
            else if (endingBarIndex >= codaStartIndex)
            {
                codaCurrentBar = snapshot.Phase == SessionPlaybackPhase.Ending
                    ? codaCells.Length - 1
                    : endingBarIndex - codaStartIndex;
                if (codaCurrentBar >= 0 && codaCurrentBar + 1 < codaCells.Length)
                {
                    codaNextBar = codaCurrentBar + 1;
                }
            }
        }
        else
        {
            mainCurrentBar = currentBarIndex;
            if (currentBarIndex >= 0 && mainCells.Length > 0)
            {
                mainNextBar = currentBarIndex + 1 < mainCells.Length
                    ? currentBarIndex + 1
                    : Math.Clamp(_activeTune.LoopStartBarIndex, 0, mainCells.Length - 1);
            }
            else if (snapshot.Phase == SessionPlaybackPhase.CountIn && mainCells.Length > 0)
            {
                mainNextBar = 0;
            }
        }

        SetPlaybackStates(mainCells, mainCurrentBar, mainNextBar, currentBeatIndex);
        SetPlaybackStates(codaCells, codaCurrentBar, codaNextBar, currentBeatIndex);

        var rowBarIndex = currentBarIndex;
        if (endingPlaybackOnInitialSheet &&
            snapshot.Phase == SessionPlaybackPhase.Ending &&
            codaCells.Length > 0)
        {
            rowBarIndex = _activeTune.CodaStartIndex!.Value + codaCells.Length - 1;
        }

        var targetRow = rowBarIndex >= 0
            ? GetChordSheetRowIndex(rowBarIndex, endingPlaybackOnInitialSheet)
            : 0;
        var previousRow = previousBarIndex >= 0
            ? GetChordSheetRowIndex(previousBarIndex, endingPlaybackOnInitialSheet)
            : -1;
        if (targetRow != previousRow || snapshot.Phase == SessionPlaybackPhase.CountIn)
        {
            ChordSheetRowChanged?.Invoke(targetRow);
        }
    }

    private static void SetPlaybackStates(
        IReadOnlyList<ChordSheetCellViewModel> cells,
        int currentBarIndex,
        int nextBarIndex,
        int currentBeatIndex)
    {
        foreach (var cell in cells)
        {
            var isCurrentBar = cell.DisplayIndex == currentBarIndex;
            var isNextBar = !isCurrentBar && cell.DisplayIndex == nextBarIndex;
            var chordIndex = isCurrentBar ? cell.GetChordIndex(currentBeatIndex) : -1;
            cell.SetPlaybackState(isCurrentBar, isNextBar, chordIndex);
        }
    }

    private int GetChordSheetRowIndex(int displayBarIndex, bool endingPlaybackOnInitialSheet)
    {
        if (endingPlaybackOnInitialSheet)
        {
            var codaStartIndex = _activeTune.CodaStartIndex!.Value;
            return displayBarIndex < codaStartIndex
                ? displayBarIndex / 4
                : ChordRows.Count + (displayBarIndex - codaStartIndex) / 4;
        }

        if (!_chordSheetUsesEndingForm || !_activeTune.HasCoda)
        {
            return displayBarIndex / 4;
        }

        var endingCodaStartIndex = _activeTune.CodaStartIndex!.Value;
        return displayBarIndex < endingCodaStartIndex
            ? displayBarIndex / 4
            : ChordRows.Count + (displayBarIndex - endingCodaStartIndex) / 4;
    }

    private void MidiPortService_MessageReceived(object? sender, MidiInputMessage message)
    {
        if (message.Channel is byte channel && message.NoteNumber is byte note)
        {
            if (message.IsNoteOn && message.Velocity is byte velocity)
            {
                _performanceAnalyzer.NoteOn(message.TimestampMilliseconds, channel, note, velocity);
            }
            else if (message.IsNoteOff)
            {
                _performanceAnalyzer.NoteOff(message.TimestampMilliseconds, channel, note);
            }
        }

        Dispatcher.UIThread.Post(() => LiveStatusText = FormatLiveStatus());
    }

    private void MidiPortService_DeviceError(object? sender, string message) =>
        Dispatcher.UIThread.Post(() => StatusText = message);

    private void PlaybackController_PlaybackError(object? sender, string message) =>
        Dispatcher.UIThread.Post(() =>
        {
            StopSession();
            StatusText = message;
        });

    private void PlaybackController_SessionCompleted(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            _snapshotTimer.Stop();
            IsSessionRunning = false;
            _performanceAnalyzer.Reset();
            ResetEnergyDisplay();
            _headOutDetector.Reset();
            RefreshPlaybackSnapshot();
            StatusText = "Session completed.";
        });

    private void UpdatePerformanceInteraction()
    {
        if (!_playbackController.IsRunning)
        {
            return;
        }

        var nowMilliseconds = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
        var guidance = _performanceAnalyzer.Evaluate(nowMilliseconds, TempoBpm, _activeTune.BeatsPerBar);

        var snapshot = _playbackController.GetSnapshot();
        if (snapshot.Phase != SessionPlaybackPhase.Playing)
        {
            if (snapshot.Phase == SessionPlaybackPhase.CountIn)
            {
                ResetEnergyScope();
            }
            return;
        }

        if (_energyScopeChorus != snapshot.Chorus)
        {
            _energyScopeChorus = snapshot.Chorus;
            _energyScopeAttackFloor = _lastEnergyAttackMilliseconds;
        }
        var scopedEnergy = _performanceAnalyzer.EvaluateShortEnergySince(
            nowMilliseconds,
            TempoBpm,
            _activeTune.BeatsPerBar,
            _energyScopeAttackFloor);
        if (scopedEnergy.LastAttackMilliseconds > _lastEnergyAttackMilliseconds)
        {
            _lastEnergyAttackMilliseconds = scopedEnergy.LastAttackMilliseconds;
        }
        var detectionGuidance = guidance with
        {
            HasRecentInput = scopedEnergy.HasInput,
            ShortEnergy = scopedEnergy.Energy,
            LastAttackMilliseconds = scopedEnergy.LastAttackMilliseconds
        };

        var soloOneFinalTwoBars = snapshot.Chorus == 2 &&
            snapshot.Bar >= Math.Max(1, snapshot.FormBarCount - 1);
        var detectionEnabled = AutomaticThemeReturnEnabled &&
            (_headOutDetector.ConfirmationRecheckArmed ||
             (!snapshot.HeadOutActive && (snapshot.Chorus >= 3 || soloOneFinalTwoBars)));
        var decision = _headOutDetector.Update(
            nowMilliseconds,
            TempoBpm,
            snapshot.Chorus,
            snapshot.Bar,
            snapshot.CurrentFeel,
            detectionGuidance,
            snapshot.FormBarCount,
            detectionEnabled,
            _activeTune.BeatsPerBar);

        var diagnostics = _headOutDetector.Diagnostics;
        ReferenceEnergyPercent = diagnostics.ReferenceEnergy * 100;
        EndingEnergyPercent = diagnostics.EndingAverageEnergy * 100;
        ReturnThresholdPercent = diagnostics.EffectiveThreshold * 100;
        CancellationThresholdPercent = diagnostics.CancellationThreshold * 100;
        IsHeadOutRecheckActive = diagnostics.ConfirmationRecheckActive;

        if (detectionEnabled)
        {
            HandleHeadOutDecision(decision, snapshot);
        }
    }

    private void HandleHeadOutDecision(HeadOutDecision decision, SessionPlaybackSnapshot snapshot)
    {
        switch (decision.Type)
        {
            case HeadOutDecisionType.CandidateArmed:
                _playbackController.CancelPendingFeelChange();
                _playbackController.CancelPendingHighFourBeat();
                ThemeReturnStatusText = $"Candidate: checking Bars 1-2 of Chorus {decision.TargetChorus}";
                StatusText = $"Possible theme return detected; the previous Return limit is held through Bar 2 of Chorus {decision.TargetChorus}.";
                break;

            case HeadOutDecisionType.ConfirmNextChorus:
                if (decision.TargetChorus > snapshot.Chorus &&
                    _playbackController.ConfirmHeadOutAtNextChorus())
                {
                    ThemeReturnStatusText = $"Confirmed: theme returns at Chorus {decision.TargetChorus}";
                    StatusText = $"HEAD OUT confirmed. Chorus {decision.TargetChorus} will play as the final theme, then {_playbackController.Tune.TonicChord.Symbol} will be held for one bar.";
                }
                else if (decision.TargetChorus == snapshot.Chorus &&
                    _playbackController.ConfirmHeadOutNow())
                {
                    ThemeReturnStatusText = $"Confirmed: theme resumes in Chorus {snapshot.Chorus}";
                    StatusText = $"HEAD OUT was confirmed at the chorus boundary. Theme feel starts at the next bar, then {_playbackController.Tune.TonicChord.Symbol} will be held for one bar.";
                }
                break;

            case HeadOutDecisionType.ConfirmNow:
                if (_playbackController.ConfirmHeadOutNow())
                {
                    ThemeReturnStatusText = $"Confirmed: theme returns now in Chorus {snapshot.Chorus}";
                    StatusText = $"HEAD OUT confirmed. Theme feel starts at the next bar, then {_playbackController.Tune.TonicChord.Symbol} will be held for one bar.";
                }
                break;

            case HeadOutDecisionType.ConfirmedHeadOutCancelled:
                if (_playbackController.CancelConfirmedHeadOutAndResumePreviousLevel())
                {
                    ThemeReturnStatusText = "HEAD OUT cancelled";
                    StatusText = $"{decision.Description} Solo accompaniment resumes at the previous chorus level.";
                }
                break;

            case HeadOutDecisionType.CandidateCancelled:
            case HeadOutDecisionType.CandidateExpired:
                StatusText = decision.Description;
                ThemeReturnStatusText = string.Empty;
                break;
        }
    }

    private void ReturnToTheme()
    {
        var snapshot = _playbackController.GetSnapshot();
        var target = snapshot.Bar <= 2
            ? snapshot.Chorus
            : snapshot.Chorus + 1;

        if (_playbackController.ConfirmHeadOutAtNextChorus())
        {
            ThemeReturnStatusText = string.Empty;
            StatusText = $"Manual theme return scheduled for Chorus {target}.";
        }
    }

    private int EffectiveThemeReturnSensitivity() => Math.Clamp(
        ThemeReturnSensitivity +
        (_activeTune.AccompanimentStyle == AccompanimentStyle.JazzBallad ? 20 : 0),
        0,
        100);

    private void ResetEnergyScope()
    {
        _energyScopeChorus = 0;
        _energyScopeAttackFloor = long.MinValue;
        _lastEnergyAttackMilliseconds = long.MinValue;
    }

    private double ChordSheetScaleFactor => ChordSheetScale switch
    {
        0 => 0.6d,
        1 => 0.7d,
        2 => 0.8d,
        3 => 0.9d,
        4 => 1.0d,
        5 => 1.1d,
        6 => 1.2d,
        7 => 1.3d,
        8 => 1.4d,
        _ => 1.5d
    };

    private void ResetEnergyDisplay()
    {
        ReferenceEnergyPercent = 0;
        EndingEnergyPercent = 0;
        ReturnThresholdPercent = 0;
        CancellationThresholdPercent = 0;
        IsHeadOutRecheckActive = false;
    }

    private string DescribeArrangementStage(SessionPlaybackSnapshot snapshot)
        => snapshot.ArrangementStage;

    private string FormatLiveStatus()
    {
        var input = _midiPortService.IsInputOpen
            ? SelectedInputPort
            : "none";
        var output = _midiPortService.IsOutputOpen
            ? SelectedOutputPort
            : "none";
        return $"Input: {input}   Output: {output}";
    }

    private string GetNextChordText(SessionPlaybackSnapshot snapshot)
    {
        if (snapshot.Phase is not (SessionPlaybackPhase.Playing or SessionPlaybackPhase.Ending))
        {
            return "-";
        }

        var bars = snapshot.UsingEndingForm ? _activeTune.EndingFormBars : _activeTune.Bars;
        if (bars.Count == 0)
        {
            return "-";
        }

        var barIndex = Math.Clamp(snapshot.Bar - 1, 0, bars.Count - 1);
        var beatIndex = Math.Max(0, snapshot.Beat - 1);
        var bar = bars[barIndex];
        var nextChange = bar.ChordChanges.FirstOrDefault(change => change.StartBeat > beatIndex);
        if (nextChange is not null)
        {
            return ChordSymbolDisplay.Format(nextChange.Chord.Symbol);
        }

        var nextBarIndex = snapshot.Phase == SessionPlaybackPhase.Ending
            ? Math.Min(barIndex + 1, bars.Count - 1)
            : (barIndex + 1) % bars.Count;
        return ChordSymbolDisplay.Format(bars[nextBarIndex].ChordChanges.FirstOrDefault()?.Chord.Symbol ?? "-");
    }

    private static void ReplaceItems<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private static string ChannelName(byte channel) => channel switch
    {
        SessionConstants.VibraphoneChannel => "Vibraphone",
        SessionConstants.BassChannel => "Bass",
        SessionConstants.PianoChannel => "Piano",
        SessionConstants.DrumsChannel => "Drums",
        _ => $"Ch.{channel + 1}"
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void SetInstrumentVolume(ref int field, int value, byte channel, [CallerMemberName] string? propertyName = null)
    {
        var clamped = Math.Clamp(value, 0, 100);
        if (SetField(ref field, clamped, propertyName))
        {
            switch (channel)
            {
                case SessionConstants.PianoChannel:
                    _settings.PianoVolume = clamped;
                    break;
                case SessionConstants.BassChannel:
                    _settings.BassVolume = clamped;
                    break;
                case SessionConstants.DrumsChannel:
                    _settings.DrumsVolume = clamped;
                    break;
                case SessionConstants.VibraphoneChannel:
                    _settings.VibraphoneVolume = clamped;
                    break;
            }
            AppSettingsStore.TrySave(_settings);
            _midiPortService.SetChannelVolume(channel, (byte)Math.Round(clamped * 127.0 / 100.0));
        }
    }

    private void ApplyMixerSettings()
    {
        _midiPortService.SetChannelVolume(
            SessionConstants.PianoChannel,
            (byte)Math.Round(_pianoVolume * 127.0 / 100.0));
        _midiPortService.SetChannelVolume(
            SessionConstants.BassChannel,
            (byte)Math.Round(_bassVolume * 127.0 / 100.0));
        _midiPortService.SetChannelVolume(
            SessionConstants.DrumsChannel,
            (byte)Math.Round(_drumsVolume * 127.0 / 100.0));
        _midiPortService.SetChannelVolume(
            SessionConstants.VibraphoneChannel,
            (byte)Math.Round(_vibraphoneVolume * 127.0 / 100.0));
        _midiPortService.SetChannelMute(SessionConstants.PianoChannel, !_pianoEnabled);
        _midiPortService.SetChannelMute(SessionConstants.BassChannel, !_bassEnabled);
        _midiPortService.SetChannelMute(SessionConstants.DrumsChannel, !_drumsEnabled);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record TuneOption(TuneForm Tune, string? FilePath = null)
{
    public string Title => Tune.Title;
    public string DisplayTitle => Title;
    public string? FileName => string.IsNullOrWhiteSpace(FilePath) ? null : Path.GetFileName(FilePath);
    public override string ToString() => DisplayTitle;
}

public sealed record KeyOption(string DisplayName, int PitchClass, bool IsMinor, bool PreferFlats);

public sealed record AccidentalOption(string DisplayName, bool? PreferFlats)
{
    public bool IsAuto => PreferFlats is null;

    public static AccidentalOption Auto { get; } = new("Auto", null);
    public static AccidentalOption Flats { get; } = new("Flat (b)", true);
    public static AccidentalOption Sharps { get; } = new("Sharp (#)", false);
}

public sealed record WindowsAudioBackendOption(string Name, string DisplayName, AsioAudioBackend Backend)
{
    public static WindowsAudioBackendOption Automatic { get; } =
        new("Automatic", "Automatic (ASIO ? WinMM)", AsioAudioBackend.Automatic);

    public static WindowsAudioBackendOption Asio { get; } =
        new("ASIO", "ASIO", AsioAudioBackend.Asio);

    public static WindowsAudioBackendOption WinMm { get; } =
        new("WinMM", "WinMM (Windows default audio)", AsioAudioBackend.WinMm);

    public static WindowsAudioBackendOption FromName(string? name) =>
        name switch
        {
            not null when name.Equals("ASIO", StringComparison.OrdinalIgnoreCase) => Asio,
            not null when name.Equals("WinMM", StringComparison.OrdinalIgnoreCase) => WinMm,
            _ => Automatic
        };
}

public sealed record AsioDriverOption(string? Name, string DisplayName)
{
    public static AsioDriverOption Automatic { get; } = new(null, "(Automatic ASIO driver)");

    public static AsioDriverOption FromName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? Automatic : new(name, name);
}

public sealed record AsioSampleRateOption(int Hertz)
{
    public string DisplayName => $"{Hertz / 1_000d:0.###} kHz";
}

public sealed record AsioBufferSizeOption(int Frames)
{
    public string DisplayName => Frames <= 0 ? "Driver preferred" : $"{Frames} samples";
}

public sealed record AsioOutputChannelOption(int Offset, string DisplayName);

public sealed record StyleOption(string DisplayName, AccompanimentStyle? OverrideStyle)
{
    public static StyleOption DefaultFor(TuneForm tune) =>
        new($"Default ({AccompanimentStyleNames.DisplayName(tune.AccompanimentStyle)})", null);

    public static StyleOption Override(AccompanimentStyle style) =>
        new(AccompanimentStyleNames.DisplayName(style), style);

    public TuneForm Resolve(TuneForm tune) =>
        OverrideStyle is null || OverrideStyle == tune.AccompanimentStyle
            ? tune
            : tune.WithAccompanimentStyle(OverrideStyle.Value);
}

public sealed class ChordSheetRowViewModel : INotifyPropertyChanged
{
    private double _scaleFactor = 1d;

    public ChordSheetRowViewModel(string sectionLabel, IReadOnlyList<ChordSheetCellViewModel> cells)
    {
        SectionLabel = sectionLabel;
        Cells = cells;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SectionLabel { get; }
    public bool HasSectionLabel => !string.IsNullOrWhiteSpace(SectionLabel);
    public double SectionLabelFontSize => SectionLabel.Length switch
    {
        <= 3 => 18d,
        <= 5 => 13d,
        _ => 12d
    };
    public IReadOnlyList<ChordSheetCellViewModel> Cells { get; }
    public double RowHeight => 58d * _scaleFactor + 4d;

    public void SetScaleFactor(double scaleFactor)
    {
        if (Math.Abs(_scaleFactor - scaleFactor) < 0.001d)
        {
            return;
        }

        _scaleFactor = scaleFactor;
        OnPropertyChanged(nameof(RowHeight));
        foreach (var cell in Cells)
        {
            cell.SetScaleFactor(_scaleFactor);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ChordSheetCellViewModel : INotifyPropertyChanged
{
    public const double LoopMarkerGutter = 16d;
    private const double BaseCodaMarkerWidth = 22d;
    private const double BaseCodaMarkerHeight = 30d;
    private const double CodaMarkerChordGap = 4d;

    private static readonly IBrush CellBackgroundBrush = Brushes.White;
    private static readonly IBrush CurrentBarBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xF3, 0xF1));
    private static readonly IBrush CurrentChordBrush = new SolidColorBrush(Color.FromRgb(0x0B, 0x6E, 0x69));
    private static readonly IBrush NextBarBrush = new SolidColorBrush(Color.FromRgb(0x78, 0xAF, 0xAC));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xE0, 0xE2));
    private static readonly IBrush SectionTagBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xF3, 0xF1));

    private readonly int _beatsPerBar;
    private bool _isCurrentBar;
    private bool _isNextBar;
    private double _barWidth;
    private double _scaleFactor = 1d;

    public ChordSheetCellViewModel(
        int displayIndex,
        TuneBar bar,
        bool sectionStartsInsideRow,
        double barWidth,
        bool hasCodaMarker = false,
        bool hasCodaStartMarker = false,
        bool hasLoopStartMarker = false,
        bool hasLoopEndMarker = false,
        bool reserveLoopStartSpace = false,
        bool reserveLoopEndSpace = false)
    {
        DisplayIndex = displayIndex;
        SectionTag = sectionStartsInsideRow ? bar.Section : string.Empty;
        HasCodaMarker = hasCodaMarker;
        HasCodaStartMarker = hasCodaStartMarker;
        HasLoopStartMarker = hasLoopStartMarker;
        HasLoopEndMarker = hasLoopEndMarker;
        ReserveLoopStartSpace = reserveLoopStartSpace;
        ReserveLoopEndSpace = reserveLoopEndSpace;
        _beatsPerBar = bar.BeatsPerBar;
        _barWidth = barWidth;
        Chords = bar.ChordChanges
            .Select((change, index) =>
            {
                var nextBeat = index + 1 < bar.ChordChanges.Count
                    ? bar.ChordChanges[index + 1].StartBeat
                    : bar.BeatsPerBar;
                var span = nextBeat - change.StartBeat;
                return new ChordSheetChordViewModel(
                    index,
                    change.StartBeat,
                    change.Chord.Symbol,
                    span,
                    bar.ChordChanges.Count,
                    _barWidth,
                    _beatsPerBar);
            })
            .ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int DisplayIndex { get; }
    public string SectionTag { get; }
    public bool HasSectionTag => !string.IsNullOrWhiteSpace(SectionTag);
    public bool HasCodaMarker { get; }
    public bool HasCodaStartMarker { get; }
    public bool HasAnyCodaMarker => HasCodaMarker || HasCodaStartMarker;
    public bool HasLoopStartMarker { get; }
    public bool HasLoopEndMarker { get; }
    public bool ReserveLoopStartSpace { get; }
    public bool ReserveLoopEndSpace { get; }
    public Thickness LoopMarkerPadding => new(
        ReserveLoopStartSpace ? LoopMarkerGutter : 0,
        0,
        ReserveLoopEndSpace ? LoopMarkerGutter : 0,
        0);
    public double FrameWidth => _barWidth + LoopMarkerPadding.Left + LoopMarkerPadding.Right;
    public Thickness LoopBarMargin => new(LoopMarkerPadding.Left, 0, LoopMarkerPadding.Right, 0);
    public Thickness ChordItemsMargin => HasAnyCodaMarker
        ? new Thickness(
            0,
            0,
            HasCodaMarker ? (CodaMarkerWidth + CodaMarkerChordGap * _scaleFactor) : 0,
            0)
        : new Thickness(0);
    public Thickness CodaMarkerMargin => new(2d * _scaleFactor, 1d * _scaleFactor, 2d * _scaleFactor, 0);
    public Thickness CodaStartMarkerMargin => new(-CodaMarkerWidth, 1d * _scaleFactor, 0, 0);
    public double CodaMarkerWidth => BaseCodaMarkerWidth * _scaleFactor;
    public double CodaMarkerHeight => BaseCodaMarkerHeight * _scaleFactor;
    public IReadOnlyList<ChordSheetChordViewModel> Chords { get; }
    public double BarWidth => _barWidth;
    public double CellHeight => 58d * _scaleFactor;
    public IBrush Background => _isCurrentBar ? CurrentBarBrush : CellBackgroundBrush;
    public IBrush BorderBrush => _isCurrentBar ? CurrentChordBrush : _isNextBar ? NextBarBrush : LineBrush;
    public Thickness BorderThickness => _isCurrentBar ? new Thickness(2) : _isNextBar ? new Thickness(1.5) : new Thickness(1);
    public IBrush SectionTagBackground => SectionTagBrush;

    public void SetBarWidth(double barWidth)
    {
        if (Math.Abs(_barWidth - barWidth) < 0.5d)
        {
            return;
        }

        _barWidth = barWidth;
        OnPropertyChanged(nameof(BarWidth));
        OnPropertyChanged(nameof(FrameWidth));
        foreach (var chord in Chords)
        {
            chord.SetBarWidth(_barWidth, _beatsPerBar, Chords.Count);
        }
    }

    public void SetScaleFactor(double scaleFactor)
    {
        if (Math.Abs(_scaleFactor - scaleFactor) < 0.001d)
        {
            return;
        }

        _scaleFactor = scaleFactor;
        OnPropertyChanged(nameof(CellHeight));
        OnPropertyChanged(nameof(ChordItemsMargin));
        OnPropertyChanged(nameof(CodaMarkerMargin));
        OnPropertyChanged(nameof(CodaStartMarkerMargin));
        OnPropertyChanged(nameof(CodaMarkerWidth));
        OnPropertyChanged(nameof(CodaMarkerHeight));
        foreach (var chord in Chords)
        {
            chord.SetScaleFactor(_scaleFactor);
        }
    }

    public int GetChordIndex(int beatIndex)
    {
        var current = 0;
        foreach (var chord in Chords)
        {
            if (chord.StartBeat <= beatIndex)
            {
                current = chord.Index;
            }
        }

        return current;
    }

    public void SetPlaybackState(bool isCurrentBar, bool isNextBar, int currentChordIndex)
    {
        if (_isCurrentBar != isCurrentBar || _isNextBar != isNextBar)
        {
            _isCurrentBar = isCurrentBar;
            _isNextBar = isNextBar;
            OnPropertyChanged(nameof(Background));
            OnPropertyChanged(nameof(BorderBrush));
            OnPropertyChanged(nameof(BorderThickness));
        }

        foreach (var chord in Chords)
        {
            chord.IsCurrent = chord.Index == currentChordIndex;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

}

public sealed class ChordSheetChordViewModel : INotifyPropertyChanged
{
    private static readonly IBrush TransparentBrush = Brushes.Transparent;
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x23, 0x29));
    private static readonly IBrush CurrentChordBrush = new SolidColorBrush(Color.FromRgb(0x0B, 0x6E, 0x69));
    private static readonly IBrush CurrentTextBrush = Brushes.White;

    private readonly int _span;
    private readonly int _chordCount;
    private bool _isCurrent;
    private double _width;
    private double _fontSize;
    private double _scaleFactor = 1d;

    public ChordSheetChordViewModel(
        int index,
        int startBeat,
        string symbol,
        int span,
        int chordCount,
        double barWidth,
        int beatsPerBar)
    {
        Index = index;
        StartBeat = startBeat;
        Symbol = ChordSymbolDisplay.Format(symbol);
        _span = span;
        _chordCount = chordCount;
        _width = CalculateWidth(barWidth, beatsPerBar, _span);
        _fontSize = CalculateFontSize(Symbol, _width, _chordCount, _scaleFactor);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }
    public int StartBeat { get; }
    public string Symbol { get; }
    public double Width => _width;
    public double FontSize => _fontSize;
    public IBrush Background => _isCurrent ? CurrentChordBrush : TransparentBrush;
    public IBrush Foreground => _isCurrent ? CurrentTextBrush : TextBrush;
    public FontWeight FontWeight => _isCurrent ? FontWeight.Bold : FontWeight.SemiBold;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            OnPropertyChanged(nameof(Background));
            OnPropertyChanged(nameof(Foreground));
            OnPropertyChanged(nameof(FontWeight));
        }
    }

    public void SetBarWidth(double barWidth, int beatsPerBar, int chordCount)
    {
        var width = CalculateWidth(barWidth, beatsPerBar, _span);
        var fontSize = CalculateFontSize(Symbol, width, chordCount, _scaleFactor);
        if (Math.Abs(_width - width) >= 0.5d)
        {
            _width = width;
            OnPropertyChanged(nameof(Width));
        }

        if (Math.Abs(_fontSize - fontSize) >= 0.25d)
        {
            _fontSize = fontSize;
            OnPropertyChanged(nameof(FontSize));
        }
    }

    public void SetScaleFactor(double scaleFactor)
    {
        if (Math.Abs(_scaleFactor - scaleFactor) < 0.001d)
        {
            return;
        }

        _scaleFactor = scaleFactor;
        var fontSize = CalculateFontSize(Symbol, _width, _chordCount, _scaleFactor);
        if (Math.Abs(_fontSize - fontSize) >= 0.25d)
        {
            _fontSize = fontSize;
            OnPropertyChanged(nameof(FontSize));
        }
    }

    private static double CalculateWidth(double barWidth, int beatsPerBar, int span) =>
        Math.Max(30d, barWidth * span / Math.Max(1, beatsPerBar));

    private static double CalculateFontSize(string symbol, double width, int chordCount, double scaleFactor)
    {
        var maxFontSize = chordCount switch
        {
            1 => 22d,
            2 => 20d,
            3 => 18d,
            _ => 16d
        };
        var usableWidth = Math.Max(12d, width - 12d);
        var estimatedFit = usableWidth / (Math.Max(2, symbol.Length) * 0.56d);
        var scaledSize = Math.Floor(Math.Min(estimatedFit, maxFontSize) * scaleFactor * 2d) / 2d;
        return Math.Clamp(scaledSize, 8d, maxFontSize * scaleFactor);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal static class ChordSymbolDisplay
{
    private const string MajorTriangle = "\u25B3";

    public static string Format(string symbol)
    {
        var formatted = symbol;
        foreach (var extension in new[] { "13", "11", "9", "7" })
        {
            // iReal's m^7 / min^7 spelling is a minor-major seventh, not a
            // minor seventh.  The parser preserves that quality internally;
            // only the written symbol needs the conventional m?7 display.
            formatted = formatted
                .Replace($"mMaj{extension}", $"m{MajorTriangle}{extension}", StringComparison.OrdinalIgnoreCase)
                .Replace($"mM{extension}", $"m{MajorTriangle}{extension}", StringComparison.Ordinal)
                .Replace($"m^{extension}", $"m{MajorTriangle}{extension}", StringComparison.Ordinal)
                .Replace($"min^{extension}", $"m{MajorTriangle}{extension}", StringComparison.OrdinalIgnoreCase)
                .Replace($"-^{extension}", $"m{MajorTriangle}{extension}", StringComparison.Ordinal)
                .Replace($"maj{extension}", $"{MajorTriangle}{extension}", StringComparison.OrdinalIgnoreCase)
                .Replace($"M{extension}", $"{MajorTriangle}{extension}", StringComparison.Ordinal);
        }

        // A bare m^ is accepted as the compact minor-major-seven spelling.
        return formatted
            .Replace("min^", $"m{MajorTriangle}7", StringComparison.OrdinalIgnoreCase)
            .Replace("-^", $"m{MajorTriangle}7", StringComparison.Ordinal)
            .Replace("m^", $"m{MajorTriangle}7", StringComparison.Ordinal);
    }
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}
