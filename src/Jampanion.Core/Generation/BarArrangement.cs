namespace Jampanion.Core.Generation;

public sealed record BarArrangement(
    int LocalBarIndex,
    ResponderRole Responder,
    PhraseFunction Function,
    bool IsSectionEnding,
    BoundaryStrength Boundary = BoundaryStrength.None,
    bool IsTransitionLeadIn = false)
{
    public int DynamicLift => (Function switch
        {
            PhraseFunction.Space => -4,
            PhraseFunction.Release => -2,
            PhraseFunction.Ground => 0,
            PhraseFunction.Comment => 1,
            PhraseFunction.Answer => 2,
            PhraseFunction.Build => 4,
            PhraseFunction.Setup => 3,
            _ => 0
        }) + (IsTransitionLeadIn ? 1 : 0);

    public bool InvitesPianoStatement => Responder == ResponderRole.Piano &&
        Function is PhraseFunction.Comment or PhraseFunction.Answer or PhraseFunction.Build;

    public bool InvitesDrumStatement => Responder == ResponderRole.Drums &&
        Function is PhraseFunction.Comment or PhraseFunction.Build or PhraseFunction.Setup;
}
