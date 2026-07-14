using Jampanion.Core.Analysis;
using Jampanion.Core.Music;

namespace Jampanion.Core.Generation;

public static class Stage3ArrangementPlanner
{
    // Two-beat is not a quieter copy of four-beat.  The bass and light ride/brush
    // current already articulate the large pulse, so the piano is assigned fewer,
    // longer statements and more genuine space.  A build increases authority and
    // harmonic colour without turning the piano into a four-beat chatter pattern.
    private static readonly PlanSlot[][] TwoBeatNeutralTemplates =
    [
        [S(ResponderRole.Structural, PhraseFunction.Ground), S(ResponderRole.Piano, PhraseFunction.Comment), S(ResponderRole.Structural, PhraseFunction.Space), S(ResponderRole.Structural, PhraseFunction.Release)],
        [S(ResponderRole.Piano, PhraseFunction.Comment), S(ResponderRole.Structural, PhraseFunction.Space), S(ResponderRole.Structural, PhraseFunction.Ground), S(ResponderRole.Drums, PhraseFunction.Setup)],
        [S(ResponderRole.Structural, PhraseFunction.Ground), S(ResponderRole.Structural, PhraseFunction.Space), S(ResponderRole.Piano, PhraseFunction.Comment), S(ResponderRole.Piano, PhraseFunction.Comment)],
        [S(ResponderRole.Structural, PhraseFunction.Space), S(ResponderRole.Piano, PhraseFunction.Comment), S(ResponderRole.Structural, PhraseFunction.Ground), S(ResponderRole.Structural, PhraseFunction.Release)]
    ];

    private static readonly PlanSlot[][] TwoBeatHighTemplates =
    [
        [S(ResponderRole.Piano, PhraseFunction.Comment), S(ResponderRole.Structural, PhraseFunction.Ground), S(ResponderRole.Piano, PhraseFunction.Build), S(ResponderRole.Structural, PhraseFunction.Release)],
        [S(ResponderRole.Structural, PhraseFunction.Ground), S(ResponderRole.Piano, PhraseFunction.Comment), S(ResponderRole.Drums, PhraseFunction.Build), S(ResponderRole.Piano, PhraseFunction.Release)],
        [S(ResponderRole.Piano, PhraseFunction.Build), S(ResponderRole.Structural, PhraseFunction.Space), S(ResponderRole.Piano, PhraseFunction.Release), S(ResponderRole.Drums, PhraseFunction.Setup)]
    ];

    // In four-beat the walking bass and ride provide a continuous reference.  The
    // piano can therefore use shorter syncopated punctuation and exchange foreground
    // responsibility with the drummer, while still leaving deliberate holes. Ordinary
    // four-bar phrase boundaries vary between release, piano comment and a restrained
    // drum setup; only section and chorus boundaries force the drummer to mark form.
    private static readonly PlanSlot[][] FourBeatNeutralTemplates =
    [
        [S(ResponderRole.Structural, PhraseFunction.Ground), S(ResponderRole.Piano, PhraseFunction.Comment), S(ResponderRole.Drums, PhraseFunction.Comment), S(ResponderRole.Structural, PhraseFunction.Release)],
        [S(ResponderRole.Piano, PhraseFunction.Comment), S(ResponderRole.Structural, PhraseFunction.Space), S(ResponderRole.Drums, PhraseFunction.Comment), S(ResponderRole.Drums, PhraseFunction.Setup)],
        [S(ResponderRole.Structural, PhraseFunction.Ground), S(ResponderRole.Drums, PhraseFunction.Comment), S(ResponderRole.Piano, PhraseFunction.Comment), S(ResponderRole.Piano, PhraseFunction.Comment)],
        [S(ResponderRole.Piano, PhraseFunction.Comment), S(ResponderRole.Drums, PhraseFunction.Comment), S(ResponderRole.Structural, PhraseFunction.Space), S(ResponderRole.Structural, PhraseFunction.Release)]
    ];

    private static readonly PlanSlot[][] FourBeatHighTemplates =
    [
        [S(ResponderRole.Piano, PhraseFunction.Comment), S(ResponderRole.Drums, PhraseFunction.Build), S(ResponderRole.Piano, PhraseFunction.Build), S(ResponderRole.Piano, PhraseFunction.Release)],
        [S(ResponderRole.Drums, PhraseFunction.Comment), S(ResponderRole.Piano, PhraseFunction.Build), S(ResponderRole.Piano, PhraseFunction.Comment), S(ResponderRole.Piano, PhraseFunction.Release)],
        [S(ResponderRole.Piano, PhraseFunction.Build), S(ResponderRole.Drums, PhraseFunction.Comment), S(ResponderRole.Piano, PhraseFunction.Release), S(ResponderRole.Drums, PhraseFunction.Setup)]
    ];

    public static IReadOnlyList<BarArrangement> Plan(
        int seed,
        RhythmFeel feel,
        int barCount = SessionConstants.BarsPerSegment,
        PerformanceGuidance? performanceGuidance = null,
        BoundaryStrength endingBoundary = BoundaryStrength.Phrase)
    {
        if (barCount is < 1 or > SessionConstants.BarsPerSegment)
        {
            throw new ArgumentOutOfRangeException(nameof(barCount));
        }

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var slots = SelectTemplate(seed, feel, guidance).Take(barCount).ToArray();
        var result = new BarArrangement[barCount];

        for (var localBar = 0; localBar < result.Length; localBar++)
        {
            var isEnding = localBar == result.Length - 1;
            var slot = slots[localBar];
            result[localBar] = !isEnding
                ? new BarArrangement(localBar, slot.Responder, slot.Function, IsSectionEnding: false, Boundary: BoundaryStrength.None)
                : endingBoundary >= BoundaryStrength.Section
                    ? new BarArrangement(localBar, ResponderRole.Drums, PhraseFunction.Setup, IsSectionEnding: true, Boundary: endingBoundary)
                    : new BarArrangement(localBar, slot.Responder, slot.Function, IsSectionEnding: true, Boundary: endingBoundary);
        }

        return result;
    }

    private static IReadOnlyList<PlanSlot> SelectTemplate(
        int seed,
        RhythmFeel feel,
        PerformanceGuidance guidance)
    {
        if (guidance.IsHighStageActive)
        {
            var templates = feel == RhythmFeel.TwoBeat ? TwoBeatHighTemplates : FourBeatHighTemplates;
            var index = (int)(DeterministicNoise.Unit(seed, 901) * templates.Length) % templates.Length;
            return templates[index];
        }

        var neutral = feel == RhythmFeel.TwoBeat ? TwoBeatNeutralTemplates : FourBeatNeutralTemplates;
        var templateIndex = (int)(DeterministicNoise.Unit(seed, 903) * neutral.Length) % neutral.Length;
        return neutral[templateIndex];
    }

    private static PlanSlot S(ResponderRole responder, PhraseFunction function) => new(responder, function);

    private sealed record PlanSlot(ResponderRole Responder, PhraseFunction Function);
}
