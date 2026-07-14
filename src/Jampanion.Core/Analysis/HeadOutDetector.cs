using Jampanion.Core.Music;

namespace Jampanion.Core.Analysis;

public sealed class HeadOutDetector
{
    private int _sensitivity = 50;
    private int _currentChorus;
    private int _currentBar;
    private int _chorusBars = SessionConstants.ChorusBars;
    private RhythmFeel _currentFeel = RhythmFeel.TwoBeat;
    private long _barStartMilliseconds;
    private double _barShortEnergyTotal;
    private int _barSamples;
    private bool _chorusHadInput;
    private long _lastObservedAttackMilliseconds = long.MinValue;
    private long _chorusAttackFloorMilliseconds = long.MinValue;
    private double?[] _completedBars = new double?[SessionConstants.ChorusBars + 1];

    private bool _candidateArmed;
    private int _candidateTargetChorus;
    private double _candidateReturnLimit;
    private bool _confirmedRecheckArmed;
    private int _confirmedRecheckTargetChorus;
    private double _confirmedRecheckReturnLimit;
    private double _confirmedRecheckCancellationRatio;
    private bool _headOutConfirmed;
    private HeadOutDiagnostics _diagnostics;

    public bool CandidateArmed => _candidateArmed;
    public bool ConfirmationRecheckArmed => _confirmedRecheckArmed;
    public HeadOutDiagnostics Diagnostics => _diagnostics;

    public void Configure(int sensitivity)
    {
        _sensitivity = Math.Clamp(sensitivity, 0, 100);
    }

    public void Reset()
    {
        _currentChorus = 0;
        _currentBar = 0;
        _chorusBars = SessionConstants.ChorusBars;
        _currentFeel = RhythmFeel.TwoBeat;
        _barStartMilliseconds = 0;
        _barShortEnergyTotal = 0;
        _barSamples = 0;
        _chorusHadInput = false;
        _lastObservedAttackMilliseconds = long.MinValue;
        _chorusAttackFloorMilliseconds = long.MinValue;
        _completedBars = new double?[SessionConstants.ChorusBars + 1];
        ClearCandidate();
        ClearConfirmedRecheck();
        _headOutConfirmed = false;
        _diagnostics = HeadOutDiagnostics.Empty;
    }

    public HeadOutDecision Update(
        long nowMilliseconds,
        int tempoBpm,
        int chorus,
        int bar,
        RhythmFeel feel,
        PerformanceGuidance guidance,
        int chorusBars = SessionConstants.ChorusBars,
        bool detectionEnabled = true,
        int beatsPerBar = 4)
    {
        if (tempoBpm is < 40 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(tempoBpm));
        }

        if (chorus < 1 || chorusBars < 8 || bar is < 1 || bar > chorusBars)
        {
            return HeadOutDecision.None;
        }

        if (beatsPerBar is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerBar));
        }

        // Confirmation disables further decisions, but diagnostics continue so
        // the bars reset and keep moving through HEAD OUT and manual mode.
        var normalDetectionEnabled = detectionEnabled && !_headOutConfirmed;

        HeadOutDecision transitionDecision = HeadOutDecision.None;
        if (_currentChorus == 0)
        {
            _chorusBars = chorusBars;
            _completedBars = new double?[chorusBars + 1];
            BeginBar(nowMilliseconds, chorus, bar, feel);
        }
        else if (_currentChorus != chorus || _currentBar != bar)
        {
            transitionDecision = FinalizeBar(normalDetectionEnabled);
            var crossedChorus = _currentChorus != chorus;
            _chorusBars = chorusBars;
            if (crossedChorus)
            {
                _completedBars = new double?[chorusBars + 1];
                _chorusHadInput = false;
                _chorusAttackFloorMilliseconds = _lastObservedAttackMilliseconds;
            }
            BeginBar(nowMilliseconds, chorus, bar, feel);
        }

        _currentFeel = feel;
        var hasNewChorusAttack = guidance.LastAttackMilliseconds != long.MinValue
            ? guidance.LastAttackMilliseconds > _chorusAttackFloorMilliseconds
            : guidance.HasRecentInput;
        if (hasNewChorusAttack)
        {
            _chorusHadInput = true;
        }
        if (_chorusHadInput)
        {
            // Reference begins with the first played sample in each chorus.
            // Silence before the player enters must not dilute the baseline.
            _barShortEnergyTotal += guidance.ShortEnergy;
            _barSamples++;
        }
        if (guidance.LastAttackMilliseconds > _lastObservedAttackMilliseconds)
        {
            _lastObservedAttackMilliseconds = guidance.LastAttackMilliseconds;
        }

        UpdateContinuousDiagnostics();

        if (detectionEnabled && _confirmedRecheckArmed)
        {
            var reversalDecision = EvaluateConfirmedRecheck(nowMilliseconds, tempoBpm, beatsPerBar);
            if (reversalDecision.Type != HeadOutDecisionType.None)
            {
                return reversalDecision;
            }
        }

        if (transitionDecision.Type != HeadOutDecisionType.None)
        {
            return transitionDecision;
        }

        if (normalDetectionEnabled && _candidateArmed)
        {
            var candidateDecision = EvaluateCandidate(nowMilliseconds, tempoBpm, beatsPerBar);
            if (candidateDecision.Type != HeadOutDecisionType.None)
            {
                return candidateDecision;
            }
        }

        return normalDetectionEnabled
            ? EvaluateLiveFinalBar(nowMilliseconds, tempoBpm, beatsPerBar)
            : HeadOutDecision.None;
    }

    private HeadOutDecision EvaluateLiveFinalBar(long nowMilliseconds, int tempoBpm, int beatsPerBar)
    {
        if (_currentBar != _chorusBars ||
            _barSamples == 0 ||
            !_chorusHadInput)
        {
            return HeadOutDecision.None;
        }

        var reference = ReferenceEnergy();
        var penultimate = _completedBars[_chorusBars - 1];
        if (reference <= 0 || penultimate is null)
        {
            return HeadOutDecision.None;
        }

        var barMilliseconds = 60_000.0 * beatsPerBar / tempoBpm;
        var progress = Math.Clamp((nowMilliseconds - _barStartMilliseconds) / barMilliseconds, 0.0, 1.0);
        if (progress < 0.50)
        {
            return HeadOutDecision.None;
        }

        var current = _barShortEnergyTotal / _barSamples;
        var endingAverage = (penultimate.Value + current) / 2.0;
        var thresholds = DropThresholds.ForSensitivity(_sensitivity);
        UpdateDiagnostics(reference, current, endingAverage, thresholds);
        if (progress >= 0.75 && IsClearTwoBarDrop(reference, endingAverage, thresholds))
        {
            _headOutConfirmed = true;
            ClearCandidate();
            ArmConfirmedRecheck(
                _currentChorus + 1,
                EffectiveThreshold(reference, thresholds),
                thresholds.CancellationReboundRatio);
            return new HeadOutDecision(
                HeadOutDecisionType.ConfirmNextChorus,
                _currentChorus + 1,
                reference,
                endingAverage,
                $"A clear drop was detected across the final two bars of Chorus {_currentChorus}." );
        }

        if (!_candidateArmed &&
            progress >= 0.75 &&
            endingAverage > EffectiveThreshold(reference, thresholds) &&
            endingAverage <= reference)
        {
            ArmCandidate(_currentChorus + 1, EffectiveThreshold(reference, thresholds));
            return new HeadOutDecision(
                HeadOutDecisionType.CandidateArmed,
                _candidateTargetChorus,
                reference,
                endingAverage,
                $"Possible head return detected in the final two bars of Chorus {_currentChorus}." );
        }

        return HeadOutDecision.None;
    }

    private HeadOutDecision EvaluateCandidate(long nowMilliseconds, int tempoBpm, int beatsPerBar)
    {
        if (_currentChorus != _candidateTargetChorus || _currentBar != 2 || _barSamples == 0)
        {
            return HeadOutDecision.None;
        }

        var barMilliseconds = 60_000.0 * beatsPerBar / tempoBpm;
        var progress = Math.Clamp((nowMilliseconds - _barStartMilliseconds) / barMilliseconds, 0.0, 1.0);
        if (progress < 0.75)
        {
            return HeadOutDecision.None;
        }

        var firstBar = _completedBars[1];
        if (firstBar is null)
        {
            return HeadOutDecision.None;
        }

        var current = _barShortEnergyTotal / _barSamples;
        var openingAverage = (firstBar.Value + current) / 2.0;
        var rememberedLimit = _candidateReturnLimit;
        var targetChorus = _currentChorus + 1;
        if (openingAverage <= rememberedLimit)
        {
            _headOutConfirmed = true;
            ClearCandidate();
            return new HeadOutDecision(
                HeadOutDecisionType.ConfirmNow,
                _currentChorus,
                rememberedLimit,
                openingAverage,
                $"The opening two bars of Chorus {_currentChorus} confirmed an immediate theme return." );
        }

        ClearCandidate();
        return new HeadOutDecision(
            HeadOutDecisionType.CandidateExpired,
            targetChorus,
            rememberedLimit,
            openingAverage,
            $"The opening two bars of Chorus {_currentChorus} did not confirm the theme return." );
    }

    private HeadOutDecision EvaluateConfirmedRecheck(long nowMilliseconds, int tempoBpm, int beatsPerBar)
    {
        if (_currentChorus != _confirmedRecheckTargetChorus ||
            _currentBar is < 1 or > 2)
        {
            return HeadOutDecision.None;
        }

        var barMilliseconds = 60_000.0 * beatsPerBar / tempoBpm;
        var progress = Math.Clamp((nowMilliseconds - _barStartMilliseconds) / barMilliseconds, 0.0, 1.0);
        if (progress < 0.75)
        {
            return HeadOutDecision.None;
        }

        if (_barSamples == 0)
        {
            if (_currentBar == 2)
            {
                ClearConfirmedRecheck();
            }
            return HeadOutDecision.None;
        }

        var current = _barShortEnergyTotal / _barSamples;
        var openingAverage = current;
        if (_currentBar == 2)
        {
            var firstBar = _completedBars[1];
            openingAverage = firstBar is null ? current : (firstBar.Value + current) / 2.0;
        }

        var rememberedLimit = _confirmedRecheckReturnLimit;
        var cancellationRatio = _confirmedRecheckCancellationRatio;
        if (openingAverage >= rememberedLimit * cancellationRatio)
        {
            ClearConfirmedRecheck();
            _headOutConfirmed = false;
            return new HeadOutDecision(
                HeadOutDecisionType.ConfirmedHeadOutCancelled,
                _currentChorus,
                rememberedLimit,
                openingAverage,
                $"HEAD OUT was cancelled because the opening energy of Chorus {_currentChorus} exceeded the sensitivity-adjusted rebound threshold ({cancellationRatio:0.00} x the preceding Return limit)." );
        }

        if (_currentBar == 2)
        {
            ClearConfirmedRecheck();
        }

        return HeadOutDecision.None;
    }

    private HeadOutDecision FinalizeBar(bool detectionEnabled)
    {
        if (_barSamples == 0)
        {
            return HeadOutDecision.None;
        }

        var averageEnergy = _barShortEnergyTotal / _barSamples;
        if (_currentBar >= 1 && _currentBar < _completedBars.Length)
        {
            _completedBars[_currentBar] = averageEnergy;
        }

        if (!detectionEnabled || !_chorusHadInput || _currentBar != _chorusBars)
        {
            return HeadOutDecision.None;
        }

        var reference = ReferenceEnergy();
        var penultimate = _completedBars[_chorusBars - 1];
        if (reference <= 0 || penultimate is null)
        {
            return HeadOutDecision.None;
        }

        var endingAverage = (penultimate.Value + averageEnergy) / 2.0;
        var thresholds = DropThresholds.ForSensitivity(_sensitivity);
        UpdateDiagnostics(reference, averageEnergy, endingAverage, thresholds);
        if (IsClearTwoBarDrop(reference, endingAverage, thresholds))
        {
            _headOutConfirmed = true;
            ClearCandidate();
            ArmConfirmedRecheck(
                _currentChorus + 1,
                EffectiveThreshold(reference, thresholds),
                thresholds.CancellationReboundRatio);
            return new HeadOutDecision(
                HeadOutDecisionType.ConfirmNextChorus,
                _currentChorus + 1,
                reference,
                endingAverage,
                $"A clear drop was established across the final two bars of Chorus {_currentChorus}." );
        }

        if (endingAverage > EffectiveThreshold(reference, thresholds) &&
            endingAverage <= reference)
        {
            var target = _currentChorus + 1;
            ArmCandidate(target, EffectiveThreshold(reference, thresholds));
            return new HeadOutDecision(
                HeadOutDecisionType.CandidateArmed,
                target,
                reference,
                endingAverage,
                $"Possible head return will be checked through Bar 2 of Chorus {target}." );
        }

        return HeadOutDecision.None;
    }

    private double ReferenceEnergy()
    {
        var lastReferenceBar = _chorusBars - 2;
        var values = new List<double>(Math.Max(0, lastReferenceBar));
        for (var bar = 1; bar <= lastReferenceBar; bar++)
        {
            if (bar >= 1 && bar < _completedBars.Length &&
                _completedBars[bar] is double value)
            {
                values.Add(value);
            }
        }

        return values.Count >= 2 ? values.Average() : 0;
    }

    private static bool IsClearTwoBarDrop(
        double reference,
        double endingAverage,
        DropThresholds thresholds) =>
        endingAverage <= EffectiveThreshold(reference, thresholds);

    private void BeginBar(long nowMilliseconds, int chorus, int bar, RhythmFeel feel)
    {
        _currentChorus = chorus;
        _currentBar = bar;
        _currentFeel = feel;
        _barStartMilliseconds = nowMilliseconds;
        _barShortEnergyTotal = 0;
        _barSamples = 0;
    }

    private void ArmCandidate(int targetChorus, double returnLimit)
    {
        _candidateArmed = true;
        _candidateTargetChorus = targetChorus;
        _candidateReturnLimit = returnLimit;
    }

    private void ArmConfirmedRecheck(int targetChorus, double returnLimit, double cancellationRatio)
    {
        _confirmedRecheckArmed = true;
        _confirmedRecheckTargetChorus = targetChorus;
        _confirmedRecheckReturnLimit = returnLimit;
        _confirmedRecheckCancellationRatio = cancellationRatio;
    }

    private void ClearCandidate()
    {
        _candidateArmed = false;
        _candidateTargetChorus = 0;
        _candidateReturnLimit = 0;
    }

    private void ClearConfirmedRecheck()
    {
        _confirmedRecheckArmed = false;
        _confirmedRecheckTargetChorus = 0;
        _confirmedRecheckReturnLimit = 0;
        _confirmedRecheckCancellationRatio = 0;
    }

    private void UpdateContinuousDiagnostics()
    {
        if (_barSamples == 0)
        {
            _diagnostics = HeadOutDiagnostics.Empty;
            return;
        }

        var values = new List<double>(_currentBar);
        for (var bar = 1; bar < _currentBar && bar < _completedBars.Length; bar++)
        {
            if (_completedBars[bar] is double value)
            {
                values.Add(value);
            }
        }

        var current = _barShortEnergyTotal / _barSamples;
        values.Add(current);

        var endingValues = values.TakeLast(2).ToArray();
        var endingAverage = endingValues.Length == 0 ? 0 : endingValues.Average();

        // Reference is the arithmetic mean from the chorus head up to, but not
        // including, the rolling final-two-bar window. During Bars 1-2 there is
        // no preceding window yet, so use the available Short values for a
        // continuous display; from Bar 3 onward the definition is exact.
        var referenceValues = values.Count <= 2
            ? values
            : values.Take(values.Count - 2);
        var reference = referenceValues.Average();
        var thresholds = DropThresholds.ForSensitivity(_sensitivity);
        UpdateDiagnostics(reference, current, endingAverage, thresholds);
    }

    private void UpdateDiagnostics(
        double reference,
        double current,
        double endingAverage,
        DropThresholds thresholds)
    {
        var effectiveThreshold = _confirmedRecheckArmed && _currentChorus == _confirmedRecheckTargetChorus
            ? _confirmedRecheckReturnLimit
            : _candidateArmed && _currentChorus == _candidateTargetChorus
                ? _candidateReturnLimit
                : EffectiveThreshold(reference, thresholds);
        var confirmedRecheckActive = _confirmedRecheckArmed &&
            _currentChorus == _confirmedRecheckTargetChorus;
        var cancellationThreshold = confirmedRecheckActive
            ? _confirmedRecheckReturnLimit * _confirmedRecheckCancellationRatio
            : 0;
        _diagnostics = new HeadOutDiagnostics(
            Math.Clamp(current, 0, 1),
            Math.Clamp(reference, 0, 1),
            Math.Clamp(endingAverage, 0, 1),
            Math.Clamp(effectiveThreshold, 0, 1),
            Math.Max(0, cancellationThreshold),
            confirmedRecheckActive);
    }

    private static double EffectiveThreshold(double reference, DropThresholds thresholds) =>
        Math.Max(0, reference * thresholds.EndingRatio);

    private readonly record struct DropThresholds(
        double EndingRatio,
        double CancellationReboundRatio)
    {
        public static DropThresholds ForSensitivity(int sensitivity) => new(
            EndingRatio: SensitivityMap(sensitivity, 0.68, 0.78, 0.95),
            CancellationReboundRatio: SensitivityMap(sensitivity, 1.20, 1.15, 1.10));

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

public readonly record struct HeadOutDiagnostics(
    double CurrentEnergy,
    double ReferenceEnergy,
    double EndingAverageEnergy,
    double EffectiveThreshold,
    double CancellationThreshold,
    bool ConfirmationRecheckActive)
{
    public static HeadOutDiagnostics Empty => new(0, 0, 0, 0, 0, false);
}
