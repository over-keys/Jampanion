using Jampanion.Core.Analysis;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

public sealed class Stage3SegmentPlan
{
    public Stage3SegmentPlan(
        SegmentPlan segment,
        ArrangementContext outputContext,
        IReadOnlyList<BarArrangement> barArrangements,
        IReadOnlyList<int> pianoCellIndices,
        IReadOnlyList<int> drumPatternIndices,
        PerformanceGuidance arrangementGuidance)
    {
        Segment = segment;
        OutputContext = outputContext;
        BarArrangements = barArrangements;
        PianoCellIndices = pianoCellIndices;
        DrumPatternIndices = drumPatternIndices;
        ArrangementGuidance = arrangementGuidance;
    }

    public SegmentPlan Segment { get; }
    public ArrangementContext OutputContext { get; }
    public IReadOnlyList<BarArrangement> BarArrangements { get; }
    public IReadOnlyList<int> PianoCellIndices { get; }
    public IReadOnlyList<int> DrumPatternIndices { get; }
    public PerformanceGuidance ArrangementGuidance { get; }
}
