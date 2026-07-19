using System.Security.Cryptography;
using Jampanion.Core.Analysis;
using Jampanion.Core.Generation;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;
using Jampanion.Live.Midi;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using MidiPlayback = Melanchall.DryWetMidi.Multimedia.Playback;

namespace Jampanion.Live.Playback;

public sealed class SessionPlaybackController : IDisposable
{
    // Some Windows MIDI drivers and software synths need a short interval
    // after the first output event before their audio path is fully awake.
    // Without this, the count-in's tick-0 note can be dispatched while the
    // device is still starting, making the first interval feel unstable even
    // though the count-in grid itself is exact.
    private const int CountInOutputWarmupMilliseconds = 80;

    private static int _playbackVariationSerial;
    private readonly object _gate = new();
    private readonly MidiPortService _midiPortService;
    private SessionPlan _basePlan;
    private readonly FeelTransitionState _feelState = new(RhythmFeel.TwoBeat);

    private MidiPlayback? _countInPlayback;
    private MidiPlayback? _currentSegmentPlayback;
    private MidiPlayback? _nextSegmentPlayback;
    private ObservableTimedObjectsCollection? _currentTimedObjects;
    private ObservableTimedObjectsCollection? _nextTimedObjects;
    private TempoMap? _tempoMap;
    private SessionPlaybackPhase _phase = SessionPlaybackPhase.Stopped;
    private int _currentChorus = 1;
    private int _currentSegmentIndex;
    private int _nextSegmentChorus = 1;
    private int _nextSegmentIndex;
    private RhythmFeel _nextSegmentFeel = RhythmFeel.TwoBeat;
    private bool _highFourBeatActive;
    private bool _highFourBeatPending;
    private int _highFourBeatTargetChorus;
    private int _highFourBeatTargetBar;
    private bool _nextSegmentHighFourBeat;
    private ArrangementContext _currentSegmentInputContext = ArrangementContext.Initial;
    private ArrangementContext _currentSegmentOutputContext = ArrangementContext.Initial;
    private ArrangementContext _nextSegmentInputContext = ArrangementContext.Initial;
    private ArrangementContext _nextSegmentOutputContext = ArrangementContext.Initial;
    private IReadOnlyList<BarArrangement> _currentBarArrangements = Array.Empty<BarArrangement>();
    private IReadOnlyList<BarArrangement> _nextBarArrangements = Array.Empty<BarArrangement>();
    private bool _nextPlaybackIsEnding;
    private bool _currentPlaybackIsPreEnding;
    private bool _nextPlaybackIsPreEnding;
    private bool _currentPlaybackUsesEndingForm;
    private bool _nextPlaybackUsesEndingForm;
    private bool _endingRequested;
    private int _endingTargetChorus;
    private bool _mandatoryEnding;
    private bool _headOutActive;
    private bool _headOutPending;
    private int _headOutTargetChorus;
    private int _arrangementChorusOffset;
    private HeadOutResumeState? _headOutResumeState;
    private int _tempoBpm;
    private int _tempoMapBpm;
    private int _sessionVariationSeed;
    private PerformanceGuidance _livePerformanceGuidance = PerformanceGuidance.Neutral;
    private PerformanceGuidance _currentSegmentGuidance = PerformanceGuidance.Neutral;
    private PerformanceGuidance _nextSegmentGuidance = PerformanceGuidance.Neutral;
    private bool _automaticChorusPlanEnabled = true;
    private long _countInLengthTicks;
    private int _countInBars = SessionConstants.CountInBars;
    private bool _disposed;

    public SessionPlaybackController(MidiPortService midiPortService, TuneForm? initialTune = null)
    {
        _midiPortService = midiPortService;
        _basePlan = SessionPlanBuilder.BuildTwoBeat(initialTune ?? TuneCatalog.Default);
        _countInLengthTicks = _basePlan.CountInLengthTicks;
    }

    public event EventHandler<string>? PlaybackError;
    public event EventHandler? SessionCompleted;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _phase != SessionPlaybackPhase.Stopped;
            }
        }
    }

    public bool IsHeadOutQueued
    {
        get
        {
            lock (_gate)
            {
                return _headOutPending;
            }
        }
    }

    public bool IsHeadOutActive
    {
        get
        {
            lock (_gate)
            {
                return _headOutActive;
            }
        }
    }

    public bool SetTempo(int tempoBpm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (tempoBpm is < 40 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(tempoBpm), "Tempo must be between 40 and 300 BPM.");
        }

        MidiPlayback? countInPlayback;
        MidiPlayback? currentSegmentPlayback;
        MidiPlayback? nextSegmentPlayback;
        double speed;

        lock (_gate)
        {
            if (_phase == SessionPlaybackPhase.Stopped || _tempoMapBpm <= 0)
            {
                return false;
            }

            _tempoBpm = tempoBpm;
            speed = (double)_tempoBpm / _tempoMapBpm;
            countInPlayback = _countInPlayback;
            currentSegmentPlayback = _currentSegmentPlayback;
            nextSegmentPlayback = _nextSegmentPlayback;
        }

        TrySetPlaybackSpeed(countInPlayback, speed);
        TrySetPlaybackSpeed(currentSegmentPlayback, speed);
        TrySetPlaybackSpeed(nextSegmentPlayback, speed);
        // The current block keeps its already-playing feel, while the prepared
        // block is rebuilt so its subdivision curve and millisecond offsets use
        // the newly selected tempo rather than merely playing faster or slower.
        PrepareNextSegment(replaceExisting: true);
        return true;
    }

    public string TuneTitle => _basePlan.Form.Title;
    public string TuneId => _basePlan.Form.Id;
    public TuneForm Tune => _basePlan.Form;
    public bool SupportsFeelChanges => _basePlan.Form.AccompanimentStyle == AccompanimentStyle.Swing;

    public bool AutomaticChorusPlanEnabled
    {
        get
        {
            lock (_gate)
            {
                return _automaticChorusPlanEnabled;
            }
        }
    }

    public void SetAutomaticChorusPlanEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool shouldRebuild;
        lock (_gate)
        {
            if (_automaticChorusPlanEnabled == enabled)
            {
                return;
            }

            _automaticChorusPlanEnabled = enabled;
            if (enabled)
            {
                _feelState.Cancel();
                _highFourBeatPending = false;
                _highFourBeatTargetChorus = 0;
                _highFourBeatTargetBar = 0;
            }

            shouldRebuild = _phase is not SessionPlaybackPhase.Stopped and not SessionPlaybackPhase.Ending;
        }

        if (shouldRebuild)
        {
            PrepareNextSegment(replaceExisting: true);
        }
    }

    public void SelectTune(TuneForm tune)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(tune);

        lock (_gate)
        {
            if (_phase != SessionPlaybackPhase.Stopped)
            {
                throw new InvalidOperationException("The tune cannot be changed while the session is running.");
            }

            _basePlan = SessionPlanBuilder.BuildTwoBeat(tune);
        }
    }

    public void Start(int tempoBpm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (tempoBpm is < 40 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(tempoBpm), "Tempo must be between 40 and 300 BPM.");
        }

        Stop();

        var tempoMap = TempoMap.Create(
            new TicksPerQuarterNoteTimeDivision((short)SessionConstants.Ppq),
            Tempo.FromBeatsPerMinute(tempoBpm),
            new TimeSignature((byte)_basePlan.Form.BeatsPerBar, (byte)4));
        var countInPlan = SessionPlanBuilder.BuildCountIn(_basePlan.Form, tempoBpm);

        var countInPlayback = _midiPortService.CreatePlayback(
            CreateBoundaryAnchoredTimedObjects(countInPlan.Notes, countInPlan.LengthTicks),
            tempoMap);
        countInPlayback.InterruptNotesOnStop = true;
        countInPlayback.Finished += CountInPlayback_Finished;
        countInPlayback.ErrorOccurred += Playback_ErrorOccurred;

        lock (_gate)
        {
            _tempoMap = tempoMap;
            _tempoBpm = tempoBpm;
            _tempoMapBpm = tempoBpm;
            _countInLengthTicks = countInPlan.LengthTicks;
            _countInBars = countInPlan.Bars;
            _countInPlayback = countInPlayback;
            _currentSegmentPlayback = null;
            _nextSegmentPlayback = null;
            _currentTimedObjects = null;
            _nextTimedObjects = null;
            _currentChorus = 1;
            _currentSegmentIndex = 0;
            _nextSegmentChorus = 1;
            _nextSegmentIndex = 0;
            _nextSegmentFeel = RhythmFeel.TwoBeat;
            _currentSegmentInputContext = ArrangementContext.Initial;
            _currentSegmentOutputContext = ArrangementContext.Initial;
            _nextSegmentInputContext = ArrangementContext.Initial;
            _nextSegmentOutputContext = ArrangementContext.Initial;
            _currentBarArrangements = Array.Empty<BarArrangement>();
            _nextBarArrangements = Array.Empty<BarArrangement>();
            _nextPlaybackIsEnding = false;
            _currentPlaybackIsPreEnding = false;
            _nextPlaybackIsPreEnding = false;
            _currentPlaybackUsesEndingForm = false;
            _nextPlaybackUsesEndingForm = false;
            _endingRequested = false;
            _endingTargetChorus = 0;
            _mandatoryEnding = false;
            _headOutActive = false;
            _headOutPending = false;
            _headOutTargetChorus = 0;
            _arrangementChorusOffset = 0;
            _headOutResumeState = null;
            _highFourBeatActive = false;
            _highFourBeatPending = false;
            _highFourBeatTargetChorus = 0;
            _highFourBeatTargetBar = 0;
            _nextSegmentHighFourBeat = false;
            _sessionVariationSeed = CreateSessionVariationSeed();
            _livePerformanceGuidance = PerformanceGuidance.Neutral;
            _currentSegmentGuidance = PerformanceGuidance.Neutral;
            _nextSegmentGuidance = PerformanceGuidance.Neutral;
            _feelState.Reset(RhythmFeel.TwoBeat);
            _phase = SessionPlaybackPhase.CountIn;
        }

        PrepareNextSegment(replaceExisting: true);

        try
        {
            _midiPortService.PrimeOutput();
            Thread.Sleep(CountInOutputWarmupMilliseconds);
            countInPlayback.Start();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public PerformanceGuidance GetPerformanceGuidance()
    {
        lock (_gate)
        {
            return _livePerformanceGuidance;
        }
    }

    public bool UpdatePerformanceGuidance(PerformanceGuidance guidance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            // MIDI analysis is retained as telemetry for the caller's HEAD OUT
            // detector. It deliberately does not rebuild accompaniment: the
            // rhythm section follows the form-level chorus arc only.
            _livePerformanceGuidance = guidance;
        }

        return false;
    }

    public bool RequestAutomaticFourBeat()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!SupportsFeelChanges ||
                _phase is SessionPlaybackPhase.Stopped or SessionPlaybackPhase.Ending ||
                _endingRequested ||
                _headOutActive ||
                _feelState.CurrentFeel == RhythmFeel.FourBeat ||
                _feelState.PendingFeel == RhythmFeel.FourBeat)
            {
                return false;
            }

            _feelState.Request(RhythmFeel.FourBeat);
        }

        PrepareNextSegment(replaceExisting: true);
        return true;
    }

    public bool RequestHighFourBeat(int targetChorus, int targetBar)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!SupportsFeelChanges ||
                _phase != SessionPlaybackPhase.Playing ||
                _endingRequested ||
                _headOutActive ||
                _feelState.CurrentFeel != RhythmFeel.FourBeat ||
                _highFourBeatActive ||
                _highFourBeatPending ||
                !IsHalfChorusBoundary(targetBar) ||
                !IsFutureBoundaryLocked(targetChorus, targetBar))
            {
                return false;
            }

            _highFourBeatPending = true;
            _highFourBeatTargetChorus = targetChorus;
            _highFourBeatTargetBar = targetBar;
        }

        PrepareNextSegment(replaceExisting: true);
        return true;
    }

    public bool CancelPendingHighFourBeat()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!_highFourBeatPending || _highFourBeatActive)
            {
                return false;
            }

            _highFourBeatPending = false;
            _highFourBeatTargetChorus = 0;
            _highFourBeatTargetBar = 0;
        }

        PrepareNextSegment(replaceExisting: true);
        return true;
    }

    public bool ConfirmHeadOutAtNextChorus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var snapshot = GetSnapshot();

        lock (_gate)
        {
            if (_phase != SessionPlaybackPhase.Playing ||
                _headOutActive ||
                _headOutPending ||
                _endingRequested ||
                snapshot.Phase != SessionPlaybackPhase.Playing ||
                snapshot.Chorus != _currentChorus)
            {
                return false;
            }

            // Keep the current chorus intact. A button press in its first two
            // bars may finish that chorus; later presses always wait for the
            // next chorus head so the theme is never played twice in a row.
            _headOutTargetChorus = snapshot.Bar <= 2
                ? _currentChorus
                : _currentChorus + 1;
            _headOutResumeState = _headOutTargetChorus > _currentChorus
                ? new HeadOutResumeState(
                    GetArrangementChorusLocked(_currentChorus),
                    _feelState.CurrentFeel,
                    _highFourBeatActive,
                    _currentSegmentOutputContext)
                : null;
            _headOutPending = true;
            _highFourBeatPending = false;
            _highFourBeatTargetChorus = 0;
            _highFourBeatTargetBar = 0;
            _mandatoryEnding = true;
            _endingRequested = true;
            _endingTargetChorus = _headOutTargetChorus;
            _feelState.Request(RhythmFeel.TwoBeat);
        }

        PrepareNextSegment(replaceExisting: true);
        return true;
    }

    public bool ConfirmHeadOutNow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        MidiPlayback playback;
        ObservableTimedObjectsCollection timedObjects;
        ArrangementContext inputContext;
        PerformanceGuidance guidance;
        int chorus;
        int segmentIndex;
        int sessionSeed;
        int tempoBpm;
        long switchTick;

        lock (_gate)
        {
            if (_phase != SessionPlaybackPhase.Playing ||
                _headOutActive ||
                _headOutPending ||
                _currentSegmentPlayback is null ||
                _currentTimedObjects is null ||
                _currentSegmentIndex > 1)
            {
                return false;
            }

            playback = _currentSegmentPlayback;
            timedObjects = _currentTimedObjects;
            inputContext = _currentSegmentInputContext;
            guidance = PerformanceGuidance.Neutral;
            chorus = _currentChorus;
            segmentIndex = _currentSegmentIndex;
            sessionSeed = _sessionVariationSeed;
            tempoBpm = _tempoBpm;

            var currentTick = playback.GetCurrentTime<MidiTimeSpan>().TimeSpan;
            switchTick = ((currentTick / _basePlan.Form.BarTicks) + 1) * _basePlan.Form.BarTicks;

            _headOutActive = true;
            _highFourBeatActive = false;
            _highFourBeatPending = false;
            _highFourBeatTargetChorus = 0;
            _highFourBeatTargetBar = 0;
            _mandatoryEnding = true;
            _endingRequested = true;
            _endingTargetChorus = chorus;
            _headOutPending = false;
            _headOutTargetChorus = 0;
            _feelState.Reset(RhythmFeel.TwoBeat);
        }

        var replacement = Stage3SessionPlanBuilder.BuildSegment(
            _basePlan.Form,
            segmentIndex,
            RhythmFeel.TwoBeat,
            chorus,
            inputContext,
            sessionSeed,
            guidance,
            isHeadOut: true,
            tempoBpm: tempoBpm);
        var replacementNotes = CreateDryWetMidiNotes(replacement.Segment.Notes)
            .Where(note => note.Time >= switchTick)
            .Cast<ITimedObject>()
            .ToArray();

        var segmentLength = (long)SessionConstants.BarsPerSegment * _basePlan.Form.BarTicks;
        var rewroteCurrentSegment = switchTick < segmentLength;
        if (rewroteCurrentSegment)
        {
            var notesToRemove = timedObjects
                .OfType<Note>()
                .Where(note => note.Time >= switchTick)
                .Cast<ITimedObject>()
                .ToArray();
            var notesToClip = timedObjects
                .OfType<Note>()
                .Where(note => note.Time < switchTick && note.Time + note.Length > switchTick)
                .ToArray();

            timedObjects.ChangeCollection(() =>
            {
                foreach (var note in notesToClip)
                {
                    timedObjects.ChangeObject(note, timedObject =>
                    {
                        var changedNote = (Note)timedObject;
                        changedNote.Length = Math.Max(1, switchTick - changedNote.Time);
                    });
                }

                timedObjects.Remove(notesToRemove);
                timedObjects.Add(replacementNotes);
            });
        }

        lock (_gate)
        {
            if (rewroteCurrentSegment &&
                ReferenceEquals(_currentSegmentPlayback, playback) &&
                ReferenceEquals(_currentTimedObjects, timedObjects))
            {
                _currentSegmentOutputContext = replacement.OutputContext;
                _currentBarArrangements = replacement.BarArrangements;
                _currentSegmentGuidance = replacement.ArrangementGuidance;
            }
        }

        PrepareNextSegment(replaceExisting: true);
        return true;
    }

    public bool CancelConfirmedHeadOutAndResumePreviousLevel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        MidiPlayback playback;
        ObservableTimedObjectsCollection timedObjects;
        ArrangementContext inputContext;
        PerformanceGuidance guidance;
        RhythmFeel resumeFeel;
        bool resumeHighFourBeat;
        int chorus;
        int arrangementChorus;
        int segmentIndex;
        int sessionSeed;
        int tempoBpm;
        long switchTick;
        HeadOutResumeState resumeState;

        lock (_gate)
        {
            if (_phase != SessionPlaybackPhase.Playing ||
                !_headOutActive ||
                _currentSegmentPlayback is null ||
                _currentTimedObjects is null ||
                _currentSegmentIndex != 0 ||
                _headOutResumeState is null)
            {
                return false;
            }

            playback = _currentSegmentPlayback;
            timedObjects = _currentTimedObjects;
            inputContext = _currentSegmentInputContext;
            chorus = _currentChorus;
            segmentIndex = _currentSegmentIndex;
            sessionSeed = _sessionVariationSeed;
            tempoBpm = _tempoBpm;
            resumeState = _headOutResumeState;

            var currentTick = playback.GetCurrentTime<MidiTimeSpan>().TimeSpan;
            switchTick = ((currentTick / _basePlan.Form.BarTicks) + 1) * _basePlan.Form.BarTicks;

            _arrangementChorusOffset = resumeState.ArrangementChorus - chorus;
            _headOutActive = false;
            _headOutPending = false;
            _headOutTargetChorus = 0;
            _mandatoryEnding = false;
            _endingRequested = false;
            _endingTargetChorus = 0;
            _currentPlaybackIsPreEnding = false;
            _currentPlaybackUsesEndingForm = false;
            _highFourBeatPending = false;
            _highFourBeatTargetChorus = 0;
            _highFourBeatTargetBar = 0;
            arrangementChorus = resumeState.ArrangementChorus;
            resumeFeel = resumeState.Feel;
            resumeHighFourBeat = resumeState.HighFourBeat;
            inputContext = resumeState.Context;
            guidance = resumeHighFourBeat
                ? CreateStructuralHighFourBeatGuidance()
                : PerformanceGuidance.Neutral;
            _highFourBeatActive = resumeHighFourBeat;
            _feelState.Reset(resumeFeel);
            _headOutResumeState = null;
        }

        var replacement = Stage3SessionPlanBuilder.BuildSegment(
            _basePlan.Form,
            segmentIndex,
            resumeFeel,
            arrangementChorus,
            inputContext,
            sessionSeed,
            guidance,
            isHeadOut: false,
            tempoBpm: tempoBpm);
        var transitionedNotes = HeadOutResumeTransition.Apply(
            replacement.Segment.Notes,
            switchTick,
            _basePlan.Form.BarTicks);
        var replacementNotes = CreateDryWetMidiNotes(transitionedNotes)
            .Where(note => note.Time >= switchTick)
            .Cast<ITimedObject>()
            .ToArray();

        var segmentLength = (long)SessionConstants.BarsPerSegment * _basePlan.Form.BarTicks;
        if (switchTick < segmentLength)
        {
            var notesToRemove = timedObjects
                .OfType<Note>()
                .Where(note => note.Time >= switchTick)
                .Cast<ITimedObject>()
                .ToArray();
            var notesToClip = timedObjects
                .OfType<Note>()
                .Where(note => note.Time < switchTick && note.Time + note.Length > switchTick)
                .ToArray();

            timedObjects.ChangeCollection(() =>
            {
                foreach (var note in notesToClip)
                {
                    timedObjects.ChangeObject(note, timedObject =>
                    {
                        var changedNote = (Note)timedObject;
                        changedNote.Length = Math.Max(1, switchTick - changedNote.Time);
                    });
                }

                timedObjects.Remove(notesToRemove);
                timedObjects.Add(replacementNotes);
            });
        }

        lock (_gate)
        {
            if (ReferenceEquals(_currentSegmentPlayback, playback) &&
                ReferenceEquals(_currentTimedObjects, timedObjects))
            {
                _currentSegmentOutputContext = replacement.OutputContext;
                _currentBarArrangements = replacement.BarArrangements;
                _currentSegmentGuidance = replacement.ArrangementGuidance;
            }
        }

        PrepareNextSegment(replaceExisting: true);
        return true;
    }

    public bool RequestFeel(RhythmFeel feel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!SupportsFeelChanges ||
                _phase is SessionPlaybackPhase.Stopped or SessionPlaybackPhase.Ending ||
                _headOutActive ||
                _headOutPending)
            {
                return false;
            }

            _feelState.Request(feel);
            if (feel == RhythmFeel.TwoBeat)
            {
                _highFourBeatPending = false;
                _highFourBeatTargetChorus = 0;
                _highFourBeatTargetBar = 0;
            }
        }

        PrepareNextSegment(replaceExisting: true);
        return true;
    }

    public bool CancelPendingFeelChange()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool changed;
        lock (_gate)
        {
            if (_phase == SessionPlaybackPhase.Stopped)
            {
                return false;
            }

            changed = _feelState.Cancel();
        }

        if (changed)
        {
            PrepareNextSegment(replaceExisting: true);
        }

        return changed;
    }

    public bool RequestEnding()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_phase is SessionPlaybackPhase.Stopped or SessionPlaybackPhase.Ending ||
                _currentPlaybackIsPreEnding ||
                (_basePlan.Form.HasSeparateEndingForm && _currentPlaybackUsesEndingForm))
            {
                return false;
            }

            _endingRequested = true;
            _endingTargetChorus = GetEndingTargetChorusLocked();
        }

        // The prepared next section may need to switch to the final head/coda form.
        PrepareNextSegment(replaceExisting: true);
        return true;
    }

    public bool CancelEnding()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!_endingRequested || _mandatoryEnding || _phase == SessionPlaybackPhase.Ending ||
                _currentPlaybackIsPreEnding ||
                (_basePlan.Form.HasSeparateEndingForm && _currentPlaybackUsesEndingForm))
            {
                return false;
            }

            _endingRequested = false;
            _endingTargetChorus = 0;
        }

        PrepareNextSegment(replaceExisting: true);
        return true;
    }

    public void Stop()
    {
        MidiPlayback? countInPlayback;
        MidiPlayback? currentSegmentPlayback;
        MidiPlayback? nextSegmentPlayback;
        PlaybackKind currentKind;
        PlaybackKind nextKind;

        lock (_gate)
        {
            countInPlayback = _countInPlayback;
            currentSegmentPlayback = _currentSegmentPlayback;
            nextSegmentPlayback = _nextSegmentPlayback;
            currentKind = _phase == SessionPlaybackPhase.Ending ? PlaybackKind.Ending : PlaybackKind.Segment;
            nextKind = _nextPlaybackIsEnding ? PlaybackKind.Ending : PlaybackKind.Segment;

            _countInPlayback = null;
            _currentSegmentPlayback = null;
            _nextSegmentPlayback = null;
            _currentTimedObjects = null;
            _nextTimedObjects = null;
            _tempoMap = null;
            _tempoBpm = 0;
            _tempoMapBpm = 0;
            _phase = SessionPlaybackPhase.Stopped;
            _currentChorus = 1;
            _currentSegmentIndex = 0;
            _currentSegmentInputContext = ArrangementContext.Initial;
            _currentSegmentOutputContext = ArrangementContext.Initial;
            _nextSegmentInputContext = ArrangementContext.Initial;
            _nextSegmentOutputContext = ArrangementContext.Initial;
            _currentBarArrangements = Array.Empty<BarArrangement>();
            _nextBarArrangements = Array.Empty<BarArrangement>();
            _nextPlaybackIsEnding = false;
            _currentPlaybackIsPreEnding = false;
            _nextPlaybackIsPreEnding = false;
            _currentPlaybackUsesEndingForm = false;
            _nextPlaybackUsesEndingForm = false;
            _endingRequested = false;
            _endingTargetChorus = 0;
            _mandatoryEnding = false;
            _headOutActive = false;
            _headOutPending = false;
            _headOutTargetChorus = 0;
            _arrangementChorusOffset = 0;
            _headOutResumeState = null;
            _highFourBeatActive = false;
            _highFourBeatPending = false;
            _highFourBeatTargetChorus = 0;
            _highFourBeatTargetBar = 0;
            _nextSegmentHighFourBeat = false;
            _sessionVariationSeed = 0;
            _livePerformanceGuidance = PerformanceGuidance.Neutral;
            _currentSegmentGuidance = PerformanceGuidance.Neutral;
            _nextSegmentGuidance = PerformanceGuidance.Neutral;
            _feelState.Reset(RhythmFeel.TwoBeat);
        }

        DisposePlayback(countInPlayback, PlaybackKind.CountIn);
        DisposePlayback(currentSegmentPlayback, currentKind);
        DisposePlayback(nextSegmentPlayback, nextKind);
    }

    public SessionPlaybackSnapshot GetSnapshot()
    {
        MidiPlayback? playback;
        SessionPlaybackPhase phase;
        int chorus;
        int segmentIndex;
        RhythmFeel currentFeel;
        RhythmFeel? pendingFeel;
        IReadOnlyList<BarArrangement> arrangements;
        bool endingRequested;
        int endingTargetChorus;
        bool mandatoryEnding;
        bool headOutActive;
        bool highFourBeatActive;
        bool highFourBeatPending;
        int highFourBeatTargetChorus;
        int highFourBeatTargetBar;
        bool currentPlaybackIsPreEnding;
        bool currentPlaybackUsesEndingForm;
        PerformanceGuidance currentSegmentGuidance;

        lock (_gate)
        {
            phase = _phase;
            chorus = _currentChorus;
            segmentIndex = _currentSegmentIndex;
            currentFeel = _feelState.CurrentFeel;
            pendingFeel = _feelState.PendingFeel;
            arrangements = _currentBarArrangements;
            endingRequested = _endingRequested;
            endingTargetChorus = _endingTargetChorus;
            mandatoryEnding = _mandatoryEnding;
            headOutActive = _headOutActive;
            highFourBeatActive = _highFourBeatActive;
            highFourBeatPending = _highFourBeatPending;
            highFourBeatTargetChorus = _highFourBeatTargetChorus;
            highFourBeatTargetBar = _highFourBeatTargetBar;
            currentPlaybackIsPreEnding = _currentPlaybackIsPreEnding;
            currentPlaybackUsesEndingForm = _currentPlaybackUsesEndingForm;
            currentSegmentGuidance = _currentSegmentGuidance;
            playback = phase == SessionPlaybackPhase.CountIn
                ? _countInPlayback
                : _currentSegmentPlayback;
        }

        if (phase == SessionPlaybackPhase.Stopped || playback is null)
        {
            return SessionPlaybackSnapshot.Stopped;
        }

        try
        {
            var tick = playback.GetCurrentTime<MidiTimeSpan>().TimeSpan;

            if (phase == SessionPlaybackPhase.CountIn)
            {
                tick = Math.Clamp(tick, 0, _countInLengthTicks - 1);
                var bar = (int)(tick / _basePlan.Form.BarTicks) + 1;
                var countInBeat = (int)((tick % _basePlan.Form.BarTicks) / SessionConstants.Ppq) + 1;
                var countInNextBoundary = currentFeel == RhythmFeel.TwoBeat && pendingFeel == RhythmFeel.FourBeat
                    ? FormBoundaryCalculator.GetNextTwoToFourBoundary(1, 1, _basePlan.Form.Bars.Count)
                    : (Chorus: 1, Bar: 1);

                return new SessionPlaybackSnapshot(
                    phase,
                    Chorus: 0,
                    Bar: 0,
                    Beat: 0,
                    Section: "Count-in",
                    Chord: "—",
                    CountInBar: bar,
                    CountInBeat: countInBeat,
                    CurrentFeel: currentFeel,
                    PendingFeel: pendingFeel,
                    HighFourBeatActive: highFourBeatActive,
                    HighFourBeatPending: highFourBeatPending,
                    HighFourBeatTargetChorus: highFourBeatTargetChorus,
                    HighFourBeatTargetBar: highFourBeatTargetBar,
                    NextBoundaryChorus: countInNextBoundary.Chorus,
                    NextBoundaryBar: countInNextBoundary.Bar,
                    FormBarCount: _basePlan.Form.Bars.Count,
                    UsingEndingForm: false,
                    ArrangementStage: "Count-in / preparing theme",
                    Arrangement: "—",
                    EndingRequested: endingRequested,
                    EndingTargetChorus: endingTargetChorus,
                    EndingCancelable: endingRequested && !mandatoryEnding &&
                        !currentPlaybackIsPreEnding &&
                        (!_basePlan.Form.HasSeparateEndingForm || !currentPlaybackUsesEndingForm),
                    HeadOutActive: headOutActive);
            }

            if (phase == SessionPlaybackPhase.Ending)
            {
                tick = Math.Clamp(tick, 0, EndingPlanBuilder.GetLengthTicks(_basePlan.Form.BeatsPerBar) - 1);
                var endingBar = (int)(tick / _basePlan.Form.BarTicks) + 1;
                var endingBeat = (int)((tick % _basePlan.Form.BarTicks) / SessionConstants.Ppq) + 1;
                var finalChord = GetEndingTonicChord();

                return new SessionPlaybackSnapshot(
                    phase,
                    Chorus: chorus,
                    Bar: _basePlan.Form.EndingFormBars.Count + endingBar,
                    Beat: endingBeat,
                    Section: "ENDING",
                    Chord: finalChord.Symbol,
                    CountInBar: 0,
                    CountInBeat: 0,
                    CurrentFeel: currentFeel,
                    PendingFeel: null,
                    HighFourBeatActive: highFourBeatActive,
                    HighFourBeatPending: false,
                    HighFourBeatTargetChorus: 0,
                    HighFourBeatTargetBar: 0,
                    NextBoundaryChorus: 0,
                    NextBoundaryBar: 0,
                    FormBarCount: _basePlan.Form.EndingFormBars.Count + TuneForm.EndingPlanBarCount,
                    UsingEndingForm: _basePlan.Form.HasSeparateEndingForm,
                    ArrangementStage: "Ending / final tonic",
                    Arrangement: "Final tonic hold",
                    EndingRequested: false,
                    EndingTargetChorus: chorus,
                    EndingCancelable: false,
                    HeadOutActive: headOutActive);
            }

            var activeBars = currentPlaybackUsesEndingForm
                ? _basePlan.Form.EndingFormBars
                : _basePlan.Form.Bars;
            var segmentBars = currentPlaybackUsesEndingForm
                ? _basePlan.Form.GetEndingLeadInSegmentBarCount(segmentIndex)
                : _basePlan.Form.GetSegmentBarCount(segmentIndex);
            var segmentLength = (long)segmentBars * _basePlan.Form.BarTicks;
            tick = Math.Clamp(tick, 0, segmentLength - 1);
            var barWithinSegment = (int)(tick / _basePlan.Form.BarTicks);
            var barIndex = segmentIndex * SessionConstants.BarsPerSegment + barWithinSegment;
            var beat = (int)((tick % _basePlan.Form.BarTicks) / SessionConstants.Ppq) + 1;
            var barInfo = activeBars[barIndex];
            var nextBoundary = currentPlaybackIsPreEnding
                ? (Chorus: chorus, Bar: _basePlan.Form.EndingStartBar)
                : currentPlaybackUsesEndingForm
                    ? (Chorus: chorus, Bar: (segmentIndex + 1) * SessionConstants.BarsPerSegment + 1)
                    : FormBoundaryCalculator.GetNextBoundary(chorus, segmentIndex, _basePlan.Form.SegmentCount);

            if (!currentPlaybackIsPreEnding &&
                currentFeel == RhythmFeel.TwoBeat &&
                pendingFeel == RhythmFeel.FourBeat)
            {
                nextBoundary = FormBoundaryCalculator.GetNextTwoToFourBoundary(
                    chorus,
                    barIndex + 1,
                    activeBars.Count);
            }

            var arrangement = barWithinSegment < arrangements.Count
                ? arrangements[barWithinSegment]
                : new BarArrangement(barWithinSegment, ResponderRole.Structural, PhraseFunction.Ground, false);

            return new SessionPlaybackSnapshot(
                phase,
                Chorus: chorus,
                Bar: barIndex + 1,
                Beat: beat,
                Section: barInfo.Section,
                Chord: barInfo.GetChordAtBeat(beat - 1).Symbol,
                CountInBar: 0,
                CountInBeat: 0,
                CurrentFeel: currentFeel,
                PendingFeel: pendingFeel,
                HighFourBeatActive: highFourBeatActive,
                HighFourBeatPending: highFourBeatPending,
                HighFourBeatTargetChorus: highFourBeatTargetChorus,
                HighFourBeatTargetBar: highFourBeatTargetBar,
                NextBoundaryChorus: nextBoundary.Chorus,
                NextBoundaryBar: nextBoundary.Bar,
                FormBarCount: activeBars.Count,
                UsingEndingForm: _basePlan.Form.HasSeparateEndingForm && currentPlaybackUsesEndingForm,
                ArrangementStage: ArrangementStageDisplayResolver.Resolve(
                    _basePlan.Form.AccompanimentStyle,
                    chorus,
                    barIndex + 1,
                    activeBars.Count,
                    currentFeel,
                    currentSegmentGuidance,
                    currentPlaybackUsesEndingForm,
                    headOutActive).Text,
                Arrangement: arrangement.Function switch
                {
                    PhraseFunction.Answer => "Piano answer",
                    PhraseFunction.Space => "Space",
                    PhraseFunction.Build when arrangement.Responder == ResponderRole.Piano => "Piano build",
                    PhraseFunction.Build => "Drums build",
                    PhraseFunction.Setup => "Section setup",
                    PhraseFunction.Release => "Release",
                    PhraseFunction.Comment when arrangement.Responder == ResponderRole.Piano => "Piano comment",
                    PhraseFunction.Comment => "Drums comment",
                    _ => "Time and harmony"
                },
                EndingRequested: endingRequested,
                EndingTargetChorus: endingTargetChorus,
                EndingCancelable: endingRequested && !mandatoryEnding &&
                    !currentPlaybackIsPreEnding &&
                    (!_basePlan.Form.HasSeparateEndingForm || !currentPlaybackUsesEndingForm),
                HeadOutActive: headOutActive);
        }
        catch (ObjectDisposedException)
        {
            return SessionPlaybackSnapshot.Stopped;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyList<Note> CreateDryWetMidiNotes(IEnumerable<ScheduledNote> scheduledNotes)
    {
        return scheduledNotes
            .Select(scheduledNote => new Note(
                (SevenBitNumber)scheduledNote.NoteNumber,
                scheduledNote.DurationTicks,
                scheduledNote.StartTick)
            {
                Channel = (FourBitNumber)scheduledNote.Channel,
                Velocity = (SevenBitNumber)scheduledNote.Velocity,
                OffVelocity = (SevenBitNumber)0
            })
            .ToArray();
    }

    private static IReadOnlyList<ITimedObject> CreateBoundaryAnchoredTimedObjects(
        IEnumerable<ScheduledNote> scheduledNotes,
        long lengthTicks)
    {
        if (lengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks));
        }

        var notes = CreateDryWetMidiNotes(scheduledNotes);
        var result = new ITimedObject[notes.Count + 1];
        for (var index = 0; index < notes.Count; index++)
        {
            result[index] = notes[index];
        }

        // Playback.Finished follows the last timed object, not SegmentPlan.LengthTicks.
        // A silent MIDI meta event pins every block to its exact musical boundary,
        // even when the generated phrase intentionally leaves space in bar four.
        result[^1] = new TimedEvent(new MarkerEvent("Jampanion segment boundary"), lengthTicks);
        return result;
    }

    private void PrepareNextSegment(bool replaceExisting)
    {
        TempoMap? tempoMap;
        int chorus;
        int arrangementChorus;
        int segmentIndex;
        RhythmFeel feel;
        ArrangementContext inputContext;
        int sessionSeed;
        bool buildEnding;
        bool buildPreEnding;
        bool useEndingForm;
        bool highFourBeat;
        bool isHeadOut;
        int tempoBpm;
        double playbackSpeed;
        PerformanceGuidance performanceGuidance;

        lock (_gate)
        {
            if (_phase is SessionPlaybackPhase.Stopped or SessionPlaybackPhase.Ending || _tempoMap is null)
            {
                return;
            }

            if (!replaceExisting && _nextSegmentPlayback is not null)
            {
                return;
            }

            buildEnding = ShouldPrepareEndingLocked();
            if (buildEnding)
            {
                chorus = _currentChorus;
                segmentIndex = _basePlan.Form.EndingLeadInSegmentCount;
                useEndingForm = true;
                feel = _feelState.CurrentFeel;
            }
            else
            {
                var coordinates = GetNextSegmentCoordinatesLocked();
                chorus = coordinates.Chorus;
                segmentIndex = coordinates.SegmentIndex;
                useEndingForm = coordinates.UseEndingForm;
                feel = GetPlannedFeelLocked(chorus, segmentIndex);
            }

            buildPreEnding = !buildEnding &&
                useEndingForm &&
                segmentIndex == _basePlan.Form.EndingLeadInSegmentCount - 1;

            inputContext = _phase == SessionPlaybackPhase.CountIn
                ? ArrangementContext.Initial
                : _currentSegmentOutputContext;
            tempoMap = _tempoMap;
            sessionSeed = _sessionVariationSeed;
            tempoBpm = _tempoBpm;
            playbackSpeed = _tempoMapBpm > 0
                ? (double)_tempoBpm / _tempoMapBpm
                : 1d;
            highFourBeat = !buildEnding && GetPlannedHighFourBeatLocked(chorus, segmentIndex, feel);
            isHeadOut = IsHeadOutPlannedLocked(chorus);
            arrangementChorus = GetArrangementChorusLocked(chorus);
            performanceGuidance = highFourBeat
                ? CreateStructuralHighFourBeatGuidance()
                : PerformanceGuidance.Neutral;
        }

        MidiPlayback playback;
        ObservableTimedObjectsCollection timedObjects;
        ArrangementContext outputContext;
        IReadOnlyList<BarArrangement> barArrangements;
        PerformanceGuidance generatedGuidance;

        if (buildEnding)
        {
            var endingPlan = EndingPlanBuilder.Build(GetEndingTonicChord(), _basePlan.Form.AccompanimentStyle, _basePlan.Form.BeatsPerBar);
            timedObjects = new ObservableTimedObjectsCollection(
                CreateBoundaryAnchoredTimedObjects(endingPlan.Notes, endingPlan.LengthTicks));
            playback = _midiPortService.CreatePlayback(
                timedObjects,
                tempoMap);
            playback.Finished += EndingPlayback_Finished;
            outputContext = inputContext;
            barArrangements = Array.Empty<BarArrangement>();
            generatedGuidance = PerformanceGuidance.Neutral;
        }
        else
        {
            var generatedPlan = useEndingForm
                ? Stage3SessionPlanBuilder.BuildEndingLeadInSegment(
                    _basePlan.Form,
                    segmentIndex,
                    feel,
                    arrangementChorus,
                    inputContext,
                    sessionSeed,
                    performanceGuidance,
                    tempoBpm)
                : Stage3SessionPlanBuilder.BuildSegment(
                    _basePlan.Form,
                    segmentIndex,
                    feel,
                    arrangementChorus,
                    inputContext,
                    sessionSeed,
                    performanceGuidance,
                    isHeadOut: isHeadOut,
                    tempoBpm: tempoBpm);
            timedObjects = new ObservableTimedObjectsCollection(
                CreateBoundaryAnchoredTimedObjects(generatedPlan.Segment.Notes, generatedPlan.Segment.LengthTicks));
            playback = _midiPortService.CreatePlayback(
                timedObjects,
                tempoMap);
            playback.Finished += SegmentPlayback_Finished;
            outputContext = generatedPlan.OutputContext;
            barArrangements = generatedPlan.BarArrangements;
            generatedGuidance = generatedPlan.ArrangementGuidance;
        }

        playback.InterruptNotesOnStop = true;
        TrySetPlaybackSpeed(playback, playbackSpeed);
        playback.ErrorOccurred += Playback_ErrorOccurred;

        MidiPlayback? replaced = null;
        var replacedKind = PlaybackKind.Segment;
        var accepted = false;

        lock (_gate)
        {
            if (_phase is not SessionPlaybackPhase.Stopped and not SessionPlaybackPhase.Ending && _tempoMap == tempoMap)
            {
                var shouldNowBuildEnding = ShouldPrepareEndingLocked();
                var currentCoordinates = shouldNowBuildEnding
                    ? new SegmentCoordinates(_currentChorus, _basePlan.Form.EndingLeadInSegmentCount, true)
                    : GetNextSegmentCoordinatesLocked();
                var shouldNowBuildPreEnding = !shouldNowBuildEnding &&
                    currentCoordinates.UseEndingForm &&
                    currentCoordinates.SegmentIndex == _basePlan.Form.EndingLeadInSegmentCount - 1;
                var mayReplace = replaceExisting || _nextSegmentPlayback is null;
                var requestStillMatches = shouldNowBuildEnding == buildEnding &&
                    shouldNowBuildPreEnding == buildPreEnding;

                if (!buildEnding && requestStillMatches)
                {
                    var currentDesiredFeel = GetPlannedFeelLocked(
                        currentCoordinates.Chorus,
                        currentCoordinates.SegmentIndex);
                    var currentDesiredHighFourBeat = GetPlannedHighFourBeatLocked(
                        currentCoordinates.Chorus,
                        currentCoordinates.SegmentIndex,
                        currentDesiredFeel);
                    var currentDesiredGuidance = currentDesiredHighFourBeat
                        ? CreateStructuralHighFourBeatGuidance()
                        : PerformanceGuidance.Neutral;
                    requestStillMatches = requestStillMatches &&
                        currentDesiredGuidance.PlanningKey == performanceGuidance.PlanningKey &&
                        currentCoordinates.Chorus == chorus &&
                        currentCoordinates.SegmentIndex == segmentIndex &&
                        currentCoordinates.UseEndingForm == useEndingForm &&
                        currentDesiredFeel == feel &&
                        currentDesiredHighFourBeat == highFourBeat;
                }

                if (requestStillMatches && mayReplace)
                {
                    replaced = _nextSegmentPlayback;
                    replacedKind = _nextPlaybackIsEnding ? PlaybackKind.Ending : PlaybackKind.Segment;
                    _nextSegmentPlayback = playback;
                    _nextTimedObjects = timedObjects;
                    _nextSegmentChorus = chorus;
                    _nextSegmentIndex = segmentIndex;
                    _nextSegmentFeel = feel;
                    _nextSegmentHighFourBeat = highFourBeat;
                    _nextSegmentInputContext = inputContext;
                    _nextSegmentOutputContext = outputContext;
                    _nextBarArrangements = barArrangements;
                    _nextPlaybackIsEnding = buildEnding;
                    _nextPlaybackIsPreEnding = buildPreEnding;
                    _nextPlaybackUsesEndingForm = !buildEnding && useEndingForm;
                    _nextSegmentGuidance = generatedGuidance;
                    accepted = true;
                }
            }
        }

        if (!accepted)
        {
            DisposePlayback(playback, buildEnding ? PlaybackKind.Ending : PlaybackKind.Segment);
            return;
        }

        DisposePlayback(replaced, replacedKind);
    }

    private bool ShouldPrepareEndingLocked()
    {
        return _phase == SessionPlaybackPhase.Playing &&
            _currentPlaybackUsesEndingForm &&
            _currentPlaybackIsPreEnding &&
            _endingRequested &&
            _endingTargetChorus == _currentChorus;
    }

    private int GetEndingTargetChorusLocked()
    {
        if (_phase == SessionPlaybackPhase.CountIn)
        {
            return 1;
        }

        if (_basePlan.Form.HasSeparateEndingForm)
        {
            return _currentChorus + 1;
        }

        return _currentSegmentIndex < _basePlan.Form.EndingLeadInSegmentCount - 1
            ? _currentChorus
            : _currentChorus + 1;
    }

    private ChordSpec GetEndingTonicChord()
    {
        return _basePlan.Form.TonicChord;
    }

    private SegmentCoordinates GetNextSegmentCoordinatesLocked()
    {
        if (_phase == SessionPlaybackPhase.CountIn)
        {
            var useEndingForm = _endingRequested && _endingTargetChorus == 1;
            return new SegmentCoordinates(1, 0, useEndingForm);
        }

        if (_currentPlaybackUsesEndingForm)
        {
            if (!_basePlan.Form.HasSeparateEndingForm && !_endingRequested)
            {
                return _currentSegmentIndex == _basePlan.Form.SegmentCount - 1
                    ? new SegmentCoordinates(_currentChorus + 1, 0, false)
                    : new SegmentCoordinates(_currentChorus, _currentSegmentIndex + 1, false);
            }

            return new SegmentCoordinates(_currentChorus, _currentSegmentIndex + 1, true);
        }

        if (_currentSegmentIndex == _basePlan.Form.SegmentCount - 1)
        {
            var nextChorus = _currentChorus + 1;
            var useEndingForm = _endingRequested && _endingTargetChorus == nextChorus;
            var loopStartSegment = useEndingForm ? 0 : _basePlan.Form.LoopStartSegmentIndex;
            return new SegmentCoordinates(nextChorus, loopStartSegment, useEndingForm);
        }

        var endingThisChorus = _endingRequested && _endingTargetChorus == _currentChorus;
        return new SegmentCoordinates(_currentChorus, _currentSegmentIndex + 1, endingThisChorus);
    }

    private RhythmFeel GetPlannedFeelLocked(int chorus, int segmentIndex)
    {
        if (!SupportsFeelChanges)
        {
            return RhythmFeel.TwoBeat;
        }

        if (IsHeadOutPlannedLocked(chorus))
        {
            return RhythmFeel.TwoBeat;
        }

        if (_automaticChorusPlanEnabled)
        {
            return ChorusFeelPlan.GetFeel(GetArrangementChorusLocked(chorus), false);
        }

        var targetBarIndex = segmentIndex * SessionConstants.BarsPerSegment;
        return FormBoundaryCalculator.ResolvePlannedFeel(
            _feelState.CurrentFeel,
            _feelState.PendingFeel,
            chorus,
            targetBarIndex,
            _basePlan.Form.Bars.Count);
    }

    private bool GetPlannedHighFourBeatLocked(int chorus, int segmentIndex, RhythmFeel feel)
    {
        if (!SupportsFeelChanges || feel != RhythmFeel.FourBeat || IsHeadOutPlannedLocked(chorus))
        {
            return false;
        }

        if (_automaticChorusPlanEnabled)
        {
            return ChorusFeelPlan.IsHighStage(GetArrangementChorusLocked(chorus), false);
        }

        if (_highFourBeatActive)
        {
            return true;
        }

        if (!_highFourBeatPending)
        {
            return false;
        }

        var targetBar = segmentIndex * SessionConstants.BarsPerSegment + 1;
        return chorus > _highFourBeatTargetChorus ||
            (chorus == _highFourBeatTargetChorus && targetBar >= _highFourBeatTargetBar);
    }

    private bool IsHeadOutPlannedLocked(int chorus) =>
        _headOutActive ||
        (_headOutPending && _headOutTargetChorus == chorus);

    private int GetArrangementChorusLocked(int playbackChorus) =>
        Math.Max(1, playbackChorus + _arrangementChorusOffset);

    private bool IsFutureBoundaryLocked(int targetChorus, int targetBar)
    {
        if (targetChorus < _currentChorus)
        {
            return false;
        }

        if (targetChorus > _currentChorus)
        {
            return true;
        }

        var currentBarStart = _currentSegmentIndex * SessionConstants.BarsPerSegment + 1;
        return targetBar > currentBarStart;
    }

    private bool IsHalfChorusBoundary(int oneBasedBar) =>
        oneBasedBar == 1 || oneBasedBar == _basePlan.Form.Bars.Count / 2 + 1;

    private static PerformanceGuidance CreateStructuralHighFourBeatGuidance()
    {
        return PerformanceGuidance.Neutral with
        {
            Intensity = PerformanceIntensity.High,
            Energy = 0.88,
            ShortEnergy = 0.88,
            Density = 0.86,
            AverageVelocity = 74,
            Motion = 0.78,
            HighEnergySustained = true,
            HighEnergyBars = 4.0,
            HighStage = true
        };
    }

    private void CountInPlayback_Finished(object? sender, EventArgs e)
    {
        MidiPlayback? toStart;
        MidiPlayback? retired;
        RhythmFeel plannedFeel;
        int plannedChorus;
        int plannedSegment;

        lock (_gate)
        {
            if (_phase != SessionPlaybackPhase.CountIn || !ReferenceEquals(sender, _countInPlayback))
            {
                return;
            }

            if (_nextSegmentPlayback is null)
            {
                PlaybackError?.Invoke(this, "No prepared segment was available after the count-in.");
                return;
            }

            retired = _countInPlayback;
            _countInPlayback = null;
            toStart = _nextSegmentPlayback;
            plannedChorus = _nextSegmentChorus;
            plannedSegment = _nextSegmentIndex;
            plannedFeel = _nextSegmentFeel;
            _nextSegmentPlayback = null;
            _currentTimedObjects = _nextTimedObjects;
            _nextTimedObjects = null;
            _nextPlaybackIsEnding = false;
            _currentPlaybackIsPreEnding = _nextPlaybackIsPreEnding;
            _nextPlaybackIsPreEnding = false;
            _currentPlaybackUsesEndingForm = _nextPlaybackUsesEndingForm;
            _nextPlaybackUsesEndingForm = false;

            _currentSegmentPlayback = toStart;
            _currentSegmentInputContext = _nextSegmentInputContext;
            _currentSegmentOutputContext = _nextSegmentOutputContext;
            _currentBarArrangements = _nextBarArrangements;
            _currentSegmentGuidance = _nextSegmentGuidance;
            _nextSegmentGuidance = PerformanceGuidance.Neutral;
            _nextSegmentInputContext = ArrangementContext.Initial;
            _nextSegmentOutputContext = ArrangementContext.Initial;
            _nextBarArrangements = Array.Empty<BarArrangement>();
            _currentChorus = plannedChorus;
            _currentSegmentIndex = plannedSegment;
            _feelState.ApplyPlannedBoundary(plannedFeel);
            _highFourBeatActive = _nextSegmentHighFourBeat;
            if (_highFourBeatActive)
            {
                _highFourBeatPending = false;
                _highFourBeatTargetChorus = 0;
                _highFourBeatTargetBar = 0;
            }
            _nextSegmentHighFourBeat = false;
            _phase = SessionPlaybackPhase.Playing;
        }

        try
        {
            // Start the prepared clock immediately at the boundary. Disposal of
            // the completed clock is cleanup and must not delay the downbeat.
            toStart.Start();
            RetirePlayback(retired, PlaybackKind.CountIn);
            PrepareNextSegment(replaceExisting: false);
        }
        catch (Exception ex)
        {
            RetirePlayback(retired, PlaybackKind.CountIn);
            Stop();
            PlaybackError?.Invoke(this, $"Could not start first section: {ex.Message}");
        }
    }

    private void SegmentPlayback_Finished(object? sender, EventArgs e)
    {
        MidiPlayback? toStart = null;
        MidiPlayback? retired = null;
        RhythmFeel plannedFeel = RhythmFeel.TwoBeat;
        int plannedChorus = 0;
        int plannedSegment = 0;
        bool plannedHighFourBeat = false;
        bool startEnding = false;
        bool startPreEnding = false;
        bool startUsesEndingForm = false;
        var prepareMissingNext = false;

        lock (_gate)
        {
            if (_phase != SessionPlaybackPhase.Playing || !ReferenceEquals(sender, _currentSegmentPlayback))
            {
                return;
            }

            if (_nextSegmentPlayback is null)
            {
                // Generation is synchronous, but a busy machine can still
                // reach the boundary before the look-ahead block is installed.
                // Give the planner one last chance instead of leaving the
                // session in Playing with no clock and no sound.
                prepareMissingNext = true;
            }
            else
            {
                retired = _currentSegmentPlayback;
                toStart = _nextSegmentPlayback;
                plannedChorus = _nextSegmentChorus;
                plannedSegment = _nextSegmentIndex;
                plannedFeel = _nextSegmentFeel;
                plannedHighFourBeat = _nextSegmentHighFourBeat;
                startEnding = _nextPlaybackIsEnding;
                startPreEnding = _nextPlaybackIsPreEnding;
                startUsesEndingForm = _nextPlaybackUsesEndingForm;
                _nextSegmentPlayback = null;
                _currentTimedObjects = _nextTimedObjects;
                _nextTimedObjects = null;
                _nextPlaybackIsEnding = false;
                _nextPlaybackIsPreEnding = false;
                _nextPlaybackUsesEndingForm = false;
                _nextSegmentHighFourBeat = false;

                _currentSegmentPlayback = toStart;
                _currentSegmentInputContext = _nextSegmentInputContext;
                _currentSegmentOutputContext = _nextSegmentOutputContext;
                _currentBarArrangements = _nextBarArrangements;
                _currentSegmentGuidance = _nextSegmentGuidance;
                _nextSegmentGuidance = PerformanceGuidance.Neutral;
                _nextSegmentInputContext = ArrangementContext.Initial;
                _nextSegmentOutputContext = ArrangementContext.Initial;
                _nextBarArrangements = Array.Empty<BarArrangement>();

                if (startEnding)
                {
                    _currentChorus = plannedChorus;
                    _currentSegmentIndex = _basePlan.Form.EndingLeadInSegmentCount - 1;
                    _currentPlaybackIsPreEnding = false;
                    _currentPlaybackUsesEndingForm = false;
                    _endingRequested = false;
                    _endingTargetChorus = 0;
                    _feelState.Cancel();
                    _highFourBeatActive = false;
                    _highFourBeatPending = false;
                    _highFourBeatTargetChorus = 0;
                    _highFourBeatTargetBar = 0;
                    _phase = SessionPlaybackPhase.Ending;
                }
                else
                {
                    _currentChorus = plannedChorus;
                    _currentSegmentIndex = plannedSegment;
                    _currentPlaybackIsPreEnding = startPreEnding;
                    _currentPlaybackUsesEndingForm = startUsesEndingForm;

                    if (_headOutPending && _headOutTargetChorus == _currentChorus)
                    {
                        _headOutActive = true;
                        _headOutPending = false;
                        _headOutTargetChorus = 0;
                    }

                    if (!startPreEnding &&
                        !_currentPlaybackUsesEndingForm &&
                        !_basePlan.Form.HasSeparateEndingForm &&
                        _currentSegmentIndex >= _basePlan.Form.EndingLeadInSegmentCount - 1 &&
                        _endingRequested &&
                        _endingTargetChorus == _currentChorus)
                    {
                        _endingTargetChorus = _currentChorus + 1;
                    }

                    _feelState.ApplyPlannedBoundary(plannedFeel);
                    _highFourBeatActive = plannedFeel == RhythmFeel.FourBeat && plannedHighFourBeat;
                    if (_highFourBeatActive)
                    {
                        _highFourBeatPending = false;
                        _highFourBeatTargetChorus = 0;
                        _highFourBeatTargetBar = 0;
                    }
                }
            }
        }

        if (prepareMissingNext)
        {
            try
            {
                PrepareNextSegment(replaceExisting: false);
            }
            catch (Exception ex)
            {
                Stop();
                PlaybackError?.Invoke(this, $"Could not prepare the next section: {ex.Message}");
                return;
            }

            // Re-enter the transition path once the look-ahead block has been
            // installed. The sender is still the finished current playback,
            // so the normal identity guard remains effective.
            SegmentPlayback_Finished(sender, e);
            return;
        }

        try
        {
            // The Finished callback already runs on the timing path. Start the
            // prepared block before doing any potentially blocking cleanup.
            toStart!.Start();
            RetirePlayback(retired, PlaybackKind.Segment);
            if (!startEnding)
            {
                PrepareNextSegment(replaceExisting: false);
            }
        }
        catch (Exception ex)
        {
            RetirePlayback(retired, PlaybackKind.Segment);
            Stop();
            PlaybackError?.Invoke(this, startEnding
                ? $"Could not start the ending: {ex.Message}"
                : $"Could not start the next section: {ex.Message}");
        }
    }

    private void EndingPlayback_Finished(object? sender, EventArgs e)
    {
        MidiPlayback? retired;

        lock (_gate)
        {
            if (_phase != SessionPlaybackPhase.Ending || !ReferenceEquals(sender, _currentSegmentPlayback))
            {
                return;
            }

            retired = _currentSegmentPlayback;
            _currentSegmentPlayback = null;
            _nextSegmentPlayback = null;
            _currentTimedObjects = null;
            _nextTimedObjects = null;
            _tempoMap = null;
            _phase = SessionPlaybackPhase.Stopped;
            _currentSegmentInputContext = ArrangementContext.Initial;
            _currentSegmentOutputContext = ArrangementContext.Initial;
            _nextSegmentInputContext = ArrangementContext.Initial;
            _nextSegmentOutputContext = ArrangementContext.Initial;
            _currentBarArrangements = Array.Empty<BarArrangement>();
            _nextBarArrangements = Array.Empty<BarArrangement>();
            _nextPlaybackIsEnding = false;
            _currentPlaybackIsPreEnding = false;
            _nextPlaybackIsPreEnding = false;
            _currentPlaybackUsesEndingForm = false;
            _nextPlaybackUsesEndingForm = false;
            _endingRequested = false;
            _endingTargetChorus = 0;
            _mandatoryEnding = false;
            _headOutActive = false;
            _headOutPending = false;
            _headOutTargetChorus = 0;
            _arrangementChorusOffset = 0;
            _headOutResumeState = null;
            _highFourBeatActive = false;
            _highFourBeatPending = false;
            _highFourBeatTargetChorus = 0;
            _highFourBeatTargetBar = 0;
            _nextSegmentHighFourBeat = false;
            _sessionVariationSeed = 0;
            _livePerformanceGuidance = PerformanceGuidance.Neutral;
            _currentSegmentGuidance = PerformanceGuidance.Neutral;
            _nextSegmentGuidance = PerformanceGuidance.Neutral;
            _feelState.Reset(RhythmFeel.TwoBeat);
        }

        RetirePlayback(retired, PlaybackKind.Ending);
        SessionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void Playback_ErrorOccurred(object? sender, PlaybackErrorOccurredEventArgs e)
    {
        var message = e.Exception?.Message ?? "Unknown playback error.";
        Stop();
        PlaybackError?.Invoke(this, $"MIDI playback error: {message}");
    }

    private static int CreateSessionVariationSeed()
    {
        // The generators remain deterministic for a given seed, but each new
        // playback receives fresh entropy. The serial is mixed in as a second
        // guard so two starts cannot accidentally reuse the same variation if
        // the entropy source returns the same value.
        var serial = unchecked((uint)Interlocked.Increment(ref _playbackVariationSerial));
        var entropy = unchecked((uint)RandomNumberGenerator.GetInt32(int.MaxValue));
        var mixed = entropy ^ (serial * 0x9E3779B9u);
        var seed = (int)(mixed & 0x7FFF_FFFFu);
        return seed == 0 ? 1 : seed;
    }

    private static void TrySetPlaybackSpeed(MidiPlayback? playback, double speed)
    {
        if (playback is null)
        {
            return;
        }

        try
        {
            playback.Speed = speed;
        }
        catch (ObjectDisposedException)
        {
            // A segment can finish while a tempo change is being applied.
        }
    }

    private void RetirePlayback(MidiPlayback? playback, PlaybackKind kind)
    {
        if (playback is null)
        {
            return;
        }

        Unsubscribe(playback, kind);
        try
        {
            // A finished Playback must be retired before the next one starts. The
            // old asynchronous disposal could otherwise run after the new downbeat
            // and issue a late cleanup on the same MIDI output.
            playback.Dispose();
        }
        catch
        {
            // The playback has already finished; disposal failure must not
            // interrupt the next prepared section.
        }
    }

    private void DisposePlayback(MidiPlayback? playback, PlaybackKind kind)
    {
        if (playback is null)
        {
            return;
        }

        try
        {
            if (playback.IsRunning)
            {
                playback.Stop();
            }
        }
        catch (Exception ex)
        {
            PlaybackError?.Invoke(this, $"Could not stop MIDI playback cleanly: {ex.Message}");
        }
        finally
        {
            Unsubscribe(playback, kind);
            playback.Dispose();
        }
    }

    private void Unsubscribe(MidiPlayback playback, PlaybackKind kind)
    {
        switch (kind)
        {
            case PlaybackKind.CountIn:
                playback.Finished -= CountInPlayback_Finished;
                break;
            case PlaybackKind.Ending:
                playback.Finished -= EndingPlayback_Finished;
                break;
            default:
                playback.Finished -= SegmentPlayback_Finished;
                break;
        }

        playback.ErrorOccurred -= Playback_ErrorOccurred;
    }

    private readonly record struct SegmentCoordinates(
        int Chorus,
        int SegmentIndex,
        bool UseEndingForm);

    private sealed record HeadOutResumeState(
        int ArrangementChorus,
        RhythmFeel Feel,
        bool HighFourBeat,
        ArrangementContext Context);

    private enum PlaybackKind
    {
        CountIn,
        Segment,
        Ending
    }
}
