using Jampanion.Core.Analysis;
using Jampanion.Core.Music;

namespace Jampanion.Core.Generation;

public readonly record struct ArrangementStageDisplay(string Label, int? DevelopmentPercent)
{
    public string Text => DevelopmentPercent is int percent
        ? $"{Label} - {percent}%"
        : Label;
}

public static class ArrangementStageDisplayResolver
{
    public static ArrangementStageDisplay Resolve(
        AccompanimentStyle style,
        int chorus,
        int bar,
        int formBarCount,
        RhythmFeel feel,
        PerformanceGuidance generatedGuidance,
        bool isEndingForm,
        bool headOutActive)
    {
        if (headOutActive)
        {
            return new ArrangementStageDisplay("Head Out / Settling", null);
        }

        if (isEndingForm)
        {
            return new ArrangementStageDisplay("Ending / Preparation", null);
        }

        if (chorus <= 1)
        {
            return new ArrangementStageDisplay(ThemeLabel(style), null);
        }

        var progress = DevelopmentProgress(style, chorus, bar, formBarCount, generatedGuidance);
        var label = style switch
        {
            AccompanimentStyle.Swing => SwingLabel(chorus, feel, generatedGuidance),
            AccompanimentStyle.BossaNova => chorus switch
            {
                2 => "Solo / First Chorus",
                3 => "Solo / Standard Bossa",
                _ => "Solo / Lifted Bossa"
            },
            AccompanimentStyle.AfroCubanLatin => chorus switch
            {
                2 => "Solo / Ponchando",
                3 => "Solo / Montuno",
                _ => "Solo / Mambo"
            },
            AccompanimentStyle.JazzWaltz => chorus switch
            {
                2 => "Solo / Sparse Waltz",
                3 => "Solo / Developing Waltz",
                _ => "Solo / Lifted Waltz"
            },
            AccompanimentStyle.JazzBallad => BalladLabel(chorus, bar, formBarCount),
            _ => "Solo / Building"
        };

        if (generatedGuidance.HighStage && style != AccompanimentStyle.Swing && progress < 100)
        {
            label += " (High)";
        }

        return new ArrangementStageDisplay(label, progress);
    }

    private static string ThemeLabel(AccompanimentStyle style) => style switch
    {
        AccompanimentStyle.BossaNova => "Theme / Open Bossa",
        AccompanimentStyle.AfroCubanLatin => "Theme / Open Latin",
        AccompanimentStyle.JazzWaltz => "Theme / Sparse Waltz",
        AccompanimentStyle.JazzBallad => "Theme / Ballad",
        _ => "Theme / Restrained"
    };

    private static string SwingLabel(int chorus, RhythmFeel feel, PerformanceGuidance guidance)
    {
        if (guidance.HighStage || chorus >= 4)
        {
            return "Solo / Peak Swing";
        }

        return chorus >= 3 || feel == RhythmFeel.FourBeat
            ? "Solo / Building 4-Feel"
            : "Solo / Calm 2-Feel";
    }

    private static string BalladLabel(int chorus, int bar, int formBarCount)
    {
        var secondHalf = bar > Math.Max(1, formBarCount / 2);
        return chorus switch
        {
            2 when !secondHalf => "Solo / Quiet Ballad",
            2 => "Solo / Moving 2-Feel",
            _ => "Solo / 4-Feel Ballad"
        };
    }

    private static int DevelopmentProgress(
        AccompanimentStyle style,
        int chorus,
        int bar,
        int formBarCount,
        PerformanceGuidance guidance)
    {
        if (guidance.HighStage || chorus >= 4)
        {
            return 100;
        }

        if (style != AccompanimentStyle.Swing)
        {
            return (int)Math.Round(Math.Clamp((guidance.Energy - 0.36) / 0.44, 0, 1) * 100);
        }

        var formProgress = Math.Clamp((bar - 1) / (double)Math.Max(1, formBarCount), 0, 1);
        var development = chorus - 2 + formProgress;
        return (int)Math.Round(Math.Clamp(development / 2.0, 0, 1) * 100);
    }
}
