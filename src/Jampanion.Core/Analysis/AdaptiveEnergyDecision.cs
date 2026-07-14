namespace Jampanion.Core.Analysis;

public enum AdaptiveEnergyDecisionType
{
    None,
    ThemeBaselineEstablished,
    FourFeelRequestArmed,
    FourFeelRequestCancelled,
    FourFeelActivated,
    HighFourFeelRequestArmed,
    HighFourFeelRequestCancelled,
    HighFourFeelActivated
}

public readonly record struct AdaptiveEnergyDecision(
    AdaptiveEnergyDecisionType Type,
    int TargetChorus,
    int TargetBar,
    double ThemeBaseline,
    double CurrentEnergy,
    string Description)
{
    public static AdaptiveEnergyDecision None => new(
        AdaptiveEnergyDecisionType.None,
        0,
        0,
        0,
        0,
        string.Empty);
}
