using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class EnsembleBalanceProcessor
{
    private const long CollisionWindowTicks = 24;

    public static IReadOnlyList<ScheduledNote> Apply(
        IEnumerable<ScheduledNote> source,
        IReadOnlyList<BarArrangement> arrangements,
        long barTicks = SessionConstants.BarTicks,
        bool preservePianoInSpace = false,
        bool preservePianoPresence = false)
    {
        var notes = source.OrderBy(note => note.StartTick).ThenBy(note => note.Channel).ThenBy(note => note.NoteNumber).ToList();

        notes.RemoveAll(note =>
        {
            if (preservePianoInSpace || note.Channel != SessionConstants.PianoChannel) return false;
            var bar = (int)(note.StartTick / barTicks);
            return bar >= 0 && bar < arrangements.Count && arrangements[bar].Function == PhraseFunction.Space;
        });

        ResolveLowRegisterCompetition(notes);
        ResolveForegroundHierarchy(notes, arrangements, barTicks, preservePianoPresence);

        return notes.OrderBy(note => note.StartTick).ThenBy(note => note.Channel).ThenBy(note => note.NoteNumber).ToArray();
    }

    private static void ResolveLowRegisterCompetition(List<ScheduledNote> notes)
    {
        for (var i = notes.Count - 1; i >= 0; i--)
        {
            var piano = notes[i];
            if (piano.Channel != SessionConstants.PianoChannel || piano.NoteNumber >= 55) continue;
            var conflict = notes.Any(bass => bass.Channel == SessionConstants.BassChannel &&
                bass.StartTick < piano.EndTick && bass.EndTick > piano.StartTick &&
                Math.Abs(bass.NoteNumber - piano.NoteNumber) < 7);
            if (!conflict) continue;

            var raised = (byte)(piano.NoteNumber + 12);
            var duplicate = notes.Any(other => other.Channel == SessionConstants.PianoChannel &&
                other.StartTick == piano.StartTick && other.NoteNumber == raised);
            if (raised <= 84 && !duplicate)
                notes[i] = piano with { NoteNumber = raised };
            else
                notes.RemoveAt(i);
        }
    }

    private static void ResolveForegroundHierarchy(
        List<ScheduledNote> notes,
        IReadOnlyList<BarArrangement> arrangements,
        long barTicks,
        bool preservePianoPresence)
    {
        var attackTimes = notes.Where(n => n.Channel == SessionConstants.PianoChannel)
            .Select(n => n.StartTick).Distinct().ToArray();
        foreach (var attackTime in attackTimes)
        {
            var bar = (int)(attackTime / barTicks);
            if (bar < 0 || bar >= arrangements.Count) continue;
            var pianoIndices = Enumerable.Range(0, notes.Count)
                .Where(i => notes[i].Channel == SessionConstants.PianoChannel && notes[i].StartTick == attackTime)
                .ToArray();
            var drumIndices = Enumerable.Range(0, notes.Count)
                .Where(i => IsDrumAccent(notes[i]) && Math.Abs(notes[i].StartTick - attackTime) <= CollisionWindowTicks)
                .ToArray();
            if (pianoIndices.Length == 0 || drumIndices.Length == 0) continue;

            // Latin montuno/mambo already carries its phrasing in the clave
            // cell. Do not let the generic foreground responder make one bar
            // of the two-bar piano phrase disappear under the drums.
            if (preservePianoPresence) continue;

            var role = arrangements[bar].Responder;
            if (role == ResponderRole.Piano)
            {
                foreach (var i in pianoIndices)
                    notes[i] = notes[i] with { Velocity = (byte)Math.Clamp(notes[i].Velocity + 2, 1, 127) };
                var pianoPeak = pianoIndices.Max(i => notes[i].Velocity);
                var cap = Math.Max(1, pianoPeak - 3);
                foreach (var i in drumIndices)
                    notes[i] = notes[i] with { Velocity = (byte)Math.Min(notes[i].Velocity, cap) };
            }
            else if (role == ResponderRole.Drums)
            {
                foreach (var i in drumIndices)
                    notes[i] = notes[i] with { Velocity = (byte)Math.Clamp(notes[i].Velocity + 2, 1, 127) };
                var drumPeak = drumIndices.Max(i => notes[i].Velocity);
                var cap = Math.Max(1, drumPeak - 3);
                foreach (var i in pianoIndices)
                    notes[i] = notes[i] with { Velocity = (byte)Math.Min(notes[i].Velocity, cap) };
            }
            else
            {
                // Structural bars have no foreground soloist. Keep simultaneous
                // punctuation modest instead of creating an accidental climax.
                foreach (var i in pianoIndices)
                    notes[i] = notes[i] with { Velocity = (byte)Math.Max(1, notes[i].Velocity - 3) };
                foreach (var i in drumIndices)
                    notes[i] = notes[i] with { Velocity = (byte)Math.Max(1, notes[i].Velocity - 3) };
            }
        }
    }

    internal static bool IsDrumAccent(ScheduledNote note)
    {
        if (note.Channel != SessionConstants.DrumsChannel) return false;
        return note.NoteNumber switch
        {
            36 => note.Velocity >= 38,
            37 or 38 or 45 or 47 or 49 or 50 or 56 or 65 or 66 => true,
            _ => false
        };
    }
}
