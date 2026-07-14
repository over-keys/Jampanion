namespace Jampanion.Core.Analysis;

public sealed class HumanPerformanceAnalyzer
{
    private const long ChordClusterMilliseconds = 35;

    private readonly object _gate = new();
    private readonly List<Attack> _attacks = [];
    private readonly Dictionary<(byte Channel, byte Note), ActiveNote> _activeNotes = [];

    private long _lastAttackMilliseconds = long.MinValue;
    private long _lastPhraseEndMilliseconds = long.MinValue;
    private long _lastEvaluationMilliseconds = long.MinValue;
    private double _highEnergyAccumulatedMilliseconds;
    private bool _phraseEndLatched;

    public void Reset()
    {
        lock (_gate)
        {
            _attacks.Clear();
            _activeNotes.Clear();
            _lastAttackMilliseconds = long.MinValue;
            _lastPhraseEndMilliseconds = long.MinValue;
            _lastEvaluationMilliseconds = long.MinValue;
            _highEnergyAccumulatedMilliseconds = 0;
            _phraseEndLatched = false;
        }
    }

    public void NoteOn(long timestampMilliseconds, byte channel, byte note, byte velocity)
    {
        if (velocity == 0)
        {
            NoteOff(timestampMilliseconds, channel, note);
            return;
        }

        lock (_gate)
        {
            var key = (channel, note);
            CloseActiveNote(key, timestampMilliseconds);

            var tone = new AttackTone(velocity, note);

            if (_attacks.Count > 0 && timestampMilliseconds - _attacks[^1].TimestampMilliseconds <= ChordClusterMilliseconds)
            {
                var previous = _attacks[^1];
                previous.Tones.Add(tone);
            }
            else
            {
                var attack = new Attack(timestampMilliseconds);
                attack.Tones.Add(tone);
                _attacks.Add(attack);
            }

            _activeNotes[key] = new ActiveNote(timestampMilliseconds, tone);
            _lastAttackMilliseconds = timestampMilliseconds;
            _phraseEndLatched = false;
        }
    }

    public void NoteOff(long timestampMilliseconds, byte channel, byte note)
    {
        lock (_gate)
        {
            CloseActiveNote((channel, note), timestampMilliseconds);
        }
    }

    public PerformanceGuidance Evaluate(long nowMilliseconds, int tempoBpm, int beatsPerBar = 4)
    {
        if (tempoBpm is < 40 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(tempoBpm));
        }

        if (beatsPerBar is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerBar));
        }

        lock (_gate)
        {
            var beatMilliseconds = 60_000.0 / tempoBpm;
            var barMilliseconds = beatMilliseconds * beatsPerBar;
            var longWindowMilliseconds = barMilliseconds * 4.0;
            var shortWindowMilliseconds = barMilliseconds;
            var minimumNoteDurationMilliseconds = beatMilliseconds / 4.0;
            var retentionMilliseconds = barMilliseconds * 8.0;
            var pruneBefore = nowMilliseconds - (long)Math.Ceiling(retentionMilliseconds);
            _attacks.RemoveAll(attack => attack.TimestampMilliseconds < pruneBefore);

            var longWindowStart = nowMilliseconds - (long)Math.Ceiling(longWindowMilliseconds);
            var shortWindowStart = nowMilliseconds - (long)Math.Ceiling(shortWindowMilliseconds);
            var recent = _attacks.Where(attack => attack.TimestampMilliseconds >= longWindowStart).ToArray();
            var shortRecent = recent.Where(attack => attack.TimestampMilliseconds >= shortWindowStart).ToArray();
            var lastAttackAge = _lastAttackMilliseconds == long.MinValue
                ? double.PositiveInfinity
                : Math.Max(0, nowMilliseconds - _lastAttackMilliseconds);
            var hasRecentInput =
                (CountEligibleAttacks(recent, minimumNoteDurationMilliseconds) > 0 || _activeNotes.Count > 0) &&
                lastAttackAge <= barMilliseconds * 2.0;

            var elapsedMilliseconds = _lastEvaluationMilliseconds == long.MinValue
                ? 0
                : Math.Clamp(nowMilliseconds - _lastEvaluationMilliseconds, 0, (long)Math.Ceiling(barMilliseconds));
            _lastEvaluationMilliseconds = nowMilliseconds;

            if (!hasRecentInput)
            {
                _highEnergyAccumulatedMilliseconds = 0;
                return PerformanceGuidance.Neutral;
            }

            var activeFromAttacks = Clamp01(
                CountEligibleAttacks(shortRecent, minimumNoteDurationMilliseconds) / 5.0);
            var recency = Clamp01(1.0 - lastAttackAge / (beatMilliseconds * 1.5));
            var phraseActivity = Math.Max(activeFromAttacks, _activeNotes.Count > 0 ? 0.85 : recency);

            var longMetrics = CalculateMetrics(
                recent,
                beatsInWindow: beatsPerBar * 4.0,
                phraseActivity,
                minimumNoteDurationMilliseconds);
            var shortMetrics = CalculateMetrics(
                shortRecent,
                beatsInWindow: beatsPerBar,
                phraseActivity,
                minimumNoteDurationMilliseconds);
            var combinedEnergy = Clamp01(longMetrics.Energy * 0.65 + shortMetrics.Energy * 0.35);

            var intensity = combinedEnergy switch
            {
                >= 0.68 => PerformanceIntensity.High,
                >= 0.40 => PerformanceIntensity.Medium,
                _ => PerformanceIntensity.Low
            };

            var enoughPriorMaterial = CountEligibleAttacks(
                recent.Where(attack => attack.TimestampMilliseconds < nowMilliseconds - beatMilliseconds * 0.45).ToArray(),
                minimumNoteDurationMilliseconds) >= 3;
            var phraseEndWindow = lastAttackAge >= beatMilliseconds * 0.55 &&
                lastAttackAge <= beatMilliseconds * 1.80;
            if (!_phraseEndLatched &&
                _activeNotes.Count == 0 &&
                enoughPriorMaterial &&
                phraseEndWindow)
            {
                _lastPhraseEndMilliseconds = nowMilliseconds;
                _phraseEndLatched = true;
            }

            var phraseEndedRecently = _lastPhraseEndMilliseconds != long.MinValue &&
                nowMilliseconds - _lastPhraseEndMilliseconds <= barMilliseconds;

            var highNow = combinedEnergy >= 0.68 && phraseActivity >= 0.45;
            var clearlyLow = combinedEnergy < 0.58 || phraseActivity < 0.30;
            if (highNow)
            {
                _highEnergyAccumulatedMilliseconds += elapsedMilliseconds;
            }
            else if (clearlyLow)
            {
                _highEnergyAccumulatedMilliseconds = Math.Max(
                    0,
                    _highEnergyAccumulatedMilliseconds - elapsedMilliseconds * 1.25);
            }
            else
            {
                // Hysteresis without false accumulation: the middle band preserves
                // continuity briefly, but it never counts as additional high-energy time.
                _highEnergyAccumulatedMilliseconds = Math.Max(
                    0,
                    _highEnergyAccumulatedMilliseconds - elapsedMilliseconds * 0.30);
            }

            var highEnergyBars = _highEnergyAccumulatedMilliseconds / barMilliseconds;

            return new PerformanceGuidance(
                HasRecentInput: true,
                Intensity: intensity,
                Energy: longMetrics.Energy,
                ShortEnergy: shortMetrics.Energy,
                Density: longMetrics.Density,
                AverageVelocity: longMetrics.AverageVelocity,
                Motion: longMetrics.Motion,
                PhraseActivity: phraseActivity,
                PhraseEndedRecently: phraseEndedRecently,
                HighEnergySustained: highEnergyBars >= 4.0,
                HighEnergyBars: highEnergyBars,
                AveragePitch: longMetrics.AveragePitch,
                LastAttackMilliseconds: _lastAttackMilliseconds);
        }
    }

    public ScopedShortEnergy EvaluateShortEnergySince(
        long nowMilliseconds,
        int tempoBpm,
        int beatsPerBar,
        long attackTimestampExclusive)
    {
        if (tempoBpm is < 40 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(tempoBpm));
        }

        if (beatsPerBar is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerBar));
        }

        lock (_gate)
        {
            var beatMilliseconds = 60_000.0 / tempoBpm;
            var barMilliseconds = beatMilliseconds * beatsPerBar;
            var minimumNoteDurationMilliseconds = beatMilliseconds / 4.0;
            var shortWindowStart = nowMilliseconds - (long)Math.Ceiling(barMilliseconds);
            var scoped = _attacks
                .Where(attack => attack.TimestampMilliseconds > attackTimestampExclusive &&
                                 attack.TimestampMilliseconds >= shortWindowStart)
                .ToArray();
            var eligibleCount = CountEligibleAttacks(scoped, minimumNoteDurationMilliseconds);
            var activeCount = _activeNotes.Values.Count(note => note.StartMilliseconds > attackTimestampExclusive);
            if (eligibleCount == 0 && activeCount == 0)
            {
                return ScopedShortEnergy.Empty;
            }

            var lastAttack = scoped.Length == 0
                ? _activeNotes.Values
                    .Where(note => note.StartMilliseconds > attackTimestampExclusive)
                    .Max(note => note.StartMilliseconds)
                : scoped.Max(attack => attack.TimestampMilliseconds);
            var lastAttackAge = Math.Max(0, nowMilliseconds - lastAttack);
            var activityFromAttacks = Clamp01(eligibleCount / 5.0);
            var recency = Clamp01(1.0 - lastAttackAge / (beatMilliseconds * 1.5));
            var phraseActivity = Math.Max(activityFromAttacks, activeCount > 0 ? 0.85 : recency);
            var metrics = CalculateMetrics(
                scoped,
                beatsInWindow: beatsPerBar,
                phraseActivity,
                minimumNoteDurationMilliseconds);
            return new ScopedShortEnergy(true, metrics.Energy, lastAttack);
        }
    }

    private static Metrics CalculateMetrics(
        IReadOnlyList<Attack> attacks,
        double beatsInWindow,
        double phraseActivity,
        double minimumNoteDurationMilliseconds)
    {
        var measurements = attacks
            .Select(attack => ToMeasurement(attack, minimumNoteDurationMilliseconds))
            .Where(measurement => measurement is not null)
            .Select(measurement => measurement!.Value)
            .ToArray();
        if (measurements.Length == 0)
        {
            return new Metrics(0, 0, 0, 0, 64.0);
        }

        var attacksPerBeat = measurements.Length / beatsInWindow;
        var density = Clamp01((attacksPerBeat - 0.10) / 1.40);
        var averageVelocityRaw = measurements.Average(measurement => measurement.Velocity);
        var averageVelocity = Clamp01((averageVelocityRaw - 32.0) / 68.0);
        var motion = CalculateMotion(measurements);
        var averagePitch = measurements.Sum(measurement => measurement.MeanPitch * measurement.NoteCount) /
            Math.Max(1, measurements.Sum(measurement => measurement.NoteCount));
        var energy = Clamp01(
            0.47 * density +
            0.28 * averageVelocity +
            0.12 * motion +
            0.13 * phraseActivity);

        return new Metrics(energy, density, averageVelocity, motion, averagePitch);
    }

    private static int CountEligibleAttacks(
        IReadOnlyList<Attack> attacks,
        double minimumNoteDurationMilliseconds) =>
        attacks.Count(attack => ToMeasurement(attack, minimumNoteDurationMilliseconds) is not null);

    private static AttackMeasurement? ToMeasurement(
        Attack attack,
        double minimumNoteDurationMilliseconds)
    {
        var tones = attack.Tones
            .Where(tone => !tone.DurationMilliseconds.HasValue ||
                           tone.DurationMilliseconds.Value >= minimumNoteDurationMilliseconds)
            .ToArray();
        if (tones.Length == 0)
        {
            return null;
        }

        return new AttackMeasurement(
            attack.TimestampMilliseconds,
            tones.Max(tone => tone.Velocity),
            tones.Average(tone => tone.Note),
            tones.Length);
    }

    private static double CalculateMotion(IReadOnlyList<AttackMeasurement> attacks)
    {
        if (attacks.Count < 2)
        {
            return 0;
        }

        var total = 0.0;
        for (var i = 1; i < attacks.Count; i++)
        {
            total += Math.Abs(attacks[i].MeanPitch - attacks[i - 1].MeanPitch);
        }

        return Clamp01(total / (attacks.Count - 1) / 8.0);
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private void CloseActiveNote((byte Channel, byte Note) key, long timestampMilliseconds)
    {
        if (!_activeNotes.Remove(key, out var activeNote))
        {
            return;
        }

        activeNote.Tone.DurationMilliseconds = Math.Max(
            0,
            timestampMilliseconds - activeNote.StartMilliseconds);
    }

    private readonly record struct Metrics(
        double Energy,
        double Density,
        double AverageVelocity,
        double Motion,
        double AveragePitch);

    private sealed class Attack
    {
        public Attack(long timestampMilliseconds)
        {
            TimestampMilliseconds = timestampMilliseconds;
        }

        public long TimestampMilliseconds { get; }
        public List<AttackTone> Tones { get; } = [];
    }

    private sealed class AttackTone
    {
        public AttackTone(int velocity, byte note)
        {
            Velocity = velocity;
            Note = note;
        }

        public int Velocity { get; }
        public byte Note { get; }
        public long? DurationMilliseconds { get; set; }
    }

    private sealed record ActiveNote(long StartMilliseconds, AttackTone Tone);

    private readonly record struct AttackMeasurement(
        long TimestampMilliseconds,
        int Velocity,
        double MeanPitch,
        int NoteCount);
}

public readonly record struct ScopedShortEnergy(
    bool HasInput,
    double Energy,
    long LastAttackMilliseconds)
{
    public static ScopedShortEnergy Empty => new(false, 0, long.MinValue);
}
