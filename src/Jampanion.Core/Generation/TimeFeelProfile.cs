using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal enum TimeFeelRole
{
    Bass,
    Piano,
    Ride,
    HiHat,
    Kick,
    DrumComp
}

/// <summary>
/// Converts the notation-like rhythm grids used by the generators into a
/// tempo-aware performance grid. Placement offsets are expressed in real time
/// so that changing tempo does not accidentally change the ensemble hierarchy.
/// </summary>
internal sealed record TimeFeelProfile(
    AccompanimentStyle Style,
    int TempoBpm,
    double SwingOffbeatRatio,
    double BassLeadMilliseconds,
    double PianoDelayMilliseconds,
    double RideDelayMilliseconds,
    double HiHatDelayMilliseconds,
    double KickDelayMilliseconds,
    double DrumCompDelayMilliseconds,
    double BassGateScale,
    double PianoGateScale,
    double WaltzBeatTwoDelayMilliseconds,
    double WaltzBeatThreeLeadMilliseconds)
{
    // Swing-based styles may approach even eighths as tempo rises, but they
    // never become fully straight. This floor protects that musical identity
    // when the tempo curves are tuned again later.
    private const double MinimumSwungOffbeatRatio = 0.54;

    public static TimeFeelProfile Resolve(AccompanimentStyle style, int tempoBpm)
    {
        var tempo = Math.Clamp(tempoBpm, 40, 300);
        return style switch
        {
            AccompanimentStyle.Swing => new TimeFeelProfile(
                style,
                tempo,
                Interpolate(tempo, (40, 0.695), (90, 0.680), (120, 2.0 / 3.0),
                    (160, 0.635), (200, 0.605), (240, 0.580), (300, 0.560)),
                BassLeadMilliseconds: Interpolate(tempo, (40, 7.0), (120, 8.5), (220, 7.0), (300, 5.5)),
                PianoDelayMilliseconds: Interpolate(tempo, (40, 19.0), (100, 16.0), (180, 12.0), (300, 8.0)),
                RideDelayMilliseconds: 2.0,
                HiHatDelayMilliseconds: 3.0,
                KickDelayMilliseconds: 0.8,
                DrumCompDelayMilliseconds: Interpolate(tempo, (40, 7.0), (140, 5.0), (300, 3.0)),
                BassGateScale: Interpolate(tempo, (40, 1.03), (120, 0.98), (220, 0.90), (300, 0.86)),
                PianoGateScale: Interpolate(tempo, (40, 1.08), (120, 1.00), (220, 0.91), (300, 0.86)),
                WaltzBeatTwoDelayMilliseconds: 0,
                WaltzBeatThreeLeadMilliseconds: 0),

            AccompanimentStyle.JazzWaltz => new TimeFeelProfile(
                style,
                tempo,
                Interpolate(tempo, (40, 0.690), (80, 0.675), (120, 0.645),
                    (160, 0.615), (210, 0.580), (260, 0.555), (300, 0.545)),
                BassLeadMilliseconds: Interpolate(tempo, (40, 6.0), (120, 5.0), (220, 3.5), (300, 2.5)),
                PianoDelayMilliseconds: Interpolate(tempo, (40, 15.0), (120, 12.0), (220, 8.0), (300, 6.0)),
                RideDelayMilliseconds: 1.5,
                HiHatDelayMilliseconds: 3.0,
                KickDelayMilliseconds: 0,
                DrumCompDelayMilliseconds: Interpolate(tempo, (40, 7.0), (160, 5.0), (300, 3.0)),
                BassGateScale: Interpolate(tempo, (40, 1.04), (120, 0.99), (220, 0.92), (300, 0.88)),
                PianoGateScale: Interpolate(tempo, (40, 1.07), (120, 1.00), (220, 0.92), (300, 0.87)),
                // Keep the three-beat lilt independent from subdivision swing.
                // It fades at fast tempos where a larger displacement would
                // sound like an uneven meter rather than a waltz pulse.
                WaltzBeatTwoDelayMilliseconds: Interpolate(tempo, (40, 5.0), (100, 4.0), (180, 2.5), (300, 1.0)),
                WaltzBeatThreeLeadMilliseconds: Interpolate(tempo, (40, 3.5), (100, 3.0), (180, 1.8), (300, 0.8))),

            AccompanimentStyle.JazzBallad => new TimeFeelProfile(
                style,
                tempo,
                // Ballad is deliberately its own slow-tempo curve rather than
                // the low end of the ordinary swing curve.
                Interpolate(tempo, (40, 0.685), (55, 0.675), (70, 0.665),
                    (90, 0.645), (120, 0.620), (180, 0.590), (300, 0.560)),
                BassLeadMilliseconds: Interpolate(tempo, (40, 4.0), (80, 3.0), (120, 2.0), (300, 1.0)),
                PianoDelayMilliseconds: Interpolate(tempo, (40, 24.0), (65, 21.0), (90, 18.0), (120, 15.0), (300, 9.0)),
                RideDelayMilliseconds: 2.5,
                HiHatDelayMilliseconds: 5.0,
                KickDelayMilliseconds: 0,
                DrumCompDelayMilliseconds: Interpolate(tempo, (40, 9.0), (80, 7.0), (120, 5.0), (300, 3.0)),
                BassGateScale: Interpolate(tempo, (40, 1.08), (70, 1.05), (100, 1.01), (300, 0.90)),
                PianoGateScale: Interpolate(tempo, (40, 1.12), (70, 1.08), (100, 1.03), (300, 0.92)),
                WaltzBeatTwoDelayMilliseconds: 0,
                WaltzBeatThreeLeadMilliseconds: 0),

            _ => Straight(style, tempo)
        };
    }

    public long Place(long gridTick, TimeFeelRole role)
    {
        var mapped = MapGrid(gridTick);
        var milliseconds = role switch
        {
            TimeFeelRole.Bass => -BassLeadMilliseconds,
            TimeFeelRole.Piano => PianoDelayMilliseconds,
            TimeFeelRole.Ride => RideDelayMilliseconds,
            TimeFeelRole.HiHat => HiHatDelayMilliseconds,
            TimeFeelRole.Kick => KickDelayMilliseconds,
            _ => DrumCompDelayMilliseconds
        };

        return Math.Max(0, mapped + MillisecondsToTicks(milliseconds));
    }

    public long MapGrid(long gridTick)
    {
        if (gridTick <= 0 || Style is AccompanimentStyle.BossaNova or AccompanimentStyle.AfroCubanLatin)
        {
            return Math.Max(0, gridTick);
        }

        var beatStart = gridTick / SessionConstants.Ppq * SessionConstants.Ppq;
        var position = gridTick - beatStart;
        var performedOffbeatRatio = Style is AccompanimentStyle.Swing or
            AccompanimentStyle.JazzWaltz or
            AccompanimentStyle.JazzBallad
                ? Math.Max(MinimumSwungOffbeatRatio, SwingOffbeatRatio)
                : SwingOffbeatRatio;
        var offbeat = SessionConstants.Ppq * performedOffbeatRatio;
        double mappedPosition;

        // Existing rhythm cells use a triplet notation grid (0, 160, 320, 480).
        // Piecewise mapping keeps the vocabulary intact while allowing the late
        // eighth to approach straight time as tempo rises.
        if (position <= SessionConstants.Ppq / 3.0)
        {
            mappedPosition = position * (offbeat / 2.0) / (SessionConstants.Ppq / 3.0);
        }
        else if (position <= SessionConstants.Ppq * 2.0 / 3.0)
        {
            mappedPosition = offbeat / 2.0 +
                (position - SessionConstants.Ppq / 3.0) *
                (offbeat / 2.0) / (SessionConstants.Ppq / 3.0);
        }
        else
        {
            mappedPosition = offbeat +
                (position - SessionConstants.Ppq * 2.0 / 3.0) *
                (SessionConstants.Ppq - offbeat) / (SessionConstants.Ppq / 3.0);
        }

        var result = beatStart + (long)Math.Round(mappedPosition);
        if (Style == AccompanimentStyle.JazzWaltz)
        {
            var beatInBar = (gridTick / SessionConstants.Ppq) % 3;
            result += beatInBar switch
            {
                1 => MillisecondsToTicks(WaltzBeatTwoDelayMilliseconds),
                2 => MillisecondsToTicks(-WaltzBeatThreeLeadMilliseconds),
                _ => 0
            };
        }

        return Math.Max(0, result);
    }

    public long ScaleGate(long duration, TimeFeelRole role)
    {
        var scale = role == TimeFeelRole.Bass ? BassGateScale : PianoGateScale;
        return Math.Max(1, (long)Math.Round(duration * scale));
    }

    public long MillisecondsToTicks(double milliseconds) =>
        (long)Math.Round(milliseconds * TempoBpm * SessionConstants.Ppq / 60_000.0);

    private static TimeFeelProfile Straight(AccompanimentStyle style, int tempo) => new(
        style,
        tempo,
        SwingOffbeatRatio: 0.5,
        BassLeadMilliseconds: 0,
        PianoDelayMilliseconds: 0,
        RideDelayMilliseconds: 0,
        HiHatDelayMilliseconds: 0,
        KickDelayMilliseconds: 0,
        DrumCompDelayMilliseconds: 0,
        BassGateScale: 1,
        PianoGateScale: 1,
        WaltzBeatTwoDelayMilliseconds: 0,
        WaltzBeatThreeLeadMilliseconds: 0);

    private static double Interpolate(int tempo, params (int Tempo, double Value)[] points)
    {
        if (tempo <= points[0].Tempo) return points[0].Value;
        for (var index = 1; index < points.Length; index++)
        {
            if (tempo > points[index].Tempo) continue;
            var left = points[index - 1];
            var right = points[index];
            var amount = (double)(tempo - left.Tempo) / (right.Tempo - left.Tempo);
            return left.Value + (right.Value - left.Value) * amount;
        }

        return points[^1].Value;
    }
}
