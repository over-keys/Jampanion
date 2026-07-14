namespace Jampanion.Core.Analysis;

public readonly record struct PerformanceGuidance(
    bool HasRecentInput,
    PerformanceIntensity Intensity,
    double Energy,
    double ShortEnergy,
    double Density,
    double AverageVelocity,
    double Motion,
    double PhraseActivity,
    bool PhraseEndedRecently,
    bool HighEnergySustained,
    double HighEnergyBars,
    double AveragePitch = 64.0,
    bool HighStage = false,
    long LastAttackMilliseconds = long.MinValue)
{
    public static PerformanceGuidance Neutral => new(
        HasRecentInput: false,
        Intensity: PerformanceIntensity.None,
        Energy: 0,
        ShortEnergy: 0,
        Density: 0,
        AverageVelocity: 0,
        Motion: 0,
        PhraseActivity: 0,
        PhraseEndedRecently: false,
        HighEnergySustained: false,
        HighEnergyBars: 0,
        AveragePitch: 64.0,
        HighStage: false,
        LastAttackMilliseconds: long.MinValue);

    public bool IsPhraseActive => HasRecentInput && PhraseActivity >= 0.35 && !PhraseEndedRecently;

    // HighStage is a fixed arrangement stage. Raw MIDI energy must not promote
    // accompaniment, because MIDI input is used only by theme-return analysis.
    public bool IsHighStageActive => HighStage;

    public int PlanningKey
    {
        get
        {
            if (!HasRecentInput && !HighStage)
            {
                return 0;
            }

            var phrase = PhraseEndedRecently ? 1 : IsPhraseActive ? 2 : 0;
            var shortBand = ShortEnergy switch
            {
                >= 0.68 => 2,
                >= 0.40 => 1,
                _ => 0
            };
            var registerBand = AveragePitch switch
            {
                >= 72 => 2,
                >= 60 => 1,
                _ => 0
            };
            var stageBand = HighStage ? 10_000 : 0;
            return stageBand + 100 + (int)Intensity * 27 + shortBand * 9 + registerBand * 3 + phrase;
        }
    }

    public string Description => HighStage
        ? "High-stage four-feel"
        : !HasRecentInput
        ? "Waiting for solo input"
        : PhraseEndedRecently
            ? $"Phrase ended · {Intensity.ToString().ToLowerInvariant()} energy"
            : $"{Intensity.ToString().ToLowerInvariant()} energy · {(IsPhraseActive ? "active phrase" : "space")}";
}
