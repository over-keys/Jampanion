using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

public static class EndingPlanBuilder
{
    public const int Bars = 1; // Appended after the complete selected form.
    public const long LengthTicks = (long)Bars * SessionConstants.BarTicks;

    public static long GetLengthTicks(int beatsPerBar) =>
        (long)Bars * SessionConstants.GetBarTicks(beatsPerBar);

    public static EndingPlan Build(
        ChordSpec tonicChord,
        AccompanimentStyle style = AccompanimentStyle.Swing,
        int beatsPerBar = SessionConstants.BeatsPerBar)
    {
        ArgumentNullException.ThrowIfNull(tonicChord);
        var lengthTicks = GetLengthTicks(beatsPerBar);

        var notes = new List<ScheduledNote>();
        AddBass(notes, tonicChord, lengthTicks);
        AddPiano(notes, tonicChord, lengthTicks);
        AddDrums(notes, style, lengthTicks);

        var ordered = notes
            .OrderBy(note => note.StartTick)
            .ThenBy(note => note.Channel)
            .ThenBy(note => note.NoteNumber)
            .ToArray();

        return new EndingPlan(tonicChord, ordered, lengthTicks);
    }

    private static void AddBass(List<ScheduledNote> notes, ChordSpec chord, long lengthTicks)
    {
        notes.Add(new ScheduledNote(
            0,
            lengthTicks,
            chord.BassRoot,
            68,
            SessionConstants.BassChannel));
    }

    private static void AddPiano(List<ScheduledNote> notes, ChordSpec chord, long lengthTicks)
    {
        const long start = 8;
        foreach (var noteNumber in BuildTensionTonicVoicing(chord))
        {
            notes.Add(new ScheduledNote(
                start,
                lengthTicks - start,
                noteNumber,
                58,
                SessionConstants.PianoChannel));
        }
    }

    private static byte[] BuildTensionTonicVoicing(ChordSpec chord)
    {
        return chord.PianoVoicing
            .Distinct()
            .Order()
            .ToArray();
    }

    private static void AddDrums(
        List<ScheduledNote> notes,
        AccompanimentStyle style,
        long lengthTicks)
    {
        var kickVelocity = style switch
        {
            AccompanimentStyle.JazzBallad => (byte)38,
            AccompanimentStyle.BossaNova => (byte)48,
            AccompanimentStyle.JazzWaltz => (byte)51,
            AccompanimentStyle.AfroCubanLatin => (byte)60,
            _ => (byte)56
        };
        var cymbalVelocity = style switch
        {
            AccompanimentStyle.JazzBallad => (byte)45,
            AccompanimentStyle.BossaNova => (byte)50,
            AccompanimentStyle.JazzWaltz => (byte)55,
            AccompanimentStyle.AfroCubanLatin => (byte)64,
            _ => (byte)60
        };

        notes.Add(new ScheduledNote(
            0,
            100,
            36,
            kickVelocity,
            SessionConstants.DrumsChannel));
        var cymbalNote = style == AccompanimentStyle.JazzBallad ? (byte)51 : (byte)49;
        notes.Add(new ScheduledNote(
            4,
            lengthTicks - 4,
            cymbalNote,
            cymbalVelocity,
            SessionConstants.DrumsChannel));
    }
}
