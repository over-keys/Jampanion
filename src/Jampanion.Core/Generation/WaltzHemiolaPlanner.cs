using Jampanion.Core.Analysis;
using Jampanion.Core.Music;

namespace Jampanion.Core.Generation;

internal readonly record struct WaltzHemiolaPlan(int PairStartBar)
{
    public static WaltzHemiolaPlan None => new(-1);

    public bool IsActive => PairStartBar >= 0;

    public bool IsFirstBar(int barIndex) => barIndex == PairStartBar;

    public bool IsSecondBar(int barIndex) => barIndex == PairStartBar + 1;

    public bool ContainsBar(int barIndex) => IsFirstBar(barIndex) || IsSecondBar(barIndex);

    public bool IsAnchor(int barIndex, long offset) =>
        IsFirstBar(barIndex) ? offset is 0 or 960 :
        IsSecondBar(barIndex) && offset == 480;
}

internal static class WaltzHemiolaPlanner
{
    public static WaltzHemiolaPlan Plan(
        IReadOnlyList<TuneBar> bars,
        IReadOnlyList<BarArrangement> arrangements,
        int seed,
        WaltzChorusStage stage,
        PerformanceGuidance? performanceGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(arrangements);
        if (bars.Count != arrangements.Count) throw new ArgumentException("Bars and arrangements must have the same length.");

        if (stage != WaltzChorusStage.Lifted || bars.Count < 2)
        {
            return WaltzHemiolaPlan.None;
        }

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var probability = guidance.HighStage ? 0.44 : 0.17;
        if (DeterministicNoise.Unit(seed, 3401) >= probability)
        {
            return WaltzHemiolaPlan.None;
        }

        var candidates = Enumerable.Range(0, bars.Count - 1)
            .Where(start => start % 2 == 0)
            .Where(start => bars[start].ChordChanges.Count == 1 && bars[start + 1].ChordChanges.Count == 1)
            .Where(start => arrangements[start].Function != PhraseFunction.Space &&
                arrangements[start + 1].Function != PhraseFunction.Space)
            .Select(start => new
            {
                Start = start,
                Score = PairScore(arrangements[start], arrangements[start + 1]) +
                    DeterministicNoise.Unit(seed, start, 3403)
            })
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();

        return candidates.Length == 0
            ? WaltzHemiolaPlan.None
            : new WaltzHemiolaPlan(candidates[0].Start);
    }

    private static double PairScore(BarArrangement first, BarArrangement second)
    {
        var score = FunctionScore(first.Function) + FunctionScore(second.Function);
        if (second.IsSectionEnding) score += second.Boundary >= BoundaryStrength.Section ? 4.0 : 2.0;
        if (second.Function == PhraseFunction.Setup) score += 2.5;
        if (first.Function == PhraseFunction.Build) score += 1.5;
        return score;
    }

    private static double FunctionScore(PhraseFunction function) => function switch
    {
        PhraseFunction.Build => 4.0,
        PhraseFunction.Setup => 3.5,
        PhraseFunction.Answer => 2.5,
        PhraseFunction.Comment => 1.5,
        PhraseFunction.Ground => 0.5,
        PhraseFunction.Release => 0.0,
        _ => -4.0
    };
}
