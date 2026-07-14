using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Analysis;

public sealed class AdaptiveEnergyTracker
{
    private const int RecentBarLimit = 16;

    private readonly Queue<double> _themeBars = new();
    private readonly Queue<double> _recentBars = new();

    private int _fourFeelSensitivity = 50;
    private int _highFourFeelSensitivity = 50;
    private int _currentChorus;
    private int _currentBar;
    private int _chorusBars = SessionConstants.ChorusBars;
    private long _barStartMilliseconds;
    private double _barEnergyTotal;
    private int _barSamples;
    private bool _barHadInput;
    private bool _fourFeelRequestArmed;
    private bool _highFourFeelRequestArmed;
    private double _pendingFourFeelLevel;
    private int _fourFeelStartAbsoluteBar;

    public AdaptiveEnergyState State { get; private set; } = AdaptiveEnergyState.ThemeLearning;
    public double ThemeBaseline { get; private set; }
    public bool ThemeBaselineAvailable { get; private set; }
    public double FourFeelEstablishedLevel { get; private set; }
    public bool FourFeelRequestArmed => _fourFeelRequestArmed;
    public bool HighFourFeelRequestArmed => _highFourFeelRequestArmed;

    public void Configure(int fourFeelSensitivity, int highFourFeelSensitivity)
    {
        _fourFeelSensitivity = Math.Clamp(fourFeelSensitivity, 0, 100);
        _highFourFeelSensitivity = Math.Clamp(highFourFeelSensitivity, 0, 100);
    }

    public void CancelPendingRequests()
    {
        _fourFeelRequestArmed = false;
        _highFourFeelRequestArmed = false;
        _pendingFourFeelLevel = 0;
    }

    public void Reset()
    {
        _themeBars.Clear();
        _recentBars.Clear();
        _currentChorus = 0;
        _currentBar = 0;
        _chorusBars = SessionConstants.ChorusBars;
        _barStartMilliseconds = 0;
        _barEnergyTotal = 0;
        _barSamples = 0;
        _barHadInput = false;
        _fourFeelRequestArmed = false;
        _highFourFeelRequestArmed = false;
        _pendingFourFeelLevel = 0;
        _fourFeelStartAbsoluteBar = 0;
        State = AdaptiveEnergyState.ThemeLearning;
        ThemeBaseline = 0;
        ThemeBaselineAvailable = false;
        FourFeelEstablishedLevel = 0;
    }

    public IReadOnlyList<AdaptiveEnergyDecision> Update(
        long nowMilliseconds,
        int tempoBpm,
        int chorus,
        int bar,
        int chorusBars,
        RhythmFeel currentFeel,
        bool highFourFeelActive,
        PerformanceGuidance guidance)
    {
        if (tempoBpm is < 40 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(tempoBpm));
        }

        if (chorus < 1 || chorusBars < 2 || chorusBars % 2 != 0 || bar is < 1 || bar > chorusBars)
        {
            return Array.Empty<AdaptiveEnergyDecision>();
        }

        var decisions = new List<AdaptiveEnergyDecision>(2);
        if (_currentChorus == 0)
        {
            _chorusBars = chorusBars;
            BeginBar(nowMilliseconds, chorus, bar);
        }
        else if (_currentChorus != chorus || _currentBar != bar)
        {
            FinalizeBar();
            var crossedFirstChorus = _currentChorus == 1 && chorus > 1;
            _chorusBars = chorusBars;
            BeginBar(nowMilliseconds, chorus, bar);

            if (crossedFirstChorus && EstablishThemeBaseline())
            {
                decisions.Add(new AdaptiveEnergyDecision(
                    AdaptiveEnergyDecisionType.ThemeBaselineEstablished,
                    chorus,
                    bar,
                    ThemeBaseline,
                    ThemeBaseline,
                    $"First-theme baseline established at {ThemeBaseline:0.00}."));
            }
        }

        SynchronizeState(chorus, bar, currentFeel, highFourFeelActive, decisions);

        if (guidance.HasRecentInput)
        {
            _barEnergyTotal += ObservedEnergy(guidance);
            _barSamples++;
            _barHadInput = true;
        }

        if (chorus == 1 || !ThemeBaselineAvailable)
        {
            return decisions;
        }

        var barProgress = GetBarProgress(nowMilliseconds, tempoBpm);
        if (State == AdaptiveEnergyState.SoloTwoFeel && currentFeel == RhythmFeel.TwoBeat)
        {
            EvaluateFourFeelRequest(chorus, bar, barProgress, decisions);
        }
        else if (State == AdaptiveEnergyState.SoloFourFeel &&
                 currentFeel == RhythmFeel.FourBeat &&
                 !highFourFeelActive)
        {
            EvaluateHighFourFeelRequest(chorus, bar, barProgress, decisions);
        }

        return decisions;
    }

    private void SynchronizeState(
        int chorus,
        int bar,
        RhythmFeel currentFeel,
        bool highFourFeelActive,
        ICollection<AdaptiveEnergyDecision> decisions)
    {
        if (chorus == 1)
        {
            State = AdaptiveEnergyState.ThemeLearning;
            return;
        }

        if (highFourFeelActive && State != AdaptiveEnergyState.SoloHighFourFeel)
        {
            State = AdaptiveEnergyState.SoloHighFourFeel;
            _highFourFeelRequestArmed = false;
            decisions.Add(new AdaptiveEnergyDecision(
                AdaptiveEnergyDecisionType.HighFourFeelActivated,
                chorus,
                bar,
                ThemeBaseline,
                CurrentWindowMedian(),
                "High four-feel became active. It will remain latched until the head return."));
            return;
        }

        if (currentFeel == RhythmFeel.FourBeat && State is AdaptiveEnergyState.ThemeLearning or AdaptiveEnergyState.SoloTwoFeel)
        {
            State = AdaptiveEnergyState.SoloFourFeel;
            _fourFeelRequestArmed = false;
            _fourFeelStartAbsoluteBar = AbsoluteBar(chorus, bar);
            FourFeelEstablishedLevel = _pendingFourFeelLevel > 0
                ? _pendingFourFeelLevel
                : Math.Max(ThemeBaseline, CurrentWindowMedian());
            _pendingFourFeelLevel = 0;
            decisions.Add(new AdaptiveEnergyDecision(
                AdaptiveEnergyDecisionType.FourFeelActivated,
                chorus,
                bar,
                ThemeBaseline,
                FourFeelEstablishedLevel,
                $"Four-feel became active at Chorus {chorus}, Bar {bar}."));
            return;
        }

        // Manual two-feel requests remain possible. Automatic logic never performs
        // this downgrade, and a latched high-four state is otherwise preserved.
        if (currentFeel == RhythmFeel.TwoBeat && State is AdaptiveEnergyState.SoloFourFeel or AdaptiveEnergyState.SoloHighFourFeel)
        {
            State = AdaptiveEnergyState.SoloTwoFeel;
            _fourFeelRequestArmed = false;
            _highFourFeelRequestArmed = false;
            _fourFeelStartAbsoluteBar = 0;
            FourFeelEstablishedLevel = 0;
        }
        else if (State == AdaptiveEnergyState.ThemeLearning)
        {
            State = AdaptiveEnergyState.SoloTwoFeel;
        }
    }

    private void EvaluateFourFeelRequest(
        int chorus,
        int bar,
        double barProgress,
        ICollection<AdaptiveEnergyDecision> decisions)
    {
        var thresholds = RiseThresholds.ForFourFeel(_fourFeelSensitivity);
        var window = CurrentWindow(4);
        var qualifies = QualifiesRise(window, ThemeBaseline, thresholds);

        if (qualifies && !_fourFeelRequestArmed)
        {
            var target = FormBoundaryCalculator.GetNextTwoToFourBoundary(chorus, bar, _chorusBars);
            _fourFeelRequestArmed = true;
            _pendingFourFeelLevel = Median(window);
            decisions.Add(new AdaptiveEnergyDecision(
                AdaptiveEnergyDecisionType.FourFeelRequestArmed,
                target.Chorus,
                target.Bar,
                ThemeBaseline,
                _pendingFourFeelLevel,
                $"Solo energy rose clearly above the first-theme baseline; four-feel reserved for Chorus {target.Chorus}, Bar {target.Bar}."));
        }
        else if (_fourFeelRequestArmed && barProgress >= 0.75 && ClearlyLostRise(window, ThemeBaseline, thresholds))
        {
            _fourFeelRequestArmed = false;
            _pendingFourFeelLevel = 0;
            decisions.Add(new AdaptiveEnergyDecision(
                AdaptiveEnergyDecisionType.FourFeelRequestCancelled,
                0,
                0,
                ThemeBaseline,
                MedianOrZero(window),
                "The rise was not maintained to the musical boundary; the pending four-feel change was cancelled."));
        }
    }

    private void EvaluateHighFourFeelRequest(
        int chorus,
        int bar,
        double barProgress,
        ICollection<AdaptiveEnergyDecision> decisions)
    {
        var elapsedBars = AbsoluteBar(chorus, bar) - _fourFeelStartAbsoluteBar;
        var halfChorusBars = _chorusBars / 2;
        if (_fourFeelStartAbsoluteBar == 0 ||
            elapsedBars < halfChorusBars - 1 ||
            (elapsedBars == halfChorusBars - 1 && barProgress < 0.50))
        {
            return;
        }

        var thresholds = RiseThresholds.ForHighFourFeel(_highFourFeelSensitivity);
        var window = CurrentWindow(4);
        var qualifies = QualifiesRise(window, ThemeBaseline, thresholds) &&
            MedianOrZero(window) >= FourFeelEstablishedLevel + thresholds.EstablishedLevelDelta;

        if (qualifies && !_highFourFeelRequestArmed)
        {
            var target = FormBoundaryCalculator.GetNextTwoToFourBoundary(chorus, bar, _chorusBars);
            _highFourFeelRequestArmed = true;
            decisions.Add(new AdaptiveEnergyDecision(
                AdaptiveEnergyDecisionType.HighFourFeelRequestArmed,
                target.Chorus,
                target.Bar,
                ThemeBaseline,
                Median(window),
                $"A second sustained lift was detected; high four-feel reserved for Chorus {target.Chorus}, Bar {target.Bar}."));
        }
        else if (_highFourFeelRequestArmed && barProgress >= 0.75 &&
                 (ClearlyLostRise(window, ThemeBaseline, thresholds) ||
                  MedianOrZero(window) < FourFeelEstablishedLevel + thresholds.EstablishedLevelDelta * 0.55))
        {
            _highFourFeelRequestArmed = false;
            decisions.Add(new AdaptiveEnergyDecision(
                AdaptiveEnergyDecisionType.HighFourFeelRequestCancelled,
                0,
                0,
                ThemeBaseline,
                MedianOrZero(window),
                "The second lift was not maintained; the pending high-four change was cancelled."));
        }
    }

    private void FinalizeBar()
    {
        if (_barSamples == 0 || !_barHadInput)
        {
            return;
        }

        var average = _barEnergyTotal / _barSamples;
        if (_currentChorus == 1)
        {
            _themeBars.Enqueue(average);
        }
        else
        {
            _recentBars.Enqueue(average);
            while (_recentBars.Count > RecentBarLimit)
            {
                _recentBars.Dequeue();
            }
        }
    }

    private bool EstablishThemeBaseline()
    {
        if (ThemeBaselineAvailable || _themeBars.Count < Math.Max(8, _chorusBars / 2))
        {
            State = AdaptiveEnergyState.SoloTwoFeel;
            return ThemeBaselineAvailable;
        }

        var bars = _themeBars.ToArray();
        var smoothed = new double[bars.Length];
        for (var i = 0; i < bars.Length; i++)
        {
            smoothed[i] = i == 0 ? bars[i] : (bars[i - 1] + bars[i]) / 2.0;
        }

        ThemeBaseline = Median(smoothed);
        ThemeBaselineAvailable = true;
        State = AdaptiveEnergyState.SoloTwoFeel;
        return true;
    }

    private void BeginBar(long nowMilliseconds, int chorus, int bar)
    {
        _currentChorus = chorus;
        _currentBar = bar;
        _barStartMilliseconds = nowMilliseconds;
        _barEnergyTotal = 0;
        _barSamples = 0;
        _barHadInput = false;
    }

    private IReadOnlyList<double> CurrentWindow(int count)
    {
        var values = _recentBars.TakeLast(Math.Max(0, count - 1)).ToList();
        if (_barSamples > 0 && _barHadInput)
        {
            values.Add(_barEnergyTotal / _barSamples);
        }
        else
        {
            values.AddRange(_recentBars.TakeLast(1));
        }

        return values.TakeLast(count).ToArray();
    }

    private double CurrentWindowMedian()
    {
        var window = CurrentWindow(4);
        return MedianOrZero(window);
    }

    private bool QualifiesRise(
        IReadOnlyList<double> window,
        double baseline,
        RiseThresholds thresholds)
    {
        if (window.Count < 4 || baseline <= 0)
        {
            return false;
        }

        var qualifyingBars = window.Count(value =>
            value >= baseline * thresholds.Ratio &&
            value - baseline >= thresholds.AbsoluteRise);
        if (qualifyingBars < thresholds.RequiredBars)
        {
            return false;
        }

        var trend = _recentBars.TakeLast(7).ToList();
        if (_barSamples > 0 && _barHadInput)
        {
            trend.Add(_barEnergyTotal / _barSamples);
        }

        if (trend.Count >= 6)
        {
            var trendMedian = Median(trend);
            var relaxedRatio = 1.0 + (thresholds.Ratio - 1.0) * 0.55;
            if (trendMedian < baseline * relaxedRatio ||
                trendMedian - baseline < thresholds.AbsoluteRise * 0.55)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ClearlyLostRise(
        IReadOnlyList<double> window,
        double baseline,
        RiseThresholds thresholds)
    {
        if (window.Count < 3 || baseline <= 0)
        {
            return false;
        }

        var relaxedRatio = 1.0 + (thresholds.Ratio - 1.0) * 0.60;
        var relaxedAbsolute = thresholds.AbsoluteRise * 0.55;
        return window.Count(value =>
            value >= baseline * relaxedRatio &&
            value - baseline >= relaxedAbsolute) < 2;
    }

    private double GetBarProgress(long nowMilliseconds, int tempoBpm)
    {
        var barMilliseconds = 240_000.0 / tempoBpm;
        return Math.Clamp((nowMilliseconds - _barStartMilliseconds) / barMilliseconds, 0.0, 1.0);
    }

    private int AbsoluteBar(int chorus, int bar) => (chorus - 1) * _chorusBars + bar;

    private static double ObservedEnergy(PerformanceGuidance guidance) =>
        Math.Clamp(guidance.Energy * 0.55 + guidance.ShortEnergy * 0.45, 0.0, 1.0);

    private static double MedianOrZero(IReadOnlyList<double> values) =>
        values.Count == 0 ? 0 : Median(values);

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2.0
            : ordered[middle];
    }

    private readonly record struct RiseThresholds(
        double Ratio,
        double AbsoluteRise,
        int RequiredBars,
        double EstablishedLevelDelta)
    {
        public static RiseThresholds ForFourFeel(int sensitivity) => new(
            Ratio: SensitivityMap(sensitivity, 1.24, 1.15, 1.08),
            AbsoluteRise: SensitivityMap(sensitivity, 0.11, 0.07, 0.04),
            RequiredBars: sensitivity < 25 ? 4 : 3,
            EstablishedLevelDelta: 0);

        public static RiseThresholds ForHighFourFeel(int sensitivity) => new(
            Ratio: SensitivityMap(sensitivity, 1.48, 1.35, 1.24),
            AbsoluteRise: SensitivityMap(sensitivity, 0.20, 0.15, 0.10),
            RequiredBars: sensitivity < 25 ? 4 : 3,
            EstablishedLevelDelta: SensitivityMap(sensitivity, 0.09, 0.06, 0.04));

        private static double SensitivityMap(
            int sensitivity,
            double lowSensitivityValue,
            double standardValue,
            double highSensitivityValue)
        {
            var clamped = Math.Clamp(sensitivity, 0, 100);
            return clamped <= 50
                ? Lerp(lowSensitivityValue, standardValue, clamped / 50.0)
                : Lerp(standardValue, highSensitivityValue, (clamped - 50) / 50.0);
        }

        private static double Lerp(double start, double end, double amount) =>
            start + (end - start) * amount;
    }
}
