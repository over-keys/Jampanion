using Jampanion.Core.Generation;
using Jampanion.Core.Music;

var failures = new List<string>();

Check(
    "ordinary chorus remains unchanged",
    Stage3SessionPlanBuilder.ResolveArrangementChorus(4, isHeadOut: false) == 4);
Check(
    "head out uses first-solo energy",
    Stage3SessionPlanBuilder.ResolveArrangementChorus(7, isHeadOut: true) == 2);
Check(
    "swing head out returns to two-feel",
    Stage3SessionPlanBuilder.ResolvePlanningFeel(
        AccompanimentStyle.Swing,
        RhythmFeel.FourBeat,
        isHeadOut: true) == RhythmFeel.TwoBeat);
Check(
    "ordinary swing preserves its requested feel",
    Stage3SessionPlanBuilder.ResolvePlanningFeel(
        AccompanimentStyle.Swing,
        RhythmFeel.FourBeat,
        isHeadOut: false) == RhythmFeel.FourBeat);
var fillThenPlainBar = new[]
{
    new BarArrangement(
        0,
        ResponderRole.Drums,
        PhraseFunction.Setup,
        IsSectionEnding: true,
        Boundary: BoundaryStrength.Section),
    new BarArrangement(
        1,
        ResponderRole.Structural,
        PhraseFunction.Ground,
        IsSectionEnding: false,
        Boundary: BoundaryStrength.None)
};
var fillStateResult = BalladDrumGrooveGenerator.Generate(
    fillThenPlainBar,
    new[] { BalladChorusStage.MovingTwoFeel, BalladChorusStage.MovingTwoFeel },
    previousPatternIndex: -1,
    previousFillVariant: -1,
    previousSectionEndedWithFill: false,
    previousRidePhraseIndex: -1,
    previousCompPatternIndex: -1,
    seed: 1);
Check(
    "ballad fill state describes the final bar rather than any earlier bar",
    !fillStateResult.SectionEndedWithFill);


var bossaTiming = TimeFeelProfile.Resolve(AccompanimentStyle.BossaNova, 120);
var latinTiming = TimeFeelProfile.Resolve(AccompanimentStyle.AfroCubanLatin, 120);
Check(
    "bossa uses tempo-aware straight-eighth placement",
    bossaTiming.SwingOffbeatRatio == 0.5 &&
    bossaTiming.PianoDelayMilliseconds > 0 &&
    bossaTiming.BassLeadMilliseconds > 0);
Check(
    "jazz latin uses tempo-aware straight-eighth placement",
    latinTiming.SwingOffbeatRatio == 0.5 &&
    latinTiming.PianoDelayMilliseconds > 0 &&
    latinTiming.BassLeadMilliseconds > 0);

var neutralFourBars = Enumerable.Range(0, 4)
    .Select(index => new BarArrangement(
        index,
        ResponderRole.Structural,
        PhraseFunction.Ground,
        IsSectionEnding: false,
        Boundary: BoundaryStrength.None))
    .ToArray();

var bossaDrums = BossaDrumGrooveGenerator.Generate(
    neutralFourBars,
    previousPatternIndex: -1,
    previousFillVariant: -1,
    previousSectionEndedWithFill: false,
    previousCompPatternIndex: -1,
    seed: 7,
    stage: BossaChorusStage.FirstSolo);
var cabasaCounts = Enumerable.Range(0, 4)
    .Select(bar => bossaDrums.Notes.Count(note =>
        note.NoteNumber == 69 &&
        note.StartTick >= bar * SessionConstants.BarTicks &&
        note.StartTick < (bar + 1L) * SessionConstants.BarTicks))
    .ToArray();
Check(
    "bossa four-bar phrasing keeps the continuous cabasa pulse",
    cabasaCounts.All(count => count == 8));

var bossaDensities = Enumerable.Range(0, 4)
    .Select(bar => bossaDrums.Notes.Count(note =>
        note.StartTick >= bar * SessionConstants.BarTicks &&
        note.StartTick < (bar + 1L) * SessionConstants.BarTicks))
    .ToArray();
Check(
    "bossa drum density changes gradually",
    bossaDensities.Zip(bossaDensities.Skip(1))
        .All(pair => Math.Abs(pair.First - pair.Second) <= 1));

var latinDrums = JazzLatinDrumGrooveGenerator.Generate(
    neutralFourBars,
    previousPatternIndex: -1,
    previousFillVariant: -1,
    previousSectionEndedWithFill: false,
    previousCompPatternIndex: -1,
    seed: 11,
    stage: LatinChorusStage.Montuno);
var latinRideVoiceCount = latinDrums.Notes.Count(note =>
    note.NoteNumber is 53 or 59);
Check(
    "jazz latin does not double bell and ride on every hit",
    latinRideVoiceCount <= 24);

ExpectArgumentOutOfRange(
    "invalid chorus is rejected",
    () => Stage3SessionPlanBuilder.ResolveArrangementChorus(0, isHeadOut: false));

if (failures.Count == 0)
{
    Console.WriteLine("All Jampanion core regression checks passed.");
    return 0;
}

foreach (var failure in failures)
{
    Console.Error.WriteLine($"FAILED: {failure}");
}

return 1;

void Check(string name, bool condition)
{
    if (!condition)
    {
        failures.Add(name);
    }
}

void ExpectArgumentOutOfRange(string name, Action action)
{
    try
    {
        action();
        failures.Add(name);
    }
    catch (ArgumentOutOfRangeException)
    {
    }
}
