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
Check(
    "ballad drum calibration preserves the internal contour",
    BalladDrumGrooveGenerator.CalibrateOutputVelocity(20) == 32 &&
    BalladDrumGrooveGenerator.CalibrateOutputVelocity(42) == 54);
Check(
    "ballad drum calibration clamps the MIDI maximum",
    BalladDrumGrooveGenerator.CalibrateOutputVelocity(127) == 127);

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
