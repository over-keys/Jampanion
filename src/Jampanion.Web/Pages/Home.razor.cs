using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using Jampanion.Core.Music;
using Jampanion.Web.Audio;
using Jampanion.Web.Models;
using Jampanion.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Jampanion.Web.Pages;

public class HomeLogic : ComponentBase, IAsyncDisposable
{
    private const string LegacyLocalSongsKey = "jampanion-web-songs-v1";
    private const string LocalSongIndexKey = "jampanion-web-song-index-v2";
    private const string LocalSongSourcePrefix = "jampanion-web-song-v2:";
    private readonly Dictionary<string, StoredWebSongMetadata> _localSongIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BuiltInSongMetadata> _builtInSongs = new(StringComparer.OrdinalIgnoreCase);
    private IJSObjectReference? _audioModule;
    private IJSObjectReference? _browserModule;
    private DotNetObjectReference<HomeLogic>? _dotNetReference;
    private CancellationTokenSource? _progressCancellation;
    private WebSessionPlan? _sessionPlan;
    private TuneForm _activeTune = null!;
    private string _savedSource = string.Empty;
    private string _savedSongSettingsSignature = string.Empty;
    private string _savedChartSignature = string.Empty;
    private double _positionSeconds;
    private int _sessionSeed;
    private double _launchPlanDurationSeconds;
    private bool _loadedBrowserStorage;
    private DateTimeOffset? _lowEnergySince;
    private DateTimeOffset? _lastMidiAttack;
    private int _sessionGenerationVersion;
    private int _styleGenerationVersion;
    private AccompanimentStyle _activePlaybackStyle = AccompanimentStyle.Swing;
    private AccompanimentStyle? _pendingPlaybackStyle;
    private double _pendingStyleBoundarySeconds = double.PositiveInfinity;
    private bool _chartFitRequested = true;
    private string? _loadedLocalSongId;
    private IReadOnlyList<WebChartRow> _chartRows = Array.Empty<WebChartRow>();

    [Inject]
    public IJSRuntime JS { get; set; } = default!;

    protected WebSongDocument Document { get; set; } = new();
    protected List<WebSongChoice> SongChoices { get; } = [];
    protected string SelectedSongId { get; set; } = string.Empty;
    protected string SongSearchText { get; set; } = string.Empty;
    protected string SelectedStyleValue { get; set; } = AccompanimentStyleNames.StorageName(AccompanimentStyle.Swing);
    protected string SelectedKey { get; set; } = "C";
    protected AccidentalPreference AccidentalPreference { get; set; } = AccidentalPreference.Auto;

    protected bool IsPlaying { get; set; }
    protected bool IsLoading { get; set; }
    protected bool IsImporting { get; set; }
    protected bool HeadOutQueued { get; set; }
    protected int? HeadOutChorus { get; set; }
    protected string StatusText { get; set; } = "Ready";
    protected string ChartStatusText { get; set; } = "Double-click a chord or rehearsal mark to edit.";
    protected bool HasValidationError { get; set; }

    protected bool AutomaticThemeReturnEnabled { get; set; }
    protected int ThemeReturnSensitivity { get; set; } = 50;
    protected double ReferenceEnergyPercent { get; set; }
    protected double CurrentEnergyPercent { get; set; }

    protected bool PianoEnabled { get; set; } = true;
    protected bool BassEnabled { get; set; } = true;
    protected bool DrumsEnabled { get; set; } = true;
    protected bool MidiThruEnabled { get; set; }
    protected int PianoVolume { get; set; } = 100;
    protected int BassVolume { get; set; } = 100;
    protected int DrumsVolume { get; set; } = 100;
    protected int VibraphoneVolume { get; set; } = 100;

    protected int ChordSheetScale { get; set; } = 100;
    protected bool SettingsOpen { get; set; }
    protected List<MidiInputChoice> MidiInputs { get; } = [];
    protected List<MidiOutputChoice> MidiOutputs { get; } = [];
    protected string SelectedMidiInputId { get; set; } = string.Empty;
    protected string SelectedMidiOutputId { get; set; } = string.Empty;
    protected string MidiStatusText { get; set; } = "Web MIDI is available in Chromium-based browsers when permission is granted.";

    protected int EditingChordBar { get; set; } = -1;
    protected int EditingChordBeat { get; set; } = -1;
    protected string ChordEditText { get; set; } = string.Empty;
    protected int EditingSectionBar { get; set; } = -1;
    protected string SectionEditText { get; set; } = string.Empty;
    protected int SectionStyleMenuBar { get; set; } = -1;
    protected bool EditingTitle { get; set; }
    protected string TitleEditText { get; set; } = string.Empty;
    protected bool TitleMenuOpen { get; set; }
    protected bool NewSongEditorOpen { get; set; }
    protected string NewSongTitleText { get; set; } = string.Empty;
    protected string NewSongBarCountText { get; set; } = "32";
    protected string NewSongValidationText { get; set; } = string.Empty;
    protected bool CanDeleteCurrentSong =>
        !IsPlaying && !IsImporting && !string.IsNullOrWhiteSpace(_loadedLocalSongId);

    protected IReadOnlyList<string> KeyChoices
    {
        get
        {
            var isMinor = IsMinorKey(Document.Key);
            var preferFlats = AccidentalPreference switch
            {
                AccidentalPreference.Flats => true,
                AccidentalPreference.Sharps => false,
                _ => Document.Key.Contains('b')
            };
            var roots = preferFlats
                ? new[] { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" }
                : new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            var suffix = isMinor ? "m" : string.Empty;
            var keys = roots.Select(root => root + suffix).ToList();
            if (!string.IsNullOrWhiteSpace(Document.Key) &&
                !keys.Contains(Document.Key, StringComparer.OrdinalIgnoreCase))
            {
                keys.Insert(0, Document.Key);
            }
            return keys;
        }
    }

    protected IReadOnlyList<WebStyleChoice> StyleChoices =>
        ExplicitStyleChoices
            .Select(choice => choice.Style == Document.Style
                ? choice with { DisplayName = $"{choice.DisplayName} (Default)" }
                : choice)
            .ToArray();

    protected string SelectedSongTitle =>
        SongChoices.FirstOrDefault(song =>
            string.Equals(song.Id, SelectedSongId, StringComparison.OrdinalIgnoreCase))?.Title
        ?? Document.Title;

    protected IReadOnlyList<WebStyleChoice> ExplicitStyleChoices => Document.TimeSignature == "3/4"
        ? [StyleChoice(AccompanimentStyle.JazzWaltz)]
        :
        [
            StyleChoice(AccompanimentStyle.Swing),
            StyleChoice(AccompanimentStyle.JazzBallad),
            StyleChoice(AccompanimentStyle.BossaNova),
            StyleChoice(AccompanimentStyle.AfroCubanLatin)
        ];

    protected string PrimarySessionButtonText => IsLoading
        ? "Loading…"
        : !IsPlaying
            ? "Start session"
            : HeadOutQueued
                ? "Head out queued"
                : "Back to head";

    protected string CurrentStage
    {
        get
        {
            if (!IsPlaying || _sessionPlan is null)
            {
                return "Stopped";
            }
            if (_positionSeconds < _sessionPlan.CountInSeconds)
            {
                return "Count In";
            }
            return _sessionPlan.Stages.FirstOrDefault(stage =>
                _positionSeconds >= stage.StartSeconds && _positionSeconds < stage.EndSeconds)?.Name ?? "Complete";
        }
    }

    protected int CurrentBarIndex
    {
        get
        {
            if (!IsPlaying || _sessionPlan is null || _positionSeconds < _sessionPlan.CountInSeconds)
            {
                return -1;
            }
            var musicalPosition = _positionSeconds - _sessionPlan.CountInSeconds;
            var globalBar = (int)(musicalPosition / _sessionPlan.BarDurationSeconds);
            return globalBar % Math.Max(1, _sessionPlan.BarsPerChorus);
        }
    }

    protected int CurrentBeatIndex
    {
        get
        {
            if (CurrentBarIndex < 0 || _sessionPlan is null)
            {
                return -1;
            }
            var musicalPosition = _positionSeconds - _sessionPlan.CountInSeconds;
            var positionWithinBar = musicalPosition % _sessionPlan.BarDurationSeconds;
            var beatDuration = _sessionPlan.BarDurationSeconds / Math.Max(1, _activeTune.BeatsPerBar);
            return Math.Clamp((int)(positionWithinBar / beatDuration), 0, _activeTune.BeatsPerBar - 1);
        }
    }

    protected int NextBarIndex
    {
        get
        {
            if (!IsPlaying || _sessionPlan is null || Document.Bars.Count == 0)
            {
                return -1;
            }
            if (_positionSeconds < _sessionPlan.CountInSeconds)
            {
                return 0;
            }
            return (CurrentBarIndex + 1) % Document.Bars.Count;
        }
    }

    protected IReadOnlyList<WebChartRow> ChartRows => _chartRows;

    protected string CurrentChord => ChordAt(CurrentBarIndex, CurrentBeatIndex);
    protected string NextChord
    {
        get
        {
            if (CurrentBarIndex < 0 || CurrentBeatIndex < 0)
            {
                return "–";
            }
            var nextBeat = CurrentBeatIndex + 1;
            var nextBar = CurrentBarIndex;
            if (nextBeat >= _activeTune.BeatsPerBar)
            {
                nextBeat = 0;
                nextBar = (nextBar + 1) % _activeTune.Bars.Count;
            }
            return ChordAt(nextBar, nextBeat);
        }
    }

    protected string PositionText => _sessionPlan is null
        ? "0:00"
        : $"{FormatTime(_positionSeconds)} / {FormatTime(_sessionPlan.DurationSeconds)}";

    protected double ProgressPercent => _sessionPlan is null || _sessionPlan.DurationSeconds <= 0
        ? 0
        : Math.Clamp(_positionSeconds / _sessionPlan.DurationSeconds * 100d, 0d, 100d);

    protected string ReferenceEnergyStyle => PercentageWidthStyle(ReferenceEnergyPercent);
    protected string CurrentEnergyStyle => PercentageWidthStyle(CurrentEnergyPercent);
    protected string ProgressStyle => PercentageWidthStyle(ProgressPercent);
    protected string ChartScaleStyle =>
        $"--chart-scale:{(ChordSheetScale / 100d).ToString("0.##", CultureInfo.InvariantCulture)}";

    protected void ChangeChordSheetScale(ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            ChordSheetScale = Math.Clamp(value, 60, 150);
            _chartFitRequested = true;
        }
    }

    protected string OriginalKeyText => string.IsNullOrWhiteSpace(Document.OriginalKey) ? "–" : Document.OriginalKey;
    protected string FormText => $"{Document.Bars.Count} bars · {(int)Math.Ceiling(Document.Bars.Count / 4d)} segments";
    protected string StyleStatusText
    {
        get
        {
            if (!IsPlaying)
            {
                return string.Empty;
            }

            var activeName = AccompanimentStyleNames.DisplayName(_activePlaybackStyle);
            return _pendingPlaybackStyle is AccompanimentStyle queued
                ? $"Playing: {activeName}   Queued: {AccompanimentStyleNames.DisplayName(queued)}"
                : $"Playing: {activeName}";
        }
    }
    protected bool HasUnsavedSongChanges =>
        !string.Equals(CurrentSongSettingsSignature(), _savedSongSettingsSignature, StringComparison.Ordinal);

    protected bool HasUnsavedChartChanges =>
        !string.Equals(CurrentChartSignature(), _savedChartSignature, StringComparison.Ordinal);

    protected override void OnInitialized()
    {
        // Enumerate only lightweight headers for the built-in library. The
        // complete embedded .cho body is opened only for the selected song.
        foreach (var metadata in LazyBuiltInSongCatalog.All)
        {
            _builtInSongs[metadata.Id] = metadata;
        }

        var startup = _builtInSongs.TryGetValue("autumn-leaves", out var autumnLeaves)
            ? autumnLeaves
            : _builtInSongs.Values.OrderBy(song => song.Title, StringComparer.OrdinalIgnoreCase).First();
        LoadSourceDocument(
            LazyBuiltInSongCatalog.ReadSource(startup),
            Path.GetFileNameWithoutExtension(startup.FileName),
            startup.Id,
            saveAsBaseline: true);
        RefreshSongChoices();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            var browserModule = await EnsureBrowserModuleAsync();
            await browserModule.InvokeVoidAsync("initializeSongSearch", "song-search");
            if (firstRender || _chartFitRequested)
            {
                _chartFitRequested = false;
                await browserModule.InvokeVoidAsync("fitChordLabels");
            }
            await browserModule.InvokeVoidAsync("keepCurrentChartRowVisible");
        }
        catch
        {
            // CSS fallback sizing remains available before the browser module loads.
        }

        if (!firstRender || _loadedBrowserStorage)
        {
            return;
        }

        _loadedBrowserStorage = true;
        try
        {
            var browser = await EnsureBrowserModuleAsync();
            _dotNetReference ??= DotNetObjectReference.Create(this);
            await browser.InvokeVoidAsync("registerGlobalShortcuts", _dotNetReference);
            // Read only the metadata index. A legacy all-in-one library is
            // migrated in JavaScript in small yielded batches so .NET/WASM never
            // deserializes every saved chart body during startup.
            await ReloadLocalSongIndexAsync(browser);
            RefreshSongChoices();
            await InvokeAsync(StateHasChanged);

            // Audio preparation is optional and starts only in genuine browser
            // idle time, never on the critical path to an interactive screen.
            await browser.InvokeVoidAsync("scheduleAudioPreload");
        }
        catch (Exception exception)
        {
            StatusText = $"Browser storage unavailable: {exception.Message}";
        }
    }


    protected async Task CommitSongSearchAsync(ChangeEventArgs args)
    {
        if (IsPlaying || IsImporting)
        {
            return;
        }

        var query = args.Value?.ToString()?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            SongSearchText = SelectedSongTitle;
            await BlurAsync("song-search");
            return;
        }

        // A datalist returns the visible title rather than the underlying item.
        // When a local import and a built-in chart share that title, prefer the
        // local item so the user's explicit library choice remains deletable.
        var exact = SongChoices.FirstOrDefault(song =>
            !song.IsBuiltIn &&
            string.Equals(song.Title, query, StringComparison.OrdinalIgnoreCase))
            ?? SongChoices.FirstOrDefault(song =>
                string.Equals(song.Title, query, StringComparison.OrdinalIgnoreCase));
        var choice = exact
            ?? SongChoices.FirstOrDefault(song =>
                !song.IsBuiltIn &&
                song.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            ?? SongChoices.FirstOrDefault(song =>
                song.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            SongSearchText = SelectedSongTitle;
            StatusText = $"No song matched '{query}'.";
            await BlurAsync("song-search");
            return;
        }

        await SelectSongByIdAsync(choice.Id);
        SongSearchText = SelectedSongTitle;
        await BlurAsync("song-search");
    }

    protected async Task SelectSongByIdAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            SongSearchText = SelectedSongTitle;
            return;
        }

        var choice = SongChoices.FirstOrDefault(song =>
            string.Equals(song.Id, id, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            SongSearchText = SelectedSongTitle;
            return;
        }

        // A built-in chart and a browser-local chart can intentionally share
        // the same song ID. Treat them as different selectable sources rather
        // than skipping the requested local chart as already selected.
        var requestedIsLocal = _localSongIndex.ContainsKey(id);
        var selectedIdMatches = string.Equals(
            id,
            SelectedSongId,
            StringComparison.OrdinalIgnoreCase);
        var loadedSourceMatches = requestedIsLocal
            ? string.Equals(_loadedLocalSongId, id, StringComparison.OrdinalIgnoreCase)
            : string.IsNullOrWhiteSpace(_loadedLocalSongId);
        if (selectedIdMatches && loadedSourceMatches)
        {
            SongSearchText = SelectedSongTitle;
            return;
        }

        StatusText = "Loading song";
        await InvokeAsync(StateHasChanged);
        await Task.Yield();

        try
        {
            if (_localSongIndex.TryGetValue(id, out var local))
            {
                var browser = await EnsureBrowserModuleAsync();
                var source = await ReadLocalSongSourceAsync(browser, id);
                if (string.IsNullOrWhiteSpace(source))
                {
                    throw new InvalidOperationException("The saved chart data is missing.");
                }
                LoadSourceDocument(source, local.Title, id, saveAsBaseline: true, localSongId: id);
            }
            else if (_builtInSongs.TryGetValue(id, out var builtIn))
            {
                LoadSourceDocument(
                    LazyBuiltInSongCatalog.ReadSource(builtIn),
                    Path.GetFileNameWithoutExtension(builtIn.FileName),
                    id,
                    saveAsBaseline: true,
                    localSongId: null);
            }
            else
            {
                throw new InvalidOperationException("The selected song is no longer available.");
            }
            StatusText = "Song loaded";
        }
        catch (Exception exception)
        {
            StatusText = $"Song could not be loaded: {exception.Message}";
        }

        SongSearchText = SelectedSongTitle;
    }

    protected async Task ChangeGlobalStyle(ChangeEventArgs args)
    {
        var previousValue = SelectedStyleValue;
        SelectedStyleValue = args.Value?.ToString() ?? AccompanimentStyleNames.StorageName(Document.Style);
        if (!ApplyDocument("Style changed"))
        {
            SelectedStyleValue = previousValue;
            _ = ApplyDocument("Invalid style was reverted");
            return;
        }

        if (!IsPlaying)
        {
            _activePlaybackStyle = ResolvedPlaybackStyle;
            return;
        }

        if (HeadOutQueued || _sessionPlan is null || _audioModule is null)
        {
            SelectedStyleValue = previousValue;
            _ = ApplyDocument("The accompaniment style cannot be changed during the ending.");
            StatusText = "The accompaniment style cannot be changed during the ending.";
            return;
        }

        var requestedDefaultStyle = ResolvedPlaybackStyle;
        var styleGenerationVersion = ++_styleGenerationVersion;
        var sessionGenerationVersion = _sessionGenerationVersion;
        const double schedulingGuardSeconds = 0.20d;

        try
        {
            // Match the desktop application: keep the sounding four-bar block and
            // prepare the block beginning at the next four-bar boundary. A section
            // override at that destination remains authoritative over the song default.
            _positionSeconds = await _audioModule.InvokeAsync<double>("getPosition");
            var blockDuration = _sessionPlan.BarDurationSeconds * SessionConstants.BarsPerSegment;
            var boundary = NextFourBarBoundary(_sessionPlan, _positionSeconds, blockDuration);
            if (boundary - _positionSeconds <= schedulingGuardSeconds)
            {
                boundary += blockDuration;
            }

            var queuedStyle = ResolveStyleAtPlaybackPosition(boundary);
            StatusText = $"Preparing {AccompanimentStyleNames.DisplayName(requestedDefaultStyle)}; rehearsal-mark overrides remain unchanged.";
            await InvokeAsync(StateHasChanged);

            var browser = await EnsureBrowserModuleAsync();
            async ValueTask YieldToBrowserAsync()
            {
                await browser.InvokeVoidAsync("yieldToBrowser", 1);
                if (styleGenerationVersion != _styleGenerationVersion ||
                    sessionGenerationVersion != _sessionGenerationVersion ||
                    !IsPlaying || HeadOutQueued)
                {
                    throw new OperationCanceledException();
                }
            }

            var replacement = await WebSessionPlanner.BuildSessionIncrementallyAsync(
                _activeTune,
                Document.TempoBpm,
                _sessionSeed,
                YieldToBrowserAsync,
                HeadOutChorus);

            if (styleGenerationVersion != _styleGenerationVersion ||
                sessionGenerationVersion != _sessionGenerationVersion ||
                !IsPlaying || HeadOutQueued || _sessionPlan is null || _audioModule is null)
            {
                return;
            }

            // Normally generation completes well before the requested boundary.
            // If the request arrived inside the scheduler's protected window, move
            // only to the following four-bar boundary, never to an eight-bar grid.
            _positionSeconds = await _audioModule.InvokeAsync<double>("getPosition");
            if (boundary - _positionSeconds <= schedulingGuardSeconds)
            {
                boundary = NextFourBarBoundary(_sessionPlan, _positionSeconds, blockDuration);
                if (boundary - _positionSeconds <= schedulingGuardSeconds)
                {
                    boundary += blockDuration;
                }
                queuedStyle = ResolveStyleAtPlaybackPosition(boundary);
            }

            var continuation = replacement.Notes
                .Where(note => note.StartSeconds >= boundary - 0.001d)
                .ToArray();
            await _audioModule.InvokeVoidAsync(
                "replaceContinuation",
                continuation,
                replacement.DurationSeconds,
                boundary);

            _sessionPlan = replacement;
            // Keep the currently sounding block's status unchanged until the
            // boundary even when the destination override resolves to the same style.
            _pendingPlaybackStyle = queuedStyle;
            _pendingStyleBoundarySeconds = boundary;
            StatusText = $"{AccompanimentStyleNames.DisplayName(requestedDefaultStyle)} set as the song default; rehearsal-mark overrides remain unchanged.";
        }
        catch (OperationCanceledException)
        {
            // A newer style request, Stop, tempo change, or ending request superseded this build.
        }
        catch (Exception exception)
        {
            if (styleGenerationVersion != _styleGenerationVersion)
            {
                return;
            }

            SelectedStyleValue = previousValue;
            _ = ApplyDocument("Style change failed.");
            StatusText = $"Style could not be changed: {exception.Message}";
        }
    }

    protected async Task ChangeTempoAsync(ChangeEventArgs args)
    {
        if (!int.TryParse(args.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested))
        {
            return;
        }

        var newTempo = Math.Clamp(requested, 40, 300);
        var oldTempo = Math.Clamp(Document.TempoBpm, 40, 300);
        if (newTempo == oldTempo)
        {
            return;
        }

        _styleGenerationVersion++;
        Document.TempoBpm = newTempo;
        if (!ApplyDocument($"Tempo changed to {newTempo} BPM."))
        {
            Document.TempoBpm = oldTempo;
            _ = ApplyDocument("Invalid tempo was reverted");
            return;
        }

        if (!IsPlaying || _sessionPlan is null || _audioModule is null)
        {
            return;
        }

        var oldPlan = _sessionPlan;
        var generationVersion = ++_sessionGenerationVersion;
        var styleGenerationVersion = _styleGenerationVersion;
        StatusText = $"Preparing {newTempo} BPM";
        await InvokeAsync(StateHasChanged);

        try
        {
            var browser = await EnsureBrowserModuleAsync();
            async ValueTask YieldToBrowserAsync()
            {
                await browser.InvokeVoidAsync("yieldToBrowser", 1);
                if (generationVersion != _sessionGenerationVersion ||
                    styleGenerationVersion != _styleGenerationVersion ||
                    !IsPlaying)
                {
                    throw new OperationCanceledException();
                }
            }

            // Building the complete open-ended session synchronously blocks the
            // single WebAssembly UI thread. Generate it in four-bar pieces while
            // the existing plan continues to play.
            var newPlan = await WebSessionPlanner.BuildSessionIncrementallyAsync(
                _activeTune,
                newTempo,
                _sessionSeed,
                YieldToBrowserAsync,
                HeadOutChorus);

            if (generationVersion != _sessionGenerationVersion ||
                styleGenerationVersion != _styleGenerationVersion ||
                !IsPlaying ||
                _sessionPlan is null ||
                _audioModule is null)
            {
                return;
            }

            // Read the live audio position after generation. The progress timer
            // continues to run while the incremental builder yields, so the
            // position captured before generation would already be stale.
            var currentPosition = await _audioModule.InvokeAsync<double>("getPosition");
            if (generationVersion != _sessionGenerationVersion ||
                styleGenerationVersion != _styleGenerationVersion ||
                !IsPlaying)
            {
                return;
            }

            var newPosition = currentPosition < oldPlan.CountInSeconds
                ? newPlan.CountInSeconds *
                  (currentPosition / Math.Max(0.001d, oldPlan.CountInSeconds))
                : newPlan.CountInSeconds +
                  Math.Max(0d, currentPosition - oldPlan.CountInSeconds) *
                  oldTempo / (double)newTempo;

            await _audioModule.InvokeVoidAsync(
                "replaceSession",
                newPlan.Notes,
                newPlan.DurationSeconds,
                newPosition,
                true);

            if (generationVersion != _sessionGenerationVersion || !IsPlaying)
            {
                return;
            }

            _sessionPlan = newPlan;
            _positionSeconds = newPosition;
            _launchPlanDurationSeconds = 0d;
            StatusText = $"Tempo changed to {newTempo} BPM.";
        }
        catch (OperationCanceledException)
        {
            // Stop, another tempo request, or a style change superseded this build.
        }
        catch (Exception exception)
        {
            if (generationVersion != _sessionGenerationVersion ||
                styleGenerationVersion != _styleGenerationVersion)
            {
                return;
            }

            Document.TempoBpm = oldTempo;
            _ = ApplyDocument("Tempo change failed.");
            StatusText = $"Tempo could not be changed: {exception.Message}";
        }
    }

    protected void ChangeKey(ChangeEventArgs args)
    {
        var newKey = args.Value?.ToString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newKey) || string.Equals(newKey, Document.Key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var oldPitch = ChordSymbolTransposer.PitchClass(Document.Key);
        var newPitch = ChordSymbolTransposer.PitchClass(newKey);
        if (oldPitch >= 0 && newPitch >= 0)
        {
            var shift = (newPitch - oldPitch + 12) % 12;
            TransposeBars(Document.Bars, shift);
            TransposeBars(Document.EndingBars, shift);
        }
        Document.Key = newKey;
        SelectedKey = newKey;
        ApplyDocument($"Transposed to {newKey}");
    }

    protected void ChangeAccidentals(ChangeEventArgs args)
    {
        if (!Enum.TryParse<AccidentalPreference>(args.Value?.ToString(), out var preference))
        {
            preference = AccidentalPreference.Auto;
        }
        AccidentalPreference = preference;
        RespellingBars(Document.Bars);
        RespellingBars(Document.EndingBars);
        if (!string.IsNullOrWhiteSpace(Document.Key))
        {
            Document.Key = ChordSymbolTransposer.TransposeKey(Document.Key, 0, preference);
            SelectedKey = Document.Key;
        }
        ApplyDocument("Accidental spelling changed");
    }

    protected void BeginChordEdit(int barIndex, int beatIndex)
    {
        if (IsPlaying)
        {
            return;
        }
        EditingSectionBar = -1;
        SectionStyleMenuBar = -1;
        EditingChordBar = barIndex;
        EditingChordBeat = beatIndex;
        var value = Document.Bars[barIndex].BeatCells[beatIndex];
        ChordEditText = value is "." or "/" ? string.Empty : value;
        _ = FocusAsync($"chord-editor-{barIndex}-{beatIndex}");
    }

    protected void UpdateChordEditText(ChangeEventArgs args) => ChordEditText = args.Value?.ToString() ?? string.Empty;

    protected void HandleChordKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Enter" || args.Key == "Tab")
        {
            CommitChordEdit();
        }
        else if (args.Key == "Escape")
        {
            CancelChordEdit();
        }
    }

    protected void CommitChordEdit()
    {
        if (EditingChordBar < 0 || EditingChordBeat < 0)
        {
            return;
        }
        var barIndex = EditingChordBar;
        var beatIndex = EditingChordBeat;
        var bar = Document.Bars[barIndex];
        var prior = bar.BeatCells[beatIndex];
        var value = ChordEditText.Trim();
        if (beatIndex == 0 && string.IsNullOrWhiteSpace(value))
        {
            ChartStatusText = "Beat 1 must contain a chord. Use N.C. for no chord.";
            HasValidationError = true;
            return;
        }
        bar.BeatCells[beatIndex] = string.IsNullOrWhiteSpace(value) ? "." : value;
        EditingChordBar = -1;
        EditingChordBeat = -1;
        if (!ApplyDocument("Chord updated"))
        {
            bar.BeatCells[beatIndex] = prior;
            ApplyDocument("Invalid chord was reverted");
        }
    }

    protected void CancelChordEdit()
    {
        EditingChordBar = -1;
        EditingChordBeat = -1;
        ChordEditText = string.Empty;
    }

    protected void BeginSectionEdit(int barIndex)
    {
        if (IsPlaying)
        {
            return;
        }
        EditingChordBar = -1;
        SectionStyleMenuBar = -1;
        EditingSectionBar = barIndex;
        SectionEditText = Document.Bars[barIndex].RehearsalMark;
        _ = FocusAsync($"section-editor-{barIndex}");
    }

    protected void UpdateSectionEditText(ChangeEventArgs args) => SectionEditText = args.Value?.ToString() ?? string.Empty;

    protected void HandleSectionKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Enter" || args.Key == "Tab")
        {
            CommitSectionEdit();
        }
        else if (args.Key == "Escape")
        {
            CancelSectionEdit();
        }
    }

    protected void CommitSectionEdit()
    {
        if (EditingSectionBar < 0)
        {
            return;
        }
        var barIndex = EditingSectionBar;
        var oldMark = Document.Bars[barIndex].RehearsalMark;
        var newMark = SectionEditText.Trim().Replace("|", string.Empty, StringComparison.Ordinal);
        Document.Bars[barIndex].RehearsalMark = newMark;
        if (!string.IsNullOrWhiteSpace(oldMark) && !string.Equals(oldMark, newMark, StringComparison.OrdinalIgnoreCase) &&
            Document.SectionStyles.Remove(oldMark, out var sectionStyle) && !string.IsNullOrWhiteSpace(newMark))
        {
            Document.SectionStyles[newMark] = sectionStyle;
        }
        EditingSectionBar = -1;
        ApplyDocument(string.IsNullOrWhiteSpace(newMark) ? "Rehearsal mark removed" : "Rehearsal mark updated");
    }

    protected void CancelSectionEdit()
    {
        EditingSectionBar = -1;
        SectionEditText = string.Empty;
    }

    protected string SectionStyleValue(string mark) =>
        Document.SectionStyles.TryGetValue(mark, out var style) ? AccompanimentStyleNames.StorageName(style) : "default";

    protected string SectionStyleDisplay(string mark) =>
        Document.SectionStyles.TryGetValue(mark, out var style)
            ? style switch
            {
                AccompanimentStyle.JazzBallad => "Ballad",
                AccompanimentStyle.BossaNova => "Bossa",
                AccompanimentStyle.AfroCubanLatin => "Latin",
                AccompanimentStyle.JazzWaltz => "Waltz",
                AccompanimentStyle.Swing => "Swing",
                _ => "Default"
            }
            : "Default";

    protected void ChangeSectionStyle(string mark, ChangeEventArgs args)
    {
        var value = args.Value?.ToString() ?? "default";
        if (value == "default")
        {
            Document.SectionStyles.Remove(mark);
        }
        else if (AccompanimentStyleNames.TryParseExplicit(value, out var style))
        {
            Document.SectionStyles[mark] = style;
        }
        ApplyDocument($"Style at {mark} updated");
    }

    protected async Task BeginTitleEditAsync()
    {
        if (IsPlaying || IsImporting)
        {
            return;
        }

        CloseTitleMenu();
        EditingChordBar = EditingChordBeat = EditingSectionBar = -1;
        SectionStyleMenuBar = -1;
        EditingTitle = true;
        TitleEditText = Document.Title;
        await InvokeAsync(StateHasChanged);
        await FocusAsync("title-editor");
    }

    protected void UpdateTitleEditText(ChangeEventArgs args) =>
        TitleEditText = args.Value?.ToString() ?? string.Empty;

    protected async Task HandleTitleKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key is "Enter" or "Tab")
        {
            await CommitTitleEditAsync();
        }
        else if (args.Key == "Escape")
        {
            CancelTitleEdit();
        }
    }

    protected async Task CommitTitleEditAsync()
    {
        if (!EditingTitle)
        {
            return;
        }

        string title;
        try
        {
            title = NewSongTemplate.NormalizeTitle(TitleEditText);
        }
        catch (ArgumentException exception)
        {
            HasValidationError = true;
            StatusText = exception.Message;
            ChartStatusText = exception.Message;
            await FocusAsync("title-editor");
            return;
        }

        if (string.Equals(Document.Title, title, StringComparison.Ordinal))
        {
            CancelTitleEdit();
            return;
        }

        var previousTitle = Document.Title;
        Document.Title = title;
        SongSearchText = title;
        if (!ApplyDocument("Song title validated"))
        {
            Document.Title = previousTitle;
            SongSearchText = previousTitle;
            await FocusAsync("title-editor");
            return;
        }

        await SaveCurrentDocumentAsync("Song title saved in this browser");
        if (HasValidationError)
        {
            Document.Title = previousTitle;
            SongSearchText = previousTitle;
            _ = ApplyDocument("Song title save failed");
            TitleEditText = title;
            await FocusAsync("title-editor");
            return;
        }

        EditingTitle = false;
        TitleEditText = string.Empty;
    }

    protected void CancelTitleEdit()
    {
        EditingTitle = false;
        TitleEditText = string.Empty;
    }

    protected void OpenTitleMenu()
    {
        if (EditingTitle)
        {
            return;
        }
        TitleMenuOpen = true;
    }

    protected void CloseTitleMenu() => TitleMenuOpen = false;

    protected async Task DeleteCurrentSongAsync()
    {
        CloseTitleMenu();
        if (!CanDeleteCurrentSong || string.IsNullOrWhiteSpace(_loadedLocalSongId))
        {
            return;
        }

        var id = _loadedLocalSongId;
        var title = _localSongIndex.TryGetValue(id, out var metadata)
            ? metadata.Title
            : Document.Title;
        var browser = await EnsureBrowserModuleAsync();
        var confirmed = await browser.InvokeAsync<bool>(
            "confirmAction",
            $"Delete {title}? This cannot be undone.");
        if (!confirmed)
        {
            return;
        }

        var storageKey = LocalSongStorageKey(id);
        var previousSource = await browser.InvokeAsync<string?>("storageGet", storageKey);
        var previousIndexJson = await browser.InvokeAsync<string?>("storageGet", LocalSongIndexKey);
        var stagedIndex = _localSongIndex.Values
            .Where(song => !string.Equals(song.Id, id, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(song => song.Id, song => song, StringComparer.OrdinalIgnoreCase);
        var stagedIndexJson = JsonSerializer.Serialize(
            stagedIndex.Values.OrderBy(song => song.Title, StringComparer.OrdinalIgnoreCase));

        IsImporting = true;
        StatusText = $"Deleting {title}";
        await InvokeAsync(StateHasChanged);
        try
        {
            await browser.InvokeVoidAsync("storageRemove", storageKey);
            await browser.InvokeVoidAsync("storageSet", LocalSongIndexKey, stagedIndexJson);
        }
        catch (Exception exception)
        {
            try
            {
                if (previousSource is not null)
                {
                    await browser.InvokeVoidAsync("storageSet", storageKey, previousSource);
                }
                if (previousIndexJson is null)
                {
                    await browser.InvokeVoidAsync("storageRemove", LocalSongIndexKey);
                }
                else
                {
                    await browser.InvokeVoidAsync("storageSet", LocalSongIndexKey, previousIndexJson);
                }
            }
            catch { }

            HasValidationError = true;
            StatusText = $"Song could not be deleted: {exception.Message}";
            ChartStatusText = StatusText;
            return;
        }
        finally
        {
            IsImporting = false;
        }

        _localSongIndex.Clear();
        foreach (var entry in stagedIndex.Values)
        {
            _localSongIndex[entry.Id] = entry;
        }
        _loadedLocalSongId = null;
        RefreshSongChoices();
        LoadBuiltInFallback(id);
        HasValidationError = false;
        StatusText = $"Deleted {title}";
        ChartStatusText = StatusText;
        await InvokeAsync(StateHasChanged);
    }

    protected IReadOnlyList<WebChordSegment> ChordSegments(WebEditableBar bar)
    {
        var starts = new List<(int Beat, string Symbol)>();
        for (var beat = 0; beat < Math.Min(Document.BeatsPerBar, bar.BeatCells.Count); beat++)
        {
            var symbol = bar.BeatCells[beat]?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(symbol) && symbol is not "." and not "/")
            {
                starts.Add((beat, symbol));
            }
        }

        if (starts.Count == 0)
        {
            starts.Add((0, "N.C."));
        }

        var segments = new List<WebChordSegment>(starts.Count);
        for (var index = 0; index < starts.Count; index++)
        {
            var endBeat = index + 1 < starts.Count ? starts[index + 1].Beat : Document.BeatsPerBar;
            segments.Add(new WebChordSegment(starts[index].Beat, Math.Max(1, endBeat - starts[index].Beat), starts[index].Symbol));
        }
        return segments;
    }

    protected bool IsCurrentChordSegment(int barIndex, WebChordSegment segment) =>
        barIndex == CurrentBarIndex &&
        CurrentBeatIndex >= segment.StartBeat &&
        CurrentBeatIndex < segment.StartBeat + segment.BeatSpan;

    protected void OpenSectionStyleMenu(int barIndex)
    {
        if (IsPlaying || barIndex < 0 || barIndex >= Document.Bars.Count ||
            string.IsNullOrWhiteSpace(Document.Bars[barIndex].RehearsalMark))
        {
            return;
        }
        SectionStyleMenuBar = SectionStyleMenuBar == barIndex ? -1 : barIndex;
    }

    protected void CloseSectionStyleMenu() => SectionStyleMenuBar = -1;

    protected void UseDefaultSectionStyle(int barIndex) => SetSectionStyle(barIndex, "default");

    protected void SetSectionStyle(int barIndex, string value)
    {
        if (barIndex < 0 || barIndex >= Document.Bars.Count)
        {
            return;
        }
        var mark = Document.Bars[barIndex].RehearsalMark.Trim();
        if (string.IsNullOrWhiteSpace(mark))
        {
            SectionStyleMenuBar = -1;
            return;
        }
        if (string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
        {
            Document.SectionStyles.Remove(mark);
        }
        else if (AccompanimentStyleNames.TryParseExplicit(value, out var style))
        {
            Document.SectionStyles[mark] = style;
        }
        SectionStyleMenuBar = -1;
        ApplyDocument($"Style at {mark} updated");
    }

    protected void AddBar()
    {
        var beats = Document.BeatsPerBar;
        var openingChord = Document.Bars.LastOrDefault()?.BeatCells.FirstOrDefault(cell => cell is not "." and not "/") ?? "Cmaj7";
        Document.Bars.Add(new WebEditableBar
        {
            Index = Document.Bars.Count,
            BeatCells = [openingChord, .. Enumerable.Repeat(".", beats - 1)]
        });
        ApplyDocument("Bar added");
    }

    protected void RemoveLastBar()
    {
        if (Document.Bars.Count <= NewSongTemplate.MinimumBarCount)
        {
            return;
        }
        Document.Bars.RemoveAt(Document.Bars.Count - 1);
        ApplyDocument("Last bar removed");
    }

    protected async Task BeginNewSongCreationAsync()
    {
        if (IsPlaying || IsImporting)
        {
            return;
        }

        CloseTitleMenu();
        NewSongEditorOpen = true;
        NewSongTitleText = string.Empty;
        NewSongBarCountText = "32";
        NewSongValidationText = string.Empty;
        await InvokeAsync(StateHasChanged);
        await FocusAsync("new-song-title");
    }

    protected void UpdateNewSongTitle(ChangeEventArgs args) =>
        NewSongTitleText = args.Value?.ToString() ?? string.Empty;

    protected void UpdateNewSongBarCount(ChangeEventArgs args) =>
        NewSongBarCountText = args.Value?.ToString() ?? string.Empty;

    protected async Task HandleNewSongKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await CreateNewSongAsync();
        }
        else if (args.Key == "Escape")
        {
            CancelNewSongCreation();
        }
    }

    protected void CancelNewSongCreation()
    {
        NewSongEditorOpen = false;
        NewSongTitleText = string.Empty;
        NewSongValidationText = string.Empty;
    }

    protected async Task CreateNewSongAsync()
    {
        string requestedTitle;
        try
        {
            requestedTitle = NewSongTemplate.NormalizeTitle(NewSongTitleText);
        }
        catch (ArgumentException exception)
        {
            NewSongValidationText = exception.Message;
            await FocusAsync("new-song-title");
            return;
        }

        if (!int.TryParse(
                NewSongBarCountText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var barCount))
        {
            NewSongValidationText =
                $"Enter a whole number from {NewSongTemplate.MinimumBarCount} to {NewSongTemplate.MaximumBarCount}.";
            await FocusAsync("new-song-bars");
            return;
        }

        try
        {
            NewSongTemplate.ValidateBarCount(barCount);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            NewSongValidationText = exception.Message;
            await FocusAsync("new-song-bars");
            return;
        }

        var title = CreateUniqueNewSongTitle(requestedTitle);
        var reservedIds = new HashSet<string>(
            _localSongIndex.Keys.Concat(_builtInSongs.Keys),
            StringComparer.OrdinalIgnoreCase);
        var id = CreateUniqueImportedSongId(WebSongDocument.CreateId(title), reservedIds);
        var source = NewSongTemplate.CreateChordPro(barCount, title, id);
        var document = WebSongDocument.Parse(source, title);
        LoadDocument(document, id, saveAsBaseline: false);
        StatusText = $"Saving {title}";
        ChartStatusText = "Saving new chart…";
        await SaveCurrentDocumentAsync($"Created and saved {title} ({barCount} bars)");
        if (HasValidationError)
        {
            return;
        }

        NewSongEditorOpen = false;
        NewSongTitleText = string.Empty;
        NewSongValidationText = string.Empty;
        SettingsOpen = false;
        ChartStatusText = $"Created {title}. Double-click the title to rename it.";
    }

    private string CreateUniqueNewSongTitle(string requestedTitle)
    {
        var baseTitle = NewSongTemplate.NormalizeTitle(requestedTitle);
        var existingTitles = new HashSet<string>(
            SongChoices.Select(song => song.Title),
            StringComparer.OrdinalIgnoreCase);
        if (!existingTitles.Contains(baseTitle))
        {
            return baseTitle;
        }

        for (var suffix = 2; ; suffix++)
        {
            var suffixText = $" ({suffix})";
            var stemLength = Math.Max(1, NewSongTemplate.MaximumTitleLength - suffixText.Length);
            var stem = baseTitle.Length <= stemLength
                ? baseTitle
                : baseTitle[..stemLength].TrimEnd();
            var candidate = stem + suffixText;
            if (!existingTitles.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    protected async Task RefreshBrowserSongLibraryAsync()
    {
        if (IsPlaying || IsImporting)
        {
            return;
        }

        try
        {
            var browser = await EnsureBrowserModuleAsync();
            await ReloadLocalSongIndexAsync(browser);
            if (!string.IsNullOrWhiteSpace(_loadedLocalSongId) &&
                !_localSongIndex.ContainsKey(_loadedLocalSongId))
            {
                LoadBuiltInFallback(_loadedLocalSongId);
            }
            RefreshSongChoices();
            SongSearchText = SelectedSongTitle;
            StatusText = $"Song library refreshed. {SongChoices.Count} songs.";
        }
        catch (Exception exception)
        {
            StatusText = $"Song library could not be refreshed: {exception.Message}";
        }
    }

    protected async Task ImportSongAsync(InputFileChangeEventArgs args)
    {
        try
        {
            var file = args.File;
            await using var stream = file.OpenReadStream(1_000_000);
            using var reader = new StreamReader(stream);
            var source = await reader.ReadToEndAsync();
            var document = WebSongDocument.Parse(source, Path.GetFileNameWithoutExtension(file.Name));
            LoadDocument(document, document.Id, saveAsBaseline: false);
            StatusText = $"Imported {file.Name}";
            ChartStatusText = "Imported chart is not yet stored in this browser. Click Save.";
        }
        catch (Exception exception)
        {
            StatusText = $"Import failed: {exception.Message}";
            HasValidationError = true;
        }
    }

    protected async Task ImportIRealProAsync(InputFileChangeEventArgs args)
    {
        if (IsPlaying)
        {
            StatusText = "Stop the session before importing songs.";
            return;
        }

        IsImporting = true;
        HasValidationError = false;
        StatusText = "Reading iReal Pro file";
        ChartStatusText = "Reading iReal Pro file…";
        await InvokeAsync(StateHasChanged);

        IJSObjectReference? browser = null;
        try
        {
            browser = await EnsureBrowserModuleAsync();
            await browser.InvokeVoidAsync("yieldToBrowser", 2);

            var file = args.File;
            await using var stream = file.OpenReadStream(20_000_000);
            using var reader = new StreamReader(stream);
            var source = await reader.ReadToEndAsync();

            StatusText = "Converting iReal Pro songs";
            ChartStatusText = "Converting iReal Pro songs…";
            await InvokeAsync(StateHasChanged);
            await browser.InvokeVoidAsync("yieldToBrowser", 2);

            // Convert one iReal song at a time and yield between songs. The Core
            // parser and validator remain authoritative, while a large exported
            // library no longer monopolizes the single WebAssembly UI thread.
            var import = await ParseIRealProIncrementallyAsync(source, browser);
            var reservedIds = new HashSet<string>(
                _localSongIndex.Keys.Concat(_builtInSongs.Keys),
                StringComparer.OrdinalIgnoreCase);
            var importedSongs = new List<(string Id, string Title, string Source)>();

            foreach (var song in import.Songs)
            {
                var requestedId = ReadChordProDirectiveValue(song.ChordProText, "x-ai-jam-id")
                    ?? WebSongDocument.CreateId(song.Title);
                var id = CreateUniqueImportedSongId(requestedId, reservedIds);
                var storedSource = SetChordProSongId(song.ChordProText, id);
                importedSongs.Add((id, song.Title, storedSource));
            }
            if (importedSongs.Count == 0)
            {
                throw new IRealProImportException("No supported songs were converted.");
            }

            StatusText = importedSongs.Count == 1
                ? "Saving imported song"
                : $"Saving {importedSongs.Count} imported songs";
            ChartStatusText = StatusText + "…";
            await InvokeAsync(StateHasChanged);
            await browser.InvokeVoidAsync("yieldToBrowser", 1);

            var previousIndexJson = await browser.InvokeAsync<string?>("storageGet", LocalSongIndexKey);
            var stagedIndex = _localSongIndex.Values
                .ToDictionary(song => song.Id, song => song, StringComparer.OrdinalIgnoreCase);
            foreach (var imported in importedSongs)
            {
                stagedIndex[imported.Id] = new StoredWebSongMetadata(imported.Id, imported.Title);
            }
            var stagedIndexJson = JsonSerializer.Serialize(
                stagedIndex.Values.OrderBy(song => song.Title, StringComparer.OrdinalIgnoreCase));
            var writtenKeys = new List<string>(importedSongs.Count);
            try
            {
                for (var index = 0; index < importedSongs.Count; index++)
                {
                    var imported = importedSongs[index];
                    var key = LocalSongStorageKey(imported.Id);
                    await browser.InvokeVoidAsync("storageSet", key, imported.Source);
                    writtenKeys.Add(key);
                    if ((index + 1) % 4 == 0)
                    {
                        await browser.InvokeVoidAsync("yieldToBrowser", 1);
                    }
                }
                await browser.InvokeVoidAsync("storageSet", LocalSongIndexKey, stagedIndexJson);
            }
            catch
            {
                foreach (var key in writtenKeys)
                {
                    try { await browser.InvokeVoidAsync("storageRemove", key); } catch { }
                }
                try
                {
                    if (previousIndexJson is null)
                    {
                        await browser.InvokeVoidAsync("storageRemove", LocalSongIndexKey);
                    }
                    else
                    {
                        await browser.InvokeVoidAsync("storageSet", LocalSongIndexKey, previousIndexJson);
                    }
                }
                catch { }
                throw;
            }

            _localSongIndex.Clear();
            foreach (var entry in stagedIndex.Values)
            {
                _localSongIndex[entry.Id] = entry;
            }
            RefreshSongChoices();

            // Load only the first imported song, once. The converter has already
            // validated it, so this is the sole parse needed to display the chart.
            var first = importedSongs[0];
            LoadSourceDocument(first.Source, first.Title, first.Id, saveAsBaseline: true, localSongId: first.Id);

            var warnings = import.Warnings
                .Concat(import.Songs.SelectMany(song =>
                    song.Warnings.Select(warning => $"{song.Title}: {warning}")))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var description = importedSongs.Count == 1
                ? $"Imported {first.Title}."
                : $"Imported {importedSongs.Count} songs from iReal Pro.";
            if (warnings.Length > 0)
            {
                description += $" Note: {warnings[0]}";
                if (warnings.Length > 1)
                {
                    description += $" (+{warnings.Length - 1} more)";
                }
            }

            HasValidationError = false;
            StatusText = description;
            ChartStatusText = description;
        }
        catch (Exception exception)
        {
            HasValidationError = true;
            StatusText = $"Could not import the iReal Pro file: {exception.Message}";
            ChartStatusText = StatusText;
        }
        finally
        {
            IsImporting = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task SaveSongAsync()
    {
        if (!ApplyDocument("Song settings validated"))
        {
            return;
        }
        Document.Style = ResolvedPlaybackStyle;
        SelectedStyleValue = AccompanimentStyleNames.StorageName(Document.Style);
        await SaveCurrentDocumentAsync("Song saved in this browser");
    }

    protected async Task SaveChartAsync()
    {
        if (!ApplyDocument("Chart validated"))
        {
            return;
        }
        await SaveCurrentDocumentAsync("Chord sheet saved in this browser");
    }

    protected void RevertChart()
    {
        if (string.IsNullOrWhiteSpace(_savedSource))
        {
            return;
        }
        var document = WebSongDocument.Parse(_savedSource, Document.Title);
        LoadDocument(document, SelectedSongId, saveAsBaseline: true, localSongId: _loadedLocalSongId);
        ChartStatusText = "Reverted to the last saved chart.";
    }

    protected async Task ExportSongAsync()
    {
        if (!ApplyDocument("Chart validated for export"))
        {
            return;
        }
        var module = await EnsureBrowserModuleAsync();
        await module.InvokeVoidAsync("downloadText", $"{WebSongDocument.CreateId(Document.Title)}.cho", Document.ToChordPro());
        StatusText = "ChordPro file exported";
    }

    protected async Task DeleteAllLocalSongsAsync()
    {
        if (IsPlaying || IsImporting)
        {
            StatusText = "Stop the session before deleting songs.";
            return;
        }

        var browser = await EnsureBrowserModuleAsync();
        var confirmationText = _localSongIndex.Count > 0
            ? $"Delete all {_localSongIndex.Count} locally stored songs? Built-in songs will remain."
            : "Delete all locally stored song data? Built-in songs will remain.";
        var confirmed = await browser.InvokeAsync<bool>("confirmAction", confirmationText);
        if (!confirmed)
        {
            return;
        }

        // Preserve the explicit source identity even if the metadata index is
        // stale or missing, so an orphaned local chart is not left on screen.
        var currentLocalId = _loadedLocalSongId;
        IsImporting = true;
        StatusText = "Deleting local songs";
        await InvokeAsync(StateHasChanged);
        try
        {
            var deletedSourceCount = await browser.InvokeAsync<int>(
                "clearLocalSongStorage",
                LocalSongIndexKey,
                LegacyLocalSongsKey,
                LocalSongSourcePrefix);
            _localSongIndex.Clear();
            _loadedLocalSongId = null;
            RefreshSongChoices();

            if (!string.IsNullOrWhiteSpace(currentLocalId))
            {
                LoadBuiltInFallback(currentLocalId);
            }

            StatusText = deletedSourceCount > 0
                ? $"Deleted {deletedSourceCount} local songs"
                : "No local songs were stored";
            ChartStatusText = StatusText;
        }
        catch (Exception exception)
        {
            StatusText = $"Local songs could not be deleted: {exception.Message}";
            ChartStatusText = StatusText;
        }
        finally
        {
            IsImporting = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void LoadBuiltInFallback(string? preferredId)
    {
        var fallback = !string.IsNullOrWhiteSpace(preferredId) &&
            _builtInSongs.TryGetValue(preferredId, out var matchingBuiltIn)
                ? matchingBuiltIn
                : _builtInSongs.TryGetValue("autumn-leaves", out var autumnLeaves)
                    ? autumnLeaves
                    : _builtInSongs.Values.OrderBy(song => song.Title, StringComparer.OrdinalIgnoreCase).First();
        LoadSourceDocument(
            LazyBuiltInSongCatalog.ReadSource(fallback),
            Path.GetFileNameWithoutExtension(fallback.FileName),
            fallback.Id,
            saveAsBaseline: true,
            localSongId: null);
    }

    protected async Task PrimarySessionActionAsync()
    {
        if (!IsPlaying)
        {
            await StartSessionAsync();
        }
        else if (!HeadOutQueued)
        {
            await CueEndingAsync();
        }
    }

    protected async Task StartSessionAsync()
    {
        if (IsPlaying || IsLoading || !ApplyDocument("Ready"))
        {
            return;
        }
        IsLoading = true;
        StatusText = "Starting";
        var generationVersion = ++_sessionGenerationVersion;
        try
        {
            var module = await EnsureAudioModuleAsync();
            await module.InvokeVoidAsync("primeAudio");

            _sessionSeed = Random.Shared.Next();
            // Generate only the count-in and first two four-bar blocks before playback.
            // The rest of the selected song is expanded after audio has begun.
            _sessionPlan = WebSessionPlanner.BuildSession(
                _activeTune,
                Document.TempoBpm,
                _sessionSeed,
                endWithHeadOut: false,
                generatedSegments: Math.Min(2, _activeTune.SegmentCount));

            _launchPlanDurationSeconds = _sessionPlan.DurationSeconds;
            _positionSeconds = 0;
            HeadOutQueued = false;
            HeadOutChorus = null;
            ReferenceEnergyPercent = 0;
            CurrentEnergyPercent = 0;
            await module.InvokeVoidAsync("startSession", _sessionPlan.Notes, MixerState());
            IsPlaying = true;
            _activePlaybackStyle = ResolvedPlaybackStyle;
            _pendingPlaybackStyle = null;
            _pendingStyleBoundarySeconds = double.PositiveInfinity;
            StatusText = "Playing";
            BeginProgressUpdates();
            _ = ExpandSessionAfterStartAsync(SessionPlanSignature(), _sessionSeed, generationVersion);
        }
        catch (Exception exception)
        {
            StatusText = $"Audio could not start: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExpandSessionAfterStartAsync(string signature, int seed, int generationVersion)
    {
        try
        {
            // Let the start state render and the AudioWorklet begin first. Build the
            // longer plan in four-bar pieces so the browser scheduler continues to run.
            await Task.Delay(25);
            var launchDuration = _sessionPlan?.DurationSeconds ?? 0d;
            var browser = await EnsureBrowserModuleAsync();

            async ValueTask YieldToBrowserAsync()
            {
                await browser.InvokeVoidAsync("yieldToBrowser", 1);
                if (!IsPlaying || HeadOutQueued || generationVersion != _sessionGenerationVersion ||
                    !string.Equals(signature, SessionPlanSignature(), StringComparison.Ordinal))
                {
                    throw new OperationCanceledException();
                }
            }

            var expanded = await WebSessionPlanner.BuildSessionIncrementallyAsync(
                _activeTune,
                Document.TempoBpm,
                seed,
                YieldToBrowserAsync);
            if (!IsPlaying || HeadOutQueued || generationVersion != _sessionGenerationVersion ||
                !string.Equals(signature, SessionPlanSignature(), StringComparison.Ordinal) ||
                _audioModule is null)
            {
                return;
            }

            var continuation = expanded.Notes
                .Where(note => note.StartSeconds >= launchDuration - 0.001d)
                .ToArray();
            await _audioModule.InvokeVoidAsync("appendSession", continuation, expanded.DurationSeconds);
            _sessionPlan = expanded;
            _positionSeconds = await _audioModule.InvokeAsync<double>("getPosition");
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException)
        {
            // A playback operation superseded the expansion.
        }
        catch
        {
            // The launch plan remains playable even if background expansion fails.
        }
    }

    protected async Task CueEndingAsync()
    {
        if (!IsPlaying || _sessionPlan is null || _audioModule is null)
        {
            return;
        }
        _sessionGenerationVersion++;
        var headOutChorus = WebSessionPlanner.ResolveNextHeadOutChorus(_sessionPlan, _positionSeconds);
        var replacement = WebSessionPlanner.BuildSession(_activeTune, Document.TempoBpm, _sessionSeed, headOutChorus);
        if (_positionSeconds < _launchPlanDurationSeconds && _launchPlanDurationSeconds > 0)
        {
            var continuation = replacement.Notes
                .Where(note => note.StartSeconds >= _launchPlanDurationSeconds - 0.001d)
                .ToArray();
            await _audioModule.InvokeVoidAsync(
                "replaceContinuation",
                continuation,
                replacement.DurationSeconds,
                _launchPlanDurationSeconds);
        }
        else
        {
            await _audioModule.InvokeVoidAsync("replaceSession", replacement.Notes, replacement.DurationSeconds, _positionSeconds);
        }
        _sessionPlan = replacement;
        HeadOutQueued = true;
        HeadOutChorus = headOutChorus;
        StatusText = "Head out queued";
    }

    protected async Task StopSessionAsync()
    {
        _sessionGenerationVersion++;
        _progressCancellation?.Cancel();
        if (_audioModule is not null)
        {
            await _audioModule.InvokeVoidAsync("stopSession");
        }
        IsPlaying = false;
        HeadOutQueued = false;
        HeadOutChorus = null;
        _pendingPlaybackStyle = null;
        _pendingStyleBoundarySeconds = double.PositiveInfinity;
        StatusText = "Stopped";
        _positionSeconds = 0;
        _launchPlanDurationSeconds = 0;
    }

    protected async Task PanicAsync()
    {
        _sessionGenerationVersion++;
        _progressCancellation?.Cancel();
        if (_audioModule is not null)
        {
            await _audioModule.InvokeVoidAsync("panic");
        }
        IsPlaying = false;
        HeadOutQueued = false;
        HeadOutChorus = null;
        _pendingPlaybackStyle = null;
        _pendingStyleBoundarySeconds = double.PositiveInfinity;
        StatusText = "All notes off";
        _positionSeconds = 0;
        _launchPlanDurationSeconds = 0;
    }

    protected void ToggleSettings()
    {
        CloseTitleMenu();
        SettingsOpen = !SettingsOpen;
        if (!SettingsOpen)
        {
            CancelNewSongCreation();
        }
    }

    protected async Task RefreshMidiInputsAsync()
    {
        try
        {
            var module = await EnsureAudioModuleAsync();
            var inputs = await module.InvokeAsync<MidiInputChoice[]>("getMidiInputs");
            var outputs = await module.InvokeAsync<MidiOutputChoice[]>("getMidiOutputs");
            MidiInputs.Clear();
            MidiInputs.AddRange(inputs);
            MidiOutputs.Clear();
            MidiOutputs.AddRange(outputs);
            SelectedMidiOutputId = await module.InvokeAsync<string>("getSelectedMidiOutputId");
            MidiStatusText = $"{inputs.Length} MIDI input(s), {outputs.Length} external output(s) found.";
        }
        catch (Exception exception)
        {
            MidiStatusText = $"MIDI unavailable: {exception.Message}";
        }
    }

    protected async Task SelectMidiInputAsync(ChangeEventArgs args)
    {
        SelectedMidiInputId = args.Value?.ToString() ?? string.Empty;
        try
        {
            var module = await EnsureAudioModuleAsync();
            _dotNetReference ??= DotNetObjectReference.Create(this);
            await module.InvokeVoidAsync("selectMidiInput", SelectedMidiInputId, _dotNetReference);
            MidiStatusText = string.IsNullOrWhiteSpace(SelectedMidiInputId) ? "MIDI input closed." : "MIDI input open.";
        }
        catch (Exception exception)
        {
            MidiStatusText = $"MIDI input failed: {exception.Message}";
        }
    }

    protected async Task SelectMidiOutputAsync(ChangeEventArgs args)
    {
        var previousOutputId = SelectedMidiOutputId;
        var requestedOutputId = args.Value?.ToString() ?? string.Empty;
        try
        {
            var module = await EnsureAudioModuleAsync();
            await module.InvokeVoidAsync("selectMidiOutput", requestedOutputId);
            SelectedMidiOutputId = requestedOutputId;
            MidiStatusText = string.IsNullOrWhiteSpace(SelectedMidiOutputId)
                ? "Browser synth selected."
                : "External MIDI output selected.";
        }
        catch (Exception exception)
        {
            SelectedMidiOutputId = previousOutputId;
            MidiStatusText = $"MIDI output failed: {exception.Message}";
        }
    }

    [JSInvokable]
    public async Task HandleEscapeShortcutAsync()
    {
        if (TitleMenuOpen)
        {
            CloseTitleMenu();
            await InvokeAsync(StateHasChanged);
            return;
        }
        if (EditingTitle)
        {
            CancelTitleEdit();
            await InvokeAsync(StateHasChanged);
            return;
        }
        if (!SettingsOpen)
        {
            return;
        }

        SettingsOpen = false;
        CancelNewSongCreation();
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task HandleSpaceShortcutAsync()
    {
        if (IsImporting || SettingsOpen)
        {
            return;
        }

        await PrimarySessionActionAsync();
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task ReceiveMidiMessage(int status, int data1, int data2)
    {
        var command = status & 0xF0;
        if (command == 0x90 && data2 > 0)
        {
            var energy = data2 / 127d * 100d;
            _lastMidiAttack = DateTimeOffset.UtcNow;
            _lowEnergySince = null;
            CurrentEnergyPercent = CurrentEnergyPercent <= 0 ? energy : CurrentEnergyPercent * 0.72 + energy * 0.28;
            if (IsPlaying && _sessionPlan is not null && _positionSeconds >= _sessionPlan.CountInSeconds &&
                _positionSeconds < _sessionPlan.CountInSeconds + _sessionPlan.ChorusDurationSeconds)
            {
                ReferenceEnergyPercent = ReferenceEnergyPercent <= 0
                    ? CurrentEnergyPercent
                    : ReferenceEnergyPercent * 0.94 + CurrentEnergyPercent * 0.06;
            }
            _ = InvokeAsync(StateHasChanged);
        }
        return Task.CompletedTask;
    }

    protected async Task OnPianoEnabledChanged(bool value) { PianoEnabled = value; await PushMixerAsync(); }
    protected async Task OnBassEnabledChanged(bool value) { BassEnabled = value; await PushMixerAsync(); }
    protected async Task OnDrumsEnabledChanged(bool value) { DrumsEnabled = value; await PushMixerAsync(); }
    protected async Task OnMidiThruEnabledChanged(bool value)
    {
        MidiThruEnabled = value;
        if (value)
        {
            try
            {
                var module = await EnsureAudioModuleAsync();
                await module.InvokeVoidAsync("primeAudio");
            }
            catch (Exception exception)
            {
                MidiStatusText = $"Audio could not be prepared for MIDI thru: {exception.Message}";
            }
        }
        await PushMixerAsync();
    }
    protected async Task OnPianoVolumeChanged(int value) { PianoVolume = value; await PushMixerAsync(); }
    protected async Task OnBassVolumeChanged(int value) { BassVolume = value; await PushMixerAsync(); }
    protected async Task OnDrumsVolumeChanged(int value) { DrumsVolume = value; await PushMixerAsync(); }
    protected async Task OnVibraphoneVolumeChanged(int value) { VibraphoneVolume = value; await PushMixerAsync(); }

    private void LoadSourceDocument(
        string source,
        string sourceName,
        string id,
        bool saveAsBaseline,
        string? localSongId = null)
    {
        var tune = ChordProSongParser.Parse(source, sourceName);
        LoadDocument(WebSongDocument.FromTuneForm(tune, source), id, saveAsBaseline, tune, localSongId);
    }

    private void LoadDocument(
        WebSongDocument document,
        string id,
        bool saveAsBaseline,
        TuneForm? parsedTune = null,
        string? localSongId = null)
    {
        _sessionGenerationVersion++;
        _loadedLocalSongId = localSongId;
        Document = document;
        Document.Normalize();
        SelectedSongId = id;
        SongSearchText = document.Title;
        SelectedStyleValue = AccompanimentStyleNames.StorageName(Document.Style);
        SelectedKey = string.IsNullOrWhiteSpace(Document.Key) ? "C" : Document.Key;
        AccidentalPreference = AccidentalPreference.Auto;
        _activeTune = parsedTune ?? Document.ToTuneForm();
        _activePlaybackStyle = Document.Style;
        _pendingPlaybackStyle = null;
        _pendingStyleBoundarySeconds = double.PositiveInfinity;
        _positionSeconds = 0;
        _launchPlanDurationSeconds = 0;
        _sessionPlan = null;
        HasValidationError = false;
        EditingChordBar = EditingChordBeat = EditingSectionBar = -1;
        EditingTitle = false;
        TitleEditText = string.Empty;
        TitleMenuOpen = false;
        NewSongEditorOpen = false;
        NewSongTitleText = string.Empty;
        NewSongValidationText = string.Empty;
        _chartRows = BuildChartRows();
        _chartFitRequested = true;
        if (saveAsBaseline)
        {
            _savedSource = Document.ToChordPro();
            CaptureSaveBaselines();
        }
        else
        {
            _savedSource = string.Empty;
            _savedSongSettingsSignature = string.Empty;
            _savedChartSignature = string.Empty;
        }
    }

    private bool ApplyDocument(string successMessage)
    {
        try
        {
            Document.Normalize();
            var parsed = Document.ToTuneForm();
            _activeTune = ResolvedPlaybackStyle == parsed.AccompanimentStyle
                ? parsed
                : parsed.WithAccompanimentStyle(ResolvedPlaybackStyle, preserveSectionStyles: true);
            _chartRows = BuildChartRows();
            HasValidationError = false;
            ChartStatusText = successMessage;
            _chartFitRequested = true;
            return true;
        }
        catch (Exception exception)
        {
            HasValidationError = true;
            ChartStatusText = exception.Message;
            return false;
        }
    }

    private async Task<IRealProImportDocument> ParseIRealProIncrementallyAsync(
        string source,
        IJSObjectReference browser)
    {
        var songLinks = ExtractIndividualIRealSongLinks(source);
        if (songLinks.Count == 0)
        {
            // Preserve the Core parser's precise diagnostic for malformed files.
            return IRealProSongParser.Parse(source);
        }

        var songs = new List<IRealProImportedSong>(songLinks.Count);
        var warnings = new List<string>();
        for (var index = 0; index < songLinks.Count; index++)
        {
            StatusText = songLinks.Count == 1
                ? "Converting iReal Pro song"
                : $"Converting iReal Pro songs {index + 1} / {songLinks.Count}";
            ChartStatusText = StatusText + "…";
            await InvokeAsync(StateHasChanged);
            await browser.InvokeVoidAsync("yieldToBrowser", 1);

            try
            {
                var parsed = IRealProSongParser.Parse(songLinks[index]);
                songs.AddRange(parsed.Songs);
                warnings.AddRange(parsed.Warnings);
            }
            catch (IRealProImportException exception)
            {
                warnings.Add(exception.Message);
            }
        }

        if (songs.Count == 0)
        {
            var detail = warnings.Count == 0 ? string.Empty : $" {warnings[0]}";
            throw new IRealProImportException(
                $"No supported 3/4 or 4/4 songs could be imported.{detail}");
        }

        return new IRealProImportDocument(songs, warnings);
    }

    private static IReadOnlyList<string> ExtractIndividualIRealSongLinks(string source)
    {
        var decodedSource = WebUtility.HtmlDecode(source ?? string.Empty);
        var encodedUrls = Regex.Matches(
                decodedSource,
                @"irealb://[^\s""'<>]+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (encodedUrls.Count == 0)
        {
            var trimmed = decodedSource.Trim();
            if (trimmed.StartsWith("irealb://", StringComparison.OrdinalIgnoreCase))
            {
                encodedUrls.Add(trimmed);
            }
        }

        var result = new List<string>();
        foreach (var encodedUrl in encodedUrls)
        {
            string decodedUrl;
            try
            {
                decodedUrl = Uri.UnescapeDataString(encodedUrl);
            }
            catch (Exception exception) when (exception is UriFormatException or ArgumentException)
            {
                throw new IRealProImportException(
                    "The iReal Pro link contains invalid URL encoding.",
                    exception);
            }

            var separator = decodedUrl.IndexOf("://", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var payload = decodedUrl[(separator + 3)..];
            foreach (var songPayload in payload.Split(
                         "===",
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!songPayload.Contains('='))
                {
                    continue;
                }
                result.Add("irealb://" + Uri.EscapeDataString(songPayload));
            }
        }

        return result;
    }

    private string CreateUniqueImportedSongId(string requestedId, ISet<string> reservedIds)
    {
        var baseId = WebSongDocument.CreateId(requestedId);
        var candidate = baseId;
        var suffix = 2;
        while (reservedIds.Contains(candidate))
        {
            candidate = $"{baseId}-{suffix}";
            suffix++;
        }

        reservedIds.Add(candidate);
        return candidate;
    }

    private static string? ReadChordProDirectiveValue(string source, string directiveName)
    {
        using var reader = new StringReader(source);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Contains('|'))
            {
                break;
            }
            if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
            {
                continue;
            }

            var inner = trimmed[1..^1];
            var separator = inner.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }
            var name = inner[..separator].Trim();
            if (string.Equals(name, directiveName, StringComparison.OrdinalIgnoreCase))
            {
                var value = inner[(separator + 1)..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        return null;
    }

    private static string SetChordProSongId(string source, string id)
    {
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = source
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var hadTrailingNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (hadTrailingNewline && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var replacement = $"{{x-ai-jam-id: {id}}}";
        var idIndex = lines.FindIndex(line =>
            IsChordProDirective(line, "x-ai-jam-id"));
        if (idIndex >= 0)
        {
            lines[idIndex] = replacement;
        }
        else
        {
            var titleIndex = lines.FindIndex(line =>
                IsChordProDirective(line, "title") || IsChordProDirective(line, "t"));
            lines.Insert(titleIndex >= 0 ? titleIndex + 1 : 0, replacement);
        }

        var updated = string.Join(newline, lines);
        return hadTrailingNewline ? updated + newline : updated;
    }

    private static bool IsChordProDirective(string line, string name)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
        {
            return false;
        }

        var inner = trimmed[1..^1];
        var separator = inner.IndexOf(':');
        var directive = (separator >= 0 ? inner[..separator] : inner).Trim();
        return string.Equals(directive, name, StringComparison.OrdinalIgnoreCase);
    }

    private void CaptureSaveBaselines()
    {
        _savedSongSettingsSignature = CurrentSongSettingsSignature();
        _savedChartSignature = CurrentChartSignature();
    }

    private string CurrentSongSettingsSignature() => JsonSerializer.Serialize(new
    {
        Title = Document.Title.Trim(),
        Document.TempoBpm,
        Style = SelectedStyleValue,
        Key = Document.Key.Trim(),
        Accidentals = AccidentalPreference.ToString()
    });

    private string CurrentChartSignature()
    {
        var currentPitch = ChordSymbolTransposer.PitchClass(Document.Key);
        var originalPitch = ChordSymbolTransposer.PitchClass(Document.OriginalKey);
        var reverseShift = currentPitch >= 0 && originalPitch >= 0
            ? (originalPitch - currentPitch + 12) % 12
            : 0;

        static object SerializeBar(WebEditableBar bar, int semitoneShift) => new
        {
            Mark = bar.RehearsalMark.Trim(),
            Cells = bar.BeatCells.Select(cell =>
                ChordSymbolTransposer.TransposeChord(cell, semitoneShift, AccidentalPreference.Sharps)).ToArray()
        };

        return JsonSerializer.Serialize(new
        {
            Document.TimeSignature,
            Bars = Document.Bars.Select(bar => SerializeBar(bar, reverseShift)).ToArray(),
            EndingBars = Document.EndingBars.Select(bar => SerializeBar(bar, reverseShift)).ToArray(),
            Document.CodaStartIndex,
            SectionStyles = Document.SectionStyles
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new { Section = pair.Key.Trim(), Style = pair.Value.ToString() })
                .ToArray()
        });
    }

    private async Task ReloadLocalSongIndexAsync(IJSObjectReference browser)
    {
        var indexJson = await browser.InvokeAsync<string?>(
            "loadSongIndex",
            LocalSongIndexKey,
            LegacyLocalSongsKey,
            LocalSongSourcePrefix);
        _localSongIndex.Clear();
        if (string.IsNullOrWhiteSpace(indexJson))
        {
            return;
        }

        var entries = JsonSerializer.Deserialize<List<StoredWebSongMetadata>>(indexJson) ?? [];
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.Title))
            {
                _localSongIndex[entry.Id] = entry;
            }
        }
    }

    private async Task SaveCurrentDocumentAsync(string message)
    {
        Document.Normalize();
        var source = Document.ToChordPro();
        var metadata = new StoredWebSongMetadata(Document.Id, Document.Title);
        var storageKey = LocalSongStorageKey(Document.Id);
        var browser = await EnsureBrowserModuleAsync();
        var previousSource = await browser.InvokeAsync<string?>("storageGet", storageKey);
        var previousIndexJson = await browser.InvokeAsync<string?>("storageGet", LocalSongIndexKey);
        var stagedIndex = _localSongIndex.Values
            .ToDictionary(song => song.Id, song => song, StringComparer.OrdinalIgnoreCase);
        stagedIndex[Document.Id] = metadata;
        var stagedIndexJson = JsonSerializer.Serialize(
            stagedIndex.Values.OrderBy(song => song.Title, StringComparer.OrdinalIgnoreCase));

        try
        {
            // Persist the body and metadata index as one logical transaction.
            // Component baselines are updated only after both writes succeed.
            await browser.InvokeVoidAsync("storageSet", storageKey, source);
            await browser.InvokeVoidAsync("storageSet", LocalSongIndexKey, stagedIndexJson);
        }
        catch (Exception exception)
        {
            try
            {
                if (previousSource is null)
                {
                    await browser.InvokeVoidAsync("storageRemove", storageKey);
                }
                else
                {
                    await browser.InvokeVoidAsync("storageSet", storageKey, previousSource);
                }

                if (previousIndexJson is null)
                {
                    await browser.InvokeVoidAsync("storageRemove", LocalSongIndexKey);
                }
                else
                {
                    await browser.InvokeVoidAsync("storageSet", LocalSongIndexKey, previousIndexJson);
                }
            }
            catch
            {
                // Report the original persistence failure. The next Refresh
                // library operation re-reads the browser's actual storage state.
            }

            HasValidationError = true;
            StatusText = $"Song could not be saved: {exception.Message}";
            ChartStatusText = StatusText;
            return;
        }

        _localSongIndex.Clear();
        foreach (var entry in stagedIndex.Values)
        {
            _localSongIndex[entry.Id] = entry;
        }
        _loadedLocalSongId = Document.Id;
        SelectedSongId = Document.Id;
        SongSearchText = Document.Title;
        _savedSource = source;
        CaptureSaveBaselines();
        RefreshSongChoices();
        HasValidationError = false;
        StatusText = message;
        ChartStatusText = message;
    }

    private static string LocalSongStorageKey(string id) =>
        LocalSongSourcePrefix + Uri.EscapeDataString(id);

    private async Task<string?> ReadLocalSongSourceAsync(IJSObjectReference browser, string id)
    {
        var canonicalKey = LocalSongStorageKey(id);
        var source = await browser.InvokeAsync<string?>("storageGet", canonicalKey);
        var legacyKey = LocalSongSourcePrefix + id;
        if (!string.IsNullOrWhiteSpace(source) || string.Equals(canonicalKey, legacyKey, StringComparison.Ordinal))
        {
            return source;
        }

        source = await browser.InvokeAsync<string?>("storageGet", legacyKey);
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        // v15 and earlier could migrate old all-in-one libraries without URI
        // escaping the per-song key. Repair that key lazily when the song opens.
        await browser.InvokeVoidAsync("storageSet", canonicalKey, source);
        await browser.InvokeVoidAsync("storageRemove", legacyKey);
        return source;
    }

    private void RefreshSongChoices()
    {
        SongChoices.Clear();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var local in _localSongIndex.Values.OrderBy(song => song.Title, StringComparer.OrdinalIgnoreCase))
        {
            SongChoices.Add(new WebSongChoice(local.Id, local.Title, false));
            ids.Add(local.Id);
        }
        foreach (var builtIn in _builtInSongs.Values.OrderBy(song => song.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (ids.Add(builtIn.Id))
            {
                SongChoices.Add(new WebSongChoice(builtIn.Id, builtIn.Title, true));
            }
        }
        SongChoices.Sort((left, right) => string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase));
        SongSearchText = SelectedSongTitle;
    }

    private static double NextFourBarBoundary(
        WebSessionPlan plan,
        double positionSeconds,
        double blockDuration)
    {
        if (positionSeconds < plan.CountInSeconds)
        {
            return plan.CountInSeconds;
        }

        var musicalPosition = Math.Max(0d, positionSeconds - plan.CountInSeconds);
        var completedBlocks = Math.Floor(musicalPosition / Math.Max(0.001d, blockDuration));
        return plan.CountInSeconds + (completedBlocks + 1d) * blockDuration;
    }

    private AccompanimentStyle ResolveStyleAtPlaybackPosition(double positionSeconds)
    {
        if (_sessionPlan is null || _activeTune.Bars.Count == 0 ||
            positionSeconds < _sessionPlan.CountInSeconds)
        {
            return _activeTune.ResolveStyleAtBar(0);
        }

        var musicalPosition = Math.Max(0d, positionSeconds - _sessionPlan.CountInSeconds);
        var barOffset = (int)Math.Floor(
            musicalPosition / Math.Max(0.001d, _sessionPlan.BarDurationSeconds));
        var barIndex = barOffset % _activeTune.Bars.Count;
        return _activeTune.ResolveStyleAtBar(barIndex);
    }

    private AccompanimentStyle ResolvedPlaybackStyle
    {
        get
        {
            if (AccompanimentStyleNames.TryParseExplicit(SelectedStyleValue, out var style))
            {
                return style;
            }
            return Document.Style;
        }
    }

    private static WebStyleChoice StyleChoice(AccompanimentStyle style) =>
        new(AccompanimentStyleNames.StorageName(style), AccompanimentStyleNames.DisplayName(style), style);

    private void TransposeBars(IEnumerable<WebEditableBar> bars, int semitones)
    {
        foreach (var bar in bars)
        {
            for (var beat = 0; beat < bar.BeatCells.Count; beat++)
            {
                bar.BeatCells[beat] = ChordSymbolTransposer.TransposeChord(bar.BeatCells[beat], semitones, AccidentalPreference);
            }
        }
    }

    private void RespellingBars(IEnumerable<WebEditableBar> bars)
    {
        foreach (var bar in bars)
        {
            for (var beat = 0; beat < bar.BeatCells.Count; beat++)
            {
                bar.BeatCells[beat] = ChordSymbolTransposer.RespellingChord(bar.BeatCells[beat], AccidentalPreference);
            }
        }
    }

    private string ChordAt(int barIndex, int beatIndex)
    {
        if (barIndex < 0 || beatIndex < 0 || barIndex >= _activeTune.Bars.Count)
        {
            return "–";
        }
        return _activeTune.Bars[barIndex].GetChordAtBeat(beatIndex).Symbol;
    }

    private string SafeDocumentSource()
    {
        try { return Document.ToChordPro(); }
        catch { return string.Empty; }
    }

    private void BeginProgressUpdates()
    {
        _progressCancellation?.Cancel();
        _progressCancellation?.Dispose();
        _progressCancellation = new CancellationTokenSource();
        _ = UpdateProgressAsync(_progressCancellation.Token);
    }

    private async Task UpdateProgressAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(125));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_audioModule is null || _sessionPlan is null)
                {
                    return;
                }
                _positionSeconds = await _audioModule.InvokeAsync<double>("getPosition", cancellationToken, []);
                var styleChangePending = _pendingPlaybackStyle is not null &&
                    _positionSeconds < _pendingStyleBoundarySeconds - 0.02d;
                if (!styleChangePending && _positionSeconds >= _sessionPlan.CountInSeconds)
                {
                    _activePlaybackStyle = ResolveStyleAtPlaybackPosition(_positionSeconds);
                }
                if (_pendingPlaybackStyle is not null &&
                    _positionSeconds >= _pendingStyleBoundarySeconds - 0.02d)
                {
                    _pendingPlaybackStyle = null;
                    _pendingStyleBoundarySeconds = double.PositiveInfinity;
                }
                CurrentEnergyPercent *= 0.965;
                await TryQueueAutomaticEndingAsync();
                if (_positionSeconds >= _sessionPlan.DurationSeconds + 0.2)
                {
                    IsPlaying = false;
                    HeadOutQueued = false;
                    StatusText = "Session complete";
                    await InvokeAsync(StateHasChanged);
                    return;
                }
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { }
        catch (JSDisconnectedException) { }
    }


    private async Task TryQueueAutomaticEndingAsync()
    {
        if (!AutomaticThemeReturnEnabled || !IsPlaying || HeadOutQueued || _sessionPlan is null ||
            ReferenceEnergyPercent < 12 || _lastMidiAttack is null || CurrentStage != "Peak")
        {
            _lowEnergySince = null;
            return;
        }

        var peakBoundary = _sessionPlan.Stages.FirstOrDefault(stage => stage.Name == "Peak" &&
            _positionSeconds >= stage.StartSeconds && _positionSeconds < stage.EndSeconds);
        if (peakBoundary is null || _positionSeconds - peakBoundary.StartSeconds < _sessionPlan.BarDurationSeconds * 4)
        {
            _lowEnergySince = null;
            return;
        }

        var thresholdRatio = 0.35 + Math.Clamp(ThemeReturnSensitivity, 0, 100) * 0.005;
        if (CurrentEnergyPercent > ReferenceEnergyPercent * thresholdRatio)
        {
            _lowEnergySince = null;
            return;
        }

        _lowEnergySince ??= DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - _lowEnergySince >= TimeSpan.FromSeconds(6))
        {
            await CueEndingAsync();
            StatusText = "Theme return detected · ending queued";
            _lowEnergySince = null;
        }
    }

    private List<WebChartRow> BuildChartRows()
    {
        var rows = new List<WebChartRow>();
        for (var rowStart = 0; rowStart < Document.Bars.Count;)
        {
            var rowLength = Math.Min(4, Document.Bars.Count - rowStart);
            for (var offset = 1; offset < rowLength; offset++)
            {
                if (!string.IsNullOrWhiteSpace(Document.Bars[rowStart + offset].RehearsalMark))
                {
                    rowLength = offset;
                    break;
                }
            }
            rows.Add(new WebChartRow(
                rowStart,
                Enumerable.Range(rowStart, rowLength).ToArray()));
            rowStart += rowLength;
        }
        return rows;
    }

    private static bool IsMinorKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var trimmed = key.Trim();
        return trimmed.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.EndsWith("maj", StringComparison.OrdinalIgnoreCase);
    }

    protected static string FormatChordForDisplay(string symbol)
    {
        const string majorTriangle = "\u25B3";
        var formatted = symbol;
        foreach (var extension in new[] { "13", "11", "9", "7" })
        {
            formatted = formatted
                .Replace($"mMaj{extension}", $"m{majorTriangle}{extension}", StringComparison.OrdinalIgnoreCase)
                .Replace($"mM{extension}", $"m{majorTriangle}{extension}", StringComparison.Ordinal)
                .Replace($"m^{extension}", $"m{majorTriangle}{extension}", StringComparison.Ordinal)
                .Replace($"min^{extension}", $"m{majorTriangle}{extension}", StringComparison.OrdinalIgnoreCase)
                .Replace($"-^{extension}", $"m{majorTriangle}{extension}", StringComparison.Ordinal)
                .Replace($"maj{extension}", $"{majorTriangle}{extension}", StringComparison.OrdinalIgnoreCase)
                .Replace($"M{extension}", $"{majorTriangle}{extension}", StringComparison.Ordinal);
        }

        formatted = formatted
            .Replace("min^", $"m{majorTriangle}7", StringComparison.OrdinalIgnoreCase)
            .Replace("-^", $"m{majorTriangle}7", StringComparison.Ordinal)
            .Replace("m^", $"m{majorTriangle}7", StringComparison.Ordinal);

        var slashIndex = formatted.IndexOf('/');
        return slashIndex > 0 && slashIndex < formatted.Length - 1
            ? $"{formatted[..slashIndex]}\n {formatted[slashIndex..]}"
            : formatted;
    }

    protected static double SectionLabelFontSize(string? label) => (label?.Length ?? 0) switch
    {
        <= 3 => 18d,
        <= 5 => 13d,
        _ => 12d
    };

    private string SessionPlanSignature() =>
        $"{SafeDocumentSource()}|{SelectedStyleValue}|{Document.TempoBpm}";

    protected sealed record WebChartRow(int StartIndex, IReadOnlyList<int> BarIndices);
    protected sealed record MidiOutputChoice(string Id, string Name);

    private async Task<IJSObjectReference> EnsureAudioModuleAsync() =>
        _audioModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/jampanion-audio.js?v=24");

    private async Task<IJSObjectReference> EnsureBrowserModuleAsync() =>
        _browserModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/jampanion-browser.js?v=25");

    private async Task SelectElementTextAsync(string id)
    {
        try
        {
            var module = await EnsureBrowserModuleAsync();
            await module.InvokeVoidAsync("selectElementText", id);
        }
        catch { }
    }

    private async Task FocusAsync(string id, bool selectAll = true)
    {
        try
        {
            var module = await EnsureBrowserModuleAsync();
            await module.InvokeVoidAsync("focusElement", id, selectAll);
        }
        catch { }
    }

    private async Task BlurAsync(string id)
    {
        try
        {
            var module = await EnsureBrowserModuleAsync();
            await module.InvokeVoidAsync("blurElement", id);
        }
        catch { }
    }

    private async Task PushMixerAsync()
    {
        if (_audioModule is not null)
        {
            await _audioModule.InvokeVoidAsync("setMixer", MixerState());
        }
    }

    private WebMixerState MixerState() => new(
        PianoEnabled, BassEnabled, DrumsEnabled, MidiThruEnabled,
        PianoVolume, BassVolume, DrumsVolume, VibraphoneVolume);

    private static string PercentageWidthStyle(double value) =>
        $"width:{Math.Clamp(value, 0d, 100d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}%";

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    public async ValueTask DisposeAsync()
    {
        _progressCancellation?.Cancel();
        _progressCancellation?.Dispose();
        if (_audioModule is not null)
        {
            try
            {
                await _audioModule.InvokeVoidAsync("dispose");
                await _audioModule.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
        }
        if (_browserModule is not null)
        {
            try
            {
                await _browserModule.InvokeVoidAsync("unregisterGlobalShortcuts");
                await _browserModule.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
        }
        _dotNetReference?.Dispose();
    }
}
