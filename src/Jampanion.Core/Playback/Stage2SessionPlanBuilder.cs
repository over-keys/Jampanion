using Jampanion.Core.Music;

namespace Jampanion.Core.Playback;

public static class Stage2SessionPlanBuilder
{
    private static readonly int[] FourBeatRideOffsets = [0, 480, 800, 960, 1440, 1760];
    private static readonly byte[] FourBeatRideVelocities = [74, 64, 56, 72, 63, 55];

    public static SegmentPlan BuildSegment(TuneForm form, int segmentIndex, RhythmFeel feel)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (segmentIndex is < 0 || segmentIndex >= form.SegmentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        }

        var barCount = form.GetSegmentBarCount(segmentIndex);
        var notes = new List<ScheduledNote>(barCount * 28);
        var startBarIndex = segmentIndex * SessionConstants.BarsPerSegment;

        for (var localBar = 0; localBar < barCount; localBar++)
        {
            var formBarIndex = startBarIndex + localBar;
            var chord = form.Bars[formBarIndex].Chord;
            var barStart = (long)localBar * SessionConstants.BarTicks;
            var isSectionEnding = localBar == barCount - 1;
            var nextChord = form.Bars[(formBarIndex + 1) % form.Bars.Count].Chord;

            AddBass(notes, barStart, chord, nextChord, feel, isSectionEnding);
            AddPiano(notes, barStart, formBarIndex, chord, feel, isSectionEnding);
            AddDrums(notes, barStart, formBarIndex, feel, isSectionEnding);
        }

        EnsureExactEndpoint(notes, barCount);
        var segmentBars = form.Bars.Skip(startBarIndex).Take(barCount).ToArray();

        return new SegmentPlan(
            segmentIndex,
            feel,
            notes
                .OrderBy(note => note.StartTick)
                .ThenBy(note => note.Channel)
                .ThenBy(note => note.NoteNumber)
                .ToArray(),
            (long)barCount * SessionConstants.BarTicks);
    }

    private static void AddBass(
        List<ScheduledNote> notes,
        long barStart,
        ChordSpec chord,
        ChordSpec nextChord,
        RhythmFeel feel,
        bool isSectionEnding)
    {
        if (chord.IsNoChord)
        {
            return;
        }
        if (feel == RhythmFeel.TwoBeat)
        {
            Add(notes, barStart, 840, chord.BassRoot, 78, SessionConstants.BassChannel);
            var beatThree = isSectionEnding ? GetNearestApproach(chord.BassFifth, nextChord.BassRoot) : chord.BassFifth;
            Add(notes, barStart + 2 * SessionConstants.Ppq, 840, beatThree, 72, SessionConstants.BassChannel);
            return;
        }

        var beatNotes = new[]
        {
            chord.BassRoot,
            NearestInRange(chord.BassFifth, chord.BassRoot, 12),
            NearestInRange(chord.BassRoot, chord.BassFifth, 12),
            GetNearestApproach(chord.BassRoot, nextChord.BassRoot)
        };

        for (var beat = 0; beat < SessionConstants.BeatsPerBar; beat++)
        {
            var velocity = (byte)(beat == 0 ? 80 : 73);
            Add(
                notes,
                barStart + beat * SessionConstants.Ppq,
                410,
                beatNotes[beat],
                velocity,
                SessionConstants.BassChannel);
        }
    }

    private static void AddPiano(
        List<ScheduledNote> notes,
        long barStart,
        int formBarIndex,
        ChordSpec chord,
        RhythmFeel feel,
        bool isSectionEnding)
    {
        if (chord.IsNoChord)
        {
            return;
        }
        (long Offset, long Length, byte Velocity)[] hits;

        if (isSectionEnding)
        {
            hits = [(480L, 360L, (byte)56)];
        }
        else if (feel == RhythmFeel.TwoBeat)
        {
            hits = (formBarIndex % 4) switch
            {
                0 => [(800L, 300L, (byte)58), (1440L, 300L, (byte)52)],
                1 => [(320L, 240L, (byte)52), (1280L, 400L, (byte)58)],
                2 => [(960L, 560L, (byte)60)],
                _ => [(480L, 300L, (byte)54), (1600L, 240L, (byte)50)]
            };
        }
        else
        {
            hits = (formBarIndex % 4) switch
            {
                0 => [(320L, 240L, (byte)55), (960L, 300L, (byte)60), (1600L, 220L, (byte)52)],
                1 => [(720L, 260L, (byte)58), (1440L, 300L, (byte)56)],
                2 => [(480L, 260L, (byte)54), (1120L, 320L, (byte)61)],
                _ => [(800L, 240L, (byte)56), (1280L, 300L, (byte)59), (1760L, 120L, (byte)50)]
            };
        }

        foreach (var hit in hits)
        {
            foreach (var noteNumber in chord.PianoVoicing)
            {
                Add(notes, barStart + hit.Offset, hit.Length, noteNumber, hit.Velocity, SessionConstants.PianoChannel);
            }
        }
    }

    private static void AddDrums(
        List<ScheduledNote> notes,
        long barStart,
        int formBarIndex,
        RhythmFeel feel,
        bool isSectionEnding)
    {
        if (feel == RhythmFeel.TwoBeat)
        {
            Add(notes, barStart, 55, 51, 68, SessionConstants.DrumsChannel);
            Add(notes, barStart + 2 * SessionConstants.Ppq, 55, 51, 64, SessionConstants.DrumsChannel);
            Add(notes, barStart + SessionConstants.Ppq, 55, 44, 74, SessionConstants.DrumsChannel);
            Add(notes, barStart + 3 * SessionConstants.Ppq, 55, 44, 76, SessionConstants.DrumsChannel);
            Add(notes, barStart, 55, 36, 24, SessionConstants.DrumsChannel);
            Add(notes, barStart + 2 * SessionConstants.Ppq, 55, 36, 22, SessionConstants.DrumsChannel);
        }
        else
        {
            for (var i = 0; i < FourBeatRideOffsets.Length; i++)
            {
                var velocity = FourBeatRideVelocities[i];
                Add(notes, barStart + FourBeatRideOffsets[i], 55, 51, velocity, SessionConstants.DrumsChannel);
            }

            Add(notes, barStart + SessionConstants.Ppq, 55, 44, 72, SessionConstants.DrumsChannel);
            Add(notes, barStart + 3 * SessionConstants.Ppq, 55, 44, 74, SessionConstants.DrumsChannel);
            Add(notes, barStart, 55, 36, 25, SessionConstants.DrumsChannel);
            Add(notes, barStart + SessionConstants.Ppq, 55, 36, 20, SessionConstants.DrumsChannel);
            Add(notes, barStart + 2 * SessionConstants.Ppq, 55, 36, 23, SessionConstants.DrumsChannel);
            Add(notes, barStart + 3 * SessionConstants.Ppq, 55, 36, 19, SessionConstants.DrumsChannel);

            var compOffset = (formBarIndex % 3) switch
            {
                0 => 720L,
                1 => 1120L,
                _ => 1600L
            };
            Add(notes, barStart + compOffset, 55, 38, 42, SessionConstants.DrumsChannel);
        }

        if (isSectionEnding)
        {
            Add(notes, barStart + 1440, 55, 38, 48, SessionConstants.DrumsChannel);
            Add(notes, barStart + 1600, 55, 38, 55, SessionConstants.DrumsChannel);
            Add(notes, barStart + 1760, 160, 38, 63, SessionConstants.DrumsChannel);
        }
    }

    private static void EnsureExactEndpoint(List<ScheduledNote> notes, int barCount)
    {
        var length = (long)barCount * SessionConstants.BarTicks;
        var lastBeatStart = length - SessionConstants.Ppq;
        Add(notes, lastBeatStart, SessionConstants.Ppq, 44, 1, SessionConstants.DrumsChannel);
    }

    private static byte GetNearestApproach(byte from, byte target)
    {
        var targetValue = (int)target;
        var candidates = new[] { targetValue - 1, targetValue + 1, targetValue - 2, targetValue + 2 };
        return (byte)candidates
            .Where(value => value is >= 28 and <= 60)
            .OrderBy(value => Math.Abs(value - from))
            .First();
    }

    private static byte NearestInRange(byte pitch, byte reference, int maximumInterval)
    {
        var candidates = Enumerable.Range(-3, 7)
            .Select(octave => pitch + octave * 12)
            .Where(value => value is >= 28 and <= 60)
            .OrderBy(value => Math.Abs(value - reference))
            .ToArray();

        var selected = candidates.FirstOrDefault(value => Math.Abs(value - reference) <= maximumInterval);
        return (byte)(selected == 0 ? pitch : selected);
    }

    private static void Add(
        List<ScheduledNote> notes,
        long startTick,
        long durationTicks,
        byte noteNumber,
        byte velocity,
        byte channel)
    {
        notes.Add(new ScheduledNote(startTick, durationTicks, noteNumber, velocity, channel));
    }
}
