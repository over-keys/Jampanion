using Jampanion.Core.Generation;
using Jampanion.Core.Music;

namespace Jampanion.Web.Audio;

public static class WebSessionPlanner
{
    public const int MaximumOpenEndedChoruses = 12;

    public static WebSessionPlan BuildSession(
        TuneForm tune,
        int tempoBpm,
        int seed,
        int? headOutChorus = null,
        int? generatedChoruses = null,
        bool endWithHeadOut = true,
        int? generatedSegments = null)
    {
        ArgumentNullException.ThrowIfNull(tune);
        tempoBpm = Math.Clamp(tempoBpm, 40, 300);
        var finalChorus = Math.Clamp(
            headOutChorus ?? generatedChoruses ?? MaximumOpenEndedChoruses,
            1,
            MaximumOpenEndedChoruses);
        int? resolvedHeadOutChorus = headOutChorus ?? (endWithHeadOut ? finalChorus : null);

        var secondsPerTick = 60d / tempoBpm / SessionConstants.Ppq;
        var countInTicks = SessionConstants.CountInBars * tune.BarTicks;
        var chorusTicks = tune.Bars.Sum(bar => bar.BarTicks);
        var notes = new List<WebScheduledNote>();
        AddCountIn(notes, tune, secondsPerTick);

        var context = ArrangementContext.Initial;
        long sessionTicks = countInTicks;
        var boundaries = new List<WebStageBoundary>(finalChorus);
        var segmentLimit = generatedSegments is int requestedSegments
            ? Math.Max(1, requestedSegments)
            : int.MaxValue;
        var generatedSegmentCount = 0;
        var segmentLimitReached = false;

        for (var chorus = 1; chorus <= finalChorus; chorus++)
        {
            var isHeadOut = resolvedHeadOutChorus == chorus;
            var stage = ResolveStage(chorus, isHeadOut);
            var stageStartTicks = sessionTicks;

            for (var segmentIndex = 0; segmentIndex < tune.SegmentCount; segmentIndex++)
            {
                var segment = Stage3SessionPlanBuilder.BuildSegment(
                    tune,
                    segmentIndex,
                    stage.Feel,
                    chorus,
                    context,
                    sessionSeed: seed + chorus * 1009,
                    performanceGuidance: null,
                    isHeadOut: stage.IsHeadOut,
                    tempoBpm: tempoBpm);

                foreach (var note in segment.Segment.Notes)
                {
                    var absoluteStart = sessionTicks + note.StartTick;
                    notes.Add(new WebScheduledNote(
                        StartSeconds: absoluteStart * secondsPerTick,
                        DurationSeconds: Math.Max(0.01d, note.DurationTicks * secondsPerTick),
                        NoteNumber: note.NoteNumber,
                        Velocity: note.Velocity,
                        Channel: note.Channel));
                }

                context = segment.OutputContext;
                sessionTicks += segment.Segment.LengthTicks;
                generatedSegmentCount++;
                if (generatedSegmentCount >= segmentLimit)
                {
                    segmentLimitReached = true;
                    break;
                }
            }

            boundaries.Add(new WebStageBoundary(
                stage.Name,
                chorus,
                stageStartTicks * secondsPerTick,
                sessionTicks * secondsPerTick));

            if (isHeadOut || segmentLimitReached)
            {
                break;
            }
        }

        return new WebSessionPlan(
            notes.OrderBy(note => note.StartSeconds).ThenBy(note => note.Channel).ToArray(),
            boundaries,
            CountInSeconds: countInTicks * secondsPerTick,
            BarDurationSeconds: tune.BarTicks * secondsPerTick,
            ChorusDurationSeconds: chorusTicks * secondsPerTick,
            DurationSeconds: sessionTicks * secondsPerTick,
            BarsPerChorus: tune.Bars.Count,
            HeadOutChorus: headOutChorus);
    }

    public static int ResolveNextHeadOutChorus(WebSessionPlan plan, double positionSeconds)
    {
        if (positionSeconds < plan.CountInSeconds)
        {
            return 1;
        }
        var musicalSeconds = Math.Max(0, positionSeconds - plan.CountInSeconds);
        var currentChorus = (int)Math.Floor(musicalSeconds / Math.Max(0.001, plan.ChorusDurationSeconds)) + 1;
        return Math.Clamp(currentChorus + 1, 1, MaximumOpenEndedChoruses);
    }

    private static StageSpec ResolveStage(int chorus, bool isHeadOut)
    {
        if (isHeadOut)
        {
            return new StageSpec("HeadOut", RhythmFeel.TwoBeat, true);
        }
        return chorus switch
        {
            1 => new StageSpec("Opening", RhythmFeel.TwoBeat, false),
            2 => new StageSpec("Groove", RhythmFeel.TwoBeat, false),
            3 => new StageSpec("Developing", RhythmFeel.FourBeat, false),
            _ => new StageSpec("Peak", RhythmFeel.FourBeat, false)
        };
    }

    private static void AddCountIn(List<WebScheduledNote> notes, TuneForm tune, double secondsPerTick)
    {
        for (var bar = 0; bar < SessionConstants.CountInBars; bar++)
        {
            for (var beat = 0; beat < tune.BeatsPerBar; beat++)
            {
                var tick = bar * tune.BarTicks + beat * SessionConstants.Ppq;
                var finalBar = bar == SessionConstants.CountInBars - 1;
                var velocity = beat == 0
                    ? finalBar ? (byte)76 : (byte)68
                    : (byte)54;
                notes.Add(new WebScheduledNote(
                    StartSeconds: tick * secondsPerTick,
                    DurationSeconds: 0.08d,
                    NoteNumber: 37,
                    Velocity: velocity,
                    Channel: SessionConstants.DrumsChannel));
            }
        }
    }

    private sealed record StageSpec(string Name, RhythmFeel Feel, bool IsHeadOut);
}

public sealed record WebSessionPlan(
    IReadOnlyList<WebScheduledNote> Notes,
    IReadOnlyList<WebStageBoundary> Stages,
    double CountInSeconds,
    double BarDurationSeconds,
    double ChorusDurationSeconds,
    double DurationSeconds,
    int BarsPerChorus,
    int? HeadOutChorus);

public sealed record WebScheduledNote(
    double StartSeconds,
    double DurationSeconds,
    byte NoteNumber,
    byte Velocity,
    byte Channel);

public sealed record WebStageBoundary(string Name, int Chorus, double StartSeconds, double EndSeconds);

public sealed record WebMixerState(
    bool PianoEnabled,
    bool BassEnabled,
    bool DrumsEnabled,
    bool MidiThruEnabled,
    int PianoVolume,
    int BassVolume,
    int DrumsVolume,
    int VibraphoneVolume);
