using Jampanion.Core.Generation;
using Jampanion.Core.Music;

namespace Jampanion.Web.Audio;

public static class WebSessionPlanner
{
    private static readonly StageSpec[] FullSessionStages =
    [
        new("Opening", RhythmFeel.TwoBeat, Chorus: 1, IsHeadOut: false),
        new("Groove", RhythmFeel.TwoBeat, Chorus: 2, IsHeadOut: false),
        new("Developing", RhythmFeel.FourBeat, Chorus: 3, IsHeadOut: false),
        new("Peak", RhythmFeel.FourBeat, Chorus: 4, IsHeadOut: false),
        new("HeadOut", RhythmFeel.TwoBeat, Chorus: 5, IsHeadOut: true)
    ];

    public static WebSessionPlan BuildFullSession(TuneForm tune, int tempoBpm, int seed)
    {
        ArgumentNullException.ThrowIfNull(tune);
        tempoBpm = Math.Clamp(tempoBpm, 40, 300);

        var secondsPerTick = 60d / tempoBpm / SessionConstants.Ppq;
        var countInTicks = SessionConstants.CountInBars * tune.BarTicks;
        var notes = new List<WebScheduledNote>();
        AddCountIn(notes, tune, secondsPerTick);

        var context = ArrangementContext.Initial;
        long sessionTicks = countInTicks;
        var boundaries = new List<WebStageBoundary>(FullSessionStages.Length);

        foreach (var stage in FullSessionStages)
        {
            var stageStartTicks = sessionTicks;
            for (var segmentIndex = 0; segmentIndex < tune.SegmentCount; segmentIndex++)
            {
                var segment = Stage3SessionPlanBuilder.BuildSegment(
                    tune,
                    segmentIndex,
                    stage.Feel,
                    stage.Chorus,
                    context,
                    sessionSeed: seed + stage.Chorus * 1009,
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
            }

            boundaries.Add(new WebStageBoundary(
                stage.Name,
                stageStartTicks * secondsPerTick,
                sessionTicks * secondsPerTick));
        }

        return new WebSessionPlan(
            notes.OrderBy(note => note.StartSeconds).ThenBy(note => note.Channel).ToArray(),
            boundaries,
            CountInSeconds: countInTicks * secondsPerTick,
            BarDurationSeconds: tune.BarTicks * secondsPerTick,
            DurationSeconds: sessionTicks * secondsPerTick,
            BarsPerChorus: tune.Bars.Count);
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

    private sealed record StageSpec(string Name, RhythmFeel Feel, int Chorus, bool IsHeadOut);
}

public sealed record WebSessionPlan(
    IReadOnlyList<WebScheduledNote> Notes,
    IReadOnlyList<WebStageBoundary> Stages,
    double CountInSeconds,
    double BarDurationSeconds,
    double DurationSeconds,
    int BarsPerChorus);

public sealed record WebScheduledNote(
    double StartSeconds,
    double DurationSeconds,
    byte NoteNumber,
    byte Velocity,
    byte Channel);

public sealed record WebStageBoundary(string Name, double StartSeconds, double EndSeconds);

public sealed record WebMixerState(
    bool PianoEnabled,
    bool BassEnabled,
    bool DrumsEnabled,
    int PianoVolume,
    int BassVolume,
    int DrumsVolume);
