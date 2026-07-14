namespace Jampanion.Core.Analysis;

public enum HeadOutDecisionType
{
    None,
    CandidateArmed,
    ConfirmNextChorus,
    ConfirmNow,
    ConfirmedHeadOutCancelled,
    CandidateCancelled,
    CandidateExpired
}

public readonly record struct HeadOutDecision(
    HeadOutDecisionType Type,
    int TargetChorus,
    double ReferenceEnergy,
    double CurrentEnergy,
    string Description)
{
    public static HeadOutDecision None => new(
        HeadOutDecisionType.None,
        0,
        0,
        0,
        string.Empty);
}
