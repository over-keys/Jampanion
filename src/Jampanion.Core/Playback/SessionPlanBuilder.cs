using Jampanion.Core.Music;

namespace Jampanion.Core.Playback;

public static class SessionPlanBuilder
{
    private static readonly int[] RideOffsets = [0, 480, 800, 960, 1440, 1760];
    private static readonly byte[] RideVelocities = [74, 64, 56, 72, 63, 55];

    public static SessionPlan BuildAutumnLeavesTwoBeat() => BuildTwoBeat(TuneCatalog.Default);

    public static SessionPlan BuildTwoBeat(TuneForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var countInNotes = BuildCountIn(form);
        var chorusNotes = BuildChorus(form);
        return new SessionPlan(form, countInNotes, chorusNotes);
    }

    public static CountInPlan BuildCountIn(TuneForm form, int tempoBpm)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (tempoBpm is < 40 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(tempoBpm));
        }

        var singleCount = tempoBpm < 80;
        var bars = singleCount ? 1 : 2;
        var notes = new List<ScheduledNote>(form.BeatsPerBar + 2);
        for (var bar = 0; bar < bars; bar++)
        {
            var barStart = (long)bar * form.BarTicks;
            var beats = singleCount || form.BeatsPerBar == 3 || bar == bars - 1
                ? Enumerable.Range(0, form.BeatsPerBar)
                : new[] { 0, Math.Min(2, form.BeatsPerBar - 1) };
            foreach (var beat in beats.Distinct())
            {
                var finalBeat = bar == bars - 1 && beat == form.BeatsPerBar - 1;
                var velocity = beat == 0 ? (byte)105 : (byte)82;
                Add(notes, barStart + beat * SessionConstants.Ppq,
                    finalBeat ? SessionConstants.Ppq : 70,
                    37, velocity, SessionConstants.DrumsChannel);
            }
        }

        return new CountInPlan(notes, bars, bars * form.BarTicks);
    }

    private static IReadOnlyList<ScheduledNote> BuildCountIn(TuneForm form)
    {
        var notes = new List<ScheduledNote>();

        for (var bar = 0; bar < SessionConstants.CountInBars; bar++)
        {
            var barStart = (long)bar * form.BarTicks;
            for (var beat = 0; beat < form.BeatsPerBar; beat++)
            {
                var velocity = form.AccompanimentStyle == AccompanimentStyle.JazzBallad
                    ? beat == 0 ? (byte)82 : (byte)64
                    : beat == 0 ? (byte)105 : (byte)82;
                var length = bar == SessionConstants.CountInBars - 1 && beat == form.BeatsPerBar - 1
                    ? SessionConstants.Ppq
                    : 70;
                Add(notes, barStart + beat * SessionConstants.Ppq, length, 37, velocity, SessionConstants.DrumsChannel);
            }

            if (form.BeatsPerBar == 4)
            {
                var hiHatVelocity = form.AccompanimentStyle == AccompanimentStyle.JazzBallad ? (byte)50 : (byte)72;
                Add(notes, barStart + SessionConstants.Ppq, 70, 44, hiHatVelocity, SessionConstants.DrumsChannel);
                Add(notes, barStart + 3 * SessionConstants.Ppq, 70, 44, hiHatVelocity, SessionConstants.DrumsChannel);
            }
        }

        return notes;
    }

    private static IReadOnlyList<ScheduledNote> BuildChorus(TuneForm form)
    {
        var notes = new List<ScheduledNote>(form.Bars.Count * 24);

        foreach (var bar in form.Bars)
        {
            var barStart = (long)bar.Index * form.BarTicks;
            if (form.AccompanimentStyle == AccompanimentStyle.JazzWaltz)
            {
                AddWaltzBass(notes, barStart, bar);
                AddWaltzPiano(notes, barStart, bar);
                AddWaltzDrums(notes, barStart, bar.Index);
            }
            else
            {
                AddBass(notes, barStart, bar.Chord);
                AddPiano(notes, barStart, bar.Index, form.Bars.Count, bar.Chord);
                AddDrums(notes, barStart, bar.Index);
            }
        }

        return NoChordPlaybackFilter.SuppressBassAndPiano(notes, form.Bars)
            .OrderBy(note => note.StartTick)
            .ThenBy(note => note.Channel)
            .ThenBy(note => note.NoteNumber)
            .ToArray();
    }

    private static void AddWaltzBass(List<ScheduledNote> notes, long barStart, TuneBar bar)
    {
        for (var beat = 0; beat < 3; beat++)
        {
            var chord = bar.GetChordAtBeat(beat);
            var pitch = beat == 0 || bar.ChordChanges.Any(change => change.StartBeat == beat)
                ? chord.BassRoot
                : beat == 1 ? chord.BassFifth : chord.BassPitchClasses
                    .Select(pitchClass => Enumerable.Range(31, 25).First(note => note % 12 == pitchClass))
                    .OrderBy(note => Math.Abs(note - chord.BassRoot))
                    .Select(note => (byte)note)
                    .FirstOrDefault(chord.BassFifth);
            Add(notes, barStart + beat * SessionConstants.Ppq, 410, pitch, beat == 0 ? (byte)75 : (byte)69, SessionConstants.BassChannel);
        }
    }

    private static void AddWaltzPiano(List<ScheduledNote> notes, long barStart, TuneBar bar)
    {
        var offsets = bar.Index % 2 == 0 ? new[] { 0L, 840L } : new[] { 480L, 1200L };
        foreach (var offset in offsets)
        {
            var chord = bar.GetChordAtTick(Math.Min(offset, bar.BarTicks - 1));
            var length = Math.Min(300, bar.BarTicks - offset);
            foreach (var noteNumber in chord.PianoVoicing)
            {
                Add(notes, barStart + offset, length, noteNumber, 54, SessionConstants.PianoChannel);
            }
        }
    }

    private static void AddWaltzDrums(List<ScheduledNote> notes, long barStart, int barIndex)
    {
        var rideOffsets = new[] { 0L, 480L, 800L, 960L };
        foreach (var offset in rideOffsets)
        {
            Add(notes, barStart + offset, 55, 51, offset == 0 ? (byte)68 : (byte)58, SessionConstants.DrumsChannel);
        }
        Add(notes, barStart + 960, 55, 44, 62, SessionConstants.DrumsChannel);
        if (barIndex % 8 == 7)
        {
            Add(notes, barStart + 1280, 55, 38, 45, SessionConstants.DrumsChannel);
        }
    }

    private static void AddBass(List<ScheduledNote> notes, long barStart, ChordSpec chord)
    {
        Add(notes, barStart, 840, chord.BassRoot, 78, SessionConstants.BassChannel);
        Add(notes, barStart + 2 * SessionConstants.Ppq, 840, chord.BassFifth, 72, SessionConstants.BassChannel);
    }

    private static void AddPiano(List<ScheduledNote> notes, long barStart, int barIndex, int chorusBars, ChordSpec chord)
    {
        var hits = (barIndex % 4) switch
        {
            0 => new[] { (Offset: 800L, Length: 300L, Velocity: (byte)58), (Offset: 1440L, Length: 300L, Velocity: (byte)52) },
            1 => new[] { (Offset: 320L, Length: 240L, Velocity: (byte)52), (Offset: 1280L, Length: 400L, Velocity: (byte)58) },
            2 => new[] { (Offset: 960L, Length: 560L, Velocity: (byte)60) },
            _ => new[]
            {
                (Offset: 480L, Length: 300L, Velocity: (byte)54),
                (Offset: 1760L, Length: barIndex == chorusBars - 1 ? 160L : 120L, Velocity: (byte)50)
            }
        };

        foreach (var hit in hits)
        {
            foreach (var noteNumber in chord.PianoVoicing)
            {
                Add(notes, barStart + hit.Offset, hit.Length, noteNumber, hit.Velocity, SessionConstants.PianoChannel);
            }
        }
    }

    private static void AddDrums(List<ScheduledNote> notes, long barStart, int barIndex)
    {
        for (var i = 0; i < RideOffsets.Length; i++)
        {
            var velocity = RideVelocities[i];
            if (barIndex % 8 == 7 && i >= 4)
            {
                velocity = (byte)Math.Min(127, velocity + 8);
            }

            Add(notes, barStart + RideOffsets[i], 55, 51, velocity, SessionConstants.DrumsChannel);
        }

        Add(notes, barStart + SessionConstants.Ppq, 55, 44, 72, SessionConstants.DrumsChannel);
        Add(notes, barStart + 3 * SessionConstants.Ppq, 55, 44, 74, SessionConstants.DrumsChannel);

        Add(notes, barStart, 55, 36, 28, SessionConstants.DrumsChannel);
        Add(notes, barStart + 2 * SessionConstants.Ppq, 55, 36, 24, SessionConstants.DrumsChannel);

        if (barIndex % 8 == 7)
        {
            Add(notes, barStart + 1680, 55, 38, 50, SessionConstants.DrumsChannel);
        }
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

public sealed record CountInPlan(IReadOnlyList<ScheduledNote> Notes, int Bars, long LengthTicks);
