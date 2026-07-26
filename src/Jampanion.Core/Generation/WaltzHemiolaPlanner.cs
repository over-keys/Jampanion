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

        // Hemiola must answer a live solo phrase. A form-only probabilistic
        // insertion can contradict the soloist, so keep it disabled until the
        // trigger is driven by phrase and accent detection.
        _ = seed;
        _ = stage;
        _ = performanceGuidance;
        return WaltzHemiolaPlan.None;
    }
}
