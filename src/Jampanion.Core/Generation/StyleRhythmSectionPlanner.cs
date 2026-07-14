using Jampanion.Core.Music;

namespace Jampanion.Core.Generation;

/// <summary>
/// Gives each accompaniment style its own four-bar conversational shape before
/// individual instruments render notes. This prevents a swing-style space or a
/// generic drum answer from being applied to an ostinato-based Brazilian or
/// Afro-Cuban texture.
/// </summary>
internal static class StyleRhythmSectionPlanner
{
    public static IReadOnlyList<BarArrangement> Plan(
        AccompanimentStyle style,
        IReadOnlyList<BarArrangement> source,
        int chorus,
        bool isEndingForm)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count == 0 || style == AccompanimentStyle.Swing)
        {
            return source;
        }

        var sentence = GetSentence(style, chorus, isEndingForm);
        return source.Select((bar, index) =>
        {
            var slot = sentence[Math.Min(index, sentence.Count - 1)];
            var role = slot.Role;
            var function = slot.Function;

            // Form boundaries retain their authority without making every style
            // use a generic fill. The dedicated drum generator decides how a
            // style actually articulates this setup.
            if (bar.IsSectionEnding && bar.Boundary >= BoundaryStrength.Section &&
                function is PhraseFunction.Ground or PhraseFunction.Release)
            {
                role = ResponderRole.Drums;
                function = PhraseFunction.Setup;
            }

            return bar with { Responder = role, Function = function };
        }).ToArray();
    }

    private static IReadOnlyList<PlanSlot> GetSentence(
        AccompanimentStyle style,
        int chorus,
        bool isEndingForm) => style switch
    {
        AccompanimentStyle.BossaNova => GetBossaSentence(chorus, isEndingForm),
        AccompanimentStyle.AfroCubanLatin => GetLatinSentence(chorus, isEndingForm),
        AccompanimentStyle.JazzWaltz => GetWaltzSentence(chorus, isEndingForm),
        AccompanimentStyle.JazzBallad => GetBalladSentence(chorus, isEndingForm),
        _ => SwingSentence
    };

    // Swing retains the richer responsive planner. The other styles use these
    // family-specific sentences as an underlying, repeatable ensemble contract.
    private static readonly PlanSlot[] SwingSentence =
    [
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Piano, PhraseFunction.Comment),
        P(ResponderRole.Drums, PhraseFunction.Comment),
        P(ResponderRole.Structural, PhraseFunction.Release)
    ];

    private static readonly PlanSlot[] BossaHead =
    [
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Piano, PhraseFunction.Comment),
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Structural, PhraseFunction.Release)
    ];

    private static readonly PlanSlot[] BossaSolo =
    [
        P(ResponderRole.Piano, PhraseFunction.Comment),
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Piano, PhraseFunction.Answer),
        P(ResponderRole.Drums, PhraseFunction.Setup)
    ];

    private static readonly PlanSlot[] BossaLift =
    [
        P(ResponderRole.Piano, PhraseFunction.Build),
        P(ResponderRole.Drums, PhraseFunction.Comment),
        P(ResponderRole.Piano, PhraseFunction.Build),
        P(ResponderRole.Drums, PhraseFunction.Setup)
    ];

    private static readonly PlanSlot[] LatinHead =
    [
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Piano, PhraseFunction.Comment),
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Structural, PhraseFunction.Release)
    ];

    private static readonly PlanSlot[] LatinMontuno =
    [
        P(ResponderRole.Piano, PhraseFunction.Comment),
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Piano, PhraseFunction.Answer),
        P(ResponderRole.Drums, PhraseFunction.Setup)
    ];

    private static readonly PlanSlot[] LatinMambo =
    [
        P(ResponderRole.Piano, PhraseFunction.Build),
        P(ResponderRole.Drums, PhraseFunction.Comment),
        P(ResponderRole.Piano, PhraseFunction.Build),
        P(ResponderRole.Drums, PhraseFunction.Setup)
    ];

    private static readonly PlanSlot[] WaltzHead =
    [
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Piano, PhraseFunction.Comment),
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Structural, PhraseFunction.Release)
    ];

    private static readonly PlanSlot[] WaltzSolo =
    [
        P(ResponderRole.Piano, PhraseFunction.Comment),
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Drums, PhraseFunction.Comment),
        P(ResponderRole.Piano, PhraseFunction.Release)
    ];

    private static readonly PlanSlot[] WaltzLift =
    [
        P(ResponderRole.Piano, PhraseFunction.Build),
        P(ResponderRole.Drums, PhraseFunction.Comment),
        P(ResponderRole.Piano, PhraseFunction.Build),
        P(ResponderRole.Drums, PhraseFunction.Setup)
    ];

    private static readonly PlanSlot[] BalladTheme =
    [
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Piano, PhraseFunction.Comment),
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Structural, PhraseFunction.Release)
    ];

    private static readonly PlanSlot[] BalladQuietSolo =
    [
        P(ResponderRole.Piano, PhraseFunction.Comment),
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Piano, PhraseFunction.Answer),
        P(ResponderRole.Structural, PhraseFunction.Release)
    ];

    private static readonly PlanSlot[] BalladMoving =
    [
        P(ResponderRole.Structural, PhraseFunction.Ground),
        P(ResponderRole.Piano, PhraseFunction.Comment),
        P(ResponderRole.Drums, PhraseFunction.Comment),
        P(ResponderRole.Drums, PhraseFunction.Setup)
    ];

    private static readonly PlanSlot[] BalladLift =
    [
        P(ResponderRole.Piano, PhraseFunction.Build),
        P(ResponderRole.Drums, PhraseFunction.Comment),
        P(ResponderRole.Piano, PhraseFunction.Build),
        P(ResponderRole.Drums, PhraseFunction.Setup)
    ];

    private static IReadOnlyList<PlanSlot> GetBossaSentence(int chorus, bool ending) =>
        ending || chorus == 1 ? BossaHead : chorus < 4 ? BossaSolo : BossaLift;

    private static IReadOnlyList<PlanSlot> GetLatinSentence(int chorus, bool ending) =>
        ending || chorus == 1 ? LatinHead : chorus < 4 ? LatinMontuno : LatinMambo;

    private static IReadOnlyList<PlanSlot> GetWaltzSentence(int chorus, bool ending) =>
        ending || chorus == 1 ? WaltzHead : chorus < 4 ? WaltzSolo : WaltzLift;

    private static IReadOnlyList<PlanSlot> GetBalladSentence(int chorus, bool ending) =>
        ending || chorus == 1 ? BalladTheme : chorus == 2 ? BalladQuietSolo : chorus == 3 ? BalladMoving : BalladLift;

    private static PlanSlot P(ResponderRole role, PhraseFunction function) => new(role, function);

    private readonly record struct PlanSlot(ResponderRole Role, PhraseFunction Function);
}
