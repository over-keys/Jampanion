using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

internal static class LatinPianoMontunoGenerator
{
    // Every texture preserves a 2-3 son-clave phrase: the two-side is more
    // on-beat, while the three-side answers off-beat. The opening and first solo
    // use ponchando before octave/arpeggio guajeos enter at the montuno stage.
    private static readonly long[][][] OpeningSentences =
    [
        [[0, 960], [240, 1200], [0, 1440], [240, 720, 1680]],
        [[0, 1440], [240, 1200], [480, 1440], [240, 1200]],
        [[0, 960], [240, 720], [0, 1440], [240, 1200, 1680]]
    ];

    private static readonly long[][][] PonchandoSentences =
    [
        [[0, 480, 960], [240, 720, 1200], [0, 960, 1440], [240, 720, 1200, 1680]],
        [[0, 960, 1440], [240, 720, 1680], [0, 480, 1440], [240, 720, 1200]],
        [[0, 480, 1440], [240, 720, 1200], [0, 960, 1440], [240, 1200, 1680]]
    ];

    private static readonly long[][][] MontunoSentences =
    [
        // The first full guajeo keeps the 2-side grounded on beat 1 and 2,
        // then answers with the offbeat cell instead of filling four quarter
        // notes in every bar.
        [[0, 480, 960, 1200, 1680], [240, 720, 1200, 1680], [0, 480, 960, 1200, 1680], [240, 720, 1200, 1680]],
        // A shifted answer places the first response on the upbeat while the
        // second bar retains the characteristic offbeat continuation.
        [[0, 240, 480, 960, 1200, 1680], [240, 720, 1200, 1680], [0, 480, 720, 960, 1200, 1680], [240, 720, 1200, 1680]],
        // The denser sentence is still a syncopated cell: extra attacks are
        // added around the clave, not as an even quarter-note grid.
        [[0, 240, 480, 960, 1200, 1680], [240, 720, 1200, 1680], [0, 480, 960, 1200, 1680], [240, 720, 1200, 1680]]
    ];

    private static readonly long[][][] MamboSentences =
    [
        // Mambo intensifies the montuno with a syncopated two-bar cell. The
        // 2-side has a downbeat anchor and offbeat replies; it never becomes a
        // flat 1-2-3-4 quarter-note ostinato.
        [
            [0, 480, 960, 1200, 1440, 1680],
            [240, 720, 1200, 1680],
            [0, 480, 960, 1200, 1440, 1680],
            [240, 720, 1200, 1680]
        ],
        [
            [0, 240, 480, 960, 1200, 1440, 1680],
            [240, 720, 1200, 1680],
            [0, 480, 720, 960, 1200, 1440, 1680],
            [240, 720, 1200, 1680]
        ],
        [
            [0, 240, 480, 960, 1200, 1440, 1680],
            [240, 720, 1200, 1680],
            [0, 480, 720, 960, 1200, 1440, 1680],
            [240, 720, 1200, 1680]
        ]
    ];

    public static PianoGenerationResult Generate(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<byte>? previousVoicing,
        int previousCellIndex,
        int seed,
        LatinChorusStage stage,
        PerformanceGuidance? performanceGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(followingChord);
        ArgumentNullException.ThrowIfNull(arrangements);
        if (bars.Count != arrangements.Count) throw new ArgumentException("Bars and arrangements must have the same length.");

        if (stage is LatinChorusStage.Montuno or LatinChorusStage.Mambo)
        {
            return GenerateTemplateMontuno(
                bars,
                followingChord,
                arrangements,
                previousVoicing,
                previousCellIndex,
                seed,
                stage,
                performanceGuidance);
        }

        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var notes = new List<ScheduledNote>(bars.Count * 20);
        var cells = new int[bars.Count];
        IReadOnlyList<byte> lastVoicing = previousVoicing ?? Array.Empty<byte>();
        byte? lastRenderedTop = lastVoicing.Count > 0 ? lastVoicing.Max() : null;
        var (sentence, sentenceIndex) = SelectSentence(stage, seed, previousCellIndex);

        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var nextBarChord = barIndex + 1 < bars.Count ? bars[barIndex + 1].Chord : followingChord;
            var offsets = BuildOffsets(bar, sentence[barIndex % sentence.Length], arrangements[barIndex], stage, seed, barIndex);
            cells[barIndex] = 6000 + (int)stage * 100 + sentenceIndex * 10 + barIndex % 4;
            var barStart = (long)barIndex * SessionConstants.BarTicks;

            for (var hitIndex = 0; hitIndex < offsets.Count; hitIndex++)
            {
                var offset = offsets[hitIndex];
                var chord = ResolveChord(bar, nextBarChord, offset);
                chord = ChordFactory.ApplyMinorTargetTensions(
                    chord,
                    ChordFactory.GetFollowingChord(bar, offset, nextBarChord));
                var voicing = BuildLatinVoicing(chord, lastVoicing, seed, barIndex, hitIndex);
                var rendered = ShapeMontunoVoicing(
                    voicing, offset, hitIndex, barIndex % 2, sentenceIndex, stage, lastRenderedTop);
                rendered = AddExplicitColorInside(rendered, chord, hitIndex);
                var start = barStart + offset + 2 + (long)Math.Round(DeterministicNoise.Unit(seed, barIndex, hitIndex, 6203) * 3);
                if (start >= segmentLength) continue;
                var nextOffset = hitIndex + 1 < offsets.Count ? offsets[hitIndex + 1] : SessionConstants.BarTicks;
                var duration = GetDuration(stage, offset, nextOffset, seed, barIndex, hitIndex);
                duration = Math.Min(duration, segmentLength - start);
                var stageLift = stage switch
                {
                    LatinChorusStage.Opening or LatinChorusStage.HeadOut => -4,
                    LatinChorusStage.Ponchando => -2,
                    // Keep every mambo event; lower its dynamic floor instead
                    // of thinning the interlocking rhythm.
                    LatinChorusStage.Mambo => 1,
                    _ => 0
                };
                var interactionLift = guidance.HighStage ? 3 : 0;
                var velocity = 61 + stageLift + interactionLift + arrangements[barIndex].DynamicLift / 2 + (offset % SessionConstants.Ppq == 0 ? -2 : 2);
                var renderedVelocity = (byte)Math.Clamp(velocity, 48, 82);
                foreach (var noteNumber in rendered)
                {
                    notes.Add(new ScheduledNote(
                        start,
                        duration,
                        noteNumber,
                        rendered.Count == 2 && noteNumber == rendered[^1]
                            ? (byte)Math.Max(45, renderedVelocity - 4)
                            : renderedVelocity,
                        SessionConstants.PianoChannel));
                }
                lastVoicing = voicing;
                lastRenderedTop = rendered.Max();
            }
        }

        return new PianoGenerationResult(ScheduledNoteOverlapGuard.TrimSamePitchOverlaps(notes), lastVoicing, cells[^1], cells);
    }

    private static PianoGenerationResult GenerateTemplateMontuno(
        IReadOnlyList<TuneBar> bars,
        ChordSpec followingChord,
        IReadOnlyList<BarArrangement> arrangements,
        IReadOnlyList<byte>? previousVoicing,
        int previousCellIndex,
        int seed,
        LatinChorusStage stage,
        PerformanceGuidance? performanceGuidance)
    {
        var guidance = performanceGuidance ?? PerformanceGuidance.Neutral;
        var events = LatinMontunoTemplateEngine.Build(bars, followingChord, stage, seed, previousCellIndex);
        var segmentLength = (long)bars.Count * SessionConstants.BarTicks;
        var notes = new List<ScheduledNote>(events.Count * 4);
        var cells = new int[bars.Count];
        IReadOnlyList<byte> previous = previousVoicing ?? Array.Empty<byte>();

        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            if (item.Chord.IsNoChord)
            {
                continue;
            }

            var rendered = RenderTemplateEvent(item, previous, seed, index);
            if (rendered.Count == 0)
            {
                continue;
            }

            var start = item.Tick + 2 + (long)Math.Round(DeterministicNoise.Unit(seed, index, 6311) * 3);
            if (start >= segmentLength)
            {
                continue;
            }

            var nextStart = index + 1 < events.Count ? events[index + 1].Tick : segmentLength;
            var duration = GetTemplateDuration(stage, item, nextStart - item.Tick);
            duration = Math.Min(duration, segmentLength - start);
            var barIndex = Math.Min((int)(item.Tick / SessionConstants.BarTicks), arrangements.Count - 1);
            // Mambo keeps its full note count and octave texture. The groove
            // supplies the lift, so a lower velocity prevents a dense pattern
            // from becoming a wall of sound.
            var stageLift = stage == LatinChorusStage.Mambo ? 1 : 0;
            var interactionLift = guidance.HighStage ? 3 : 0;
            // Montuno and mambo are driven by the interlocking rhythm and
            // octave/chord texture, not by a pronounced beat-by-beat accent.
            // Keep both bars of the two-bar phrase on the same dynamic floor.
            // The clave and event density provide the phrase shape; velocity
            // must not make the answering bar sound like an afterthought.
            var phraseBarIndex = barIndex - barIndex % 2;
            var phraseLift = Math.Clamp(arrangements[phraseBarIndex].DynamicLift / 4, -1, 1);
            var baseVelocity = 64 + stageLift + interactionLift + phraseLift;
            var velocity = (byte)Math.Clamp(
                baseVelocity + (item.IsAnticipation ? 1 : 0),
                48,
                88);
            foreach (var noteNumber in rendered)
            {
                var noteVelocity = item.IsOuter && rendered.Count > 2 && noteNumber == rendered[^1]
                    ? (byte)Math.Min(92, velocity + 1)
                    : velocity;
                notes.Add(new ScheduledNote(
                    start,
                    duration,
                    noteNumber,
                    noteVelocity,
                    SessionConstants.PianoChannel));
            }

            previous = rendered;
            cells[barIndex] = 6200 + (int)stage * 100 + (int)item.Template * 10 + item.CycleEventIndex % 10;
        }

        for (var barIndex = 0; barIndex < cells.Length; barIndex++)
        {
            if (cells[barIndex] == 0)
            {
                cells[barIndex] = 6200 + (int)stage * 100 + barIndex % 2;
            }
        }

        return new PianoGenerationResult(
            ScheduledNoteOverlapGuard.TrimSamePitchOverlaps(notes),
            previous,
            cells[^1],
            cells);
    }

    private static IReadOnlyList<byte> RenderTemplateEvent(
        LatinMontunoEvent item,
        IReadOnlyList<byte> previous,
        int seed,
        int eventIndex)
    {
        if (item.OuterDegree is int outerDegree)
        {
            var pitchClass = LatinMontunoTemplateEngine.PitchClass(item.Chord, outerDegree, item.PitchRootPitchClass);
            var outer = ChooseOuterOctave(pitchClass, previous, seed, eventIndex);
            if (item.Texture == LatinMontunoTexture.OctaveUnison)
            {
                return [outer, (byte)(outer + 12)];
            }

            var inner = ChooseInnerNotes(item.Chord, item.InnerDegrees, previous, outer, item.PitchRootPitchClass, seed, eventIndex);
            return new[] { outer }.Concat(inner).Append((byte)(outer + 12)).Distinct().Order().ToArray();
        }

        if (item.Texture == LatinMontunoTexture.UpperStructure)
        {
            return ChooseInnerNotes(item.Chord, item.InnerDegrees, previous, null, item.PitchRootPitchClass, seed, eventIndex);
        }

        return ChooseInnerNotes(item.Chord, item.InnerDegrees, previous, null, item.PitchRootPitchClass, seed, eventIndex);
    }

    private static byte ChooseOuterOctave(int pitchClass, IReadOnlyList<byte> previous, int seed, int eventIndex)
    {
        var candidates = Enumerable.Range(48, 25)
            .Where(note => note % 12 == pitchClass && note + 12 <= 84)
            .Select(note => (byte)note)
            .ToArray();
        if (candidates.Length == 0)
        {
            return 60;
        }

        var previousTop = previous.Count > 0 ? previous[^1] : (byte)72;
        return candidates
            .OrderBy(note => Math.Abs(note + 12 - previousTop))
            .ThenBy(note => Math.Abs(note - 60))
            .ThenBy(_ => DeterministicNoise.Unit(seed, eventIndex, 6317))
            .First();
    }

    private static IReadOnlyList<byte> ChooseInnerNotes(
        ChordSpec chord,
        IReadOnlyList<int> degrees,
        IReadOnlyList<byte> previous,
        byte? outer,
        int? rootPitchClass,
        int seed,
        int eventIndex)
    {
        var selected = new List<byte>(degrees.Count);
        foreach (var degree in degrees)
        {
            var pitchClass = LatinMontunoTemplateEngine.PitchClass(chord, degree, rootPitchClass);
            var lower = outer is byte outerNote ? Math.Max(54, outerNote + 3) : 54;
            var upper = outer is byte outerNote2 ? Math.Min(78, outerNote2 + 9) : 78;
            var candidates = Enumerable.Range(lower, Math.Max(1, upper - lower + 1))
                .Where(note => note % 12 == pitchClass && (outer is not byte outerValue || note != outerValue))
                .Select(note => (byte)note)
                .Where(note => selected.All(prior => Math.Abs(note - prior) >= 3))
                .ToArray();
            if (candidates.Length == 0 && degrees.Count > 2)
            {
                // An explicitly written tension may sit next to a chord tone
                // in a compact montuno voicing. Keep the tension rather than
                // silently dropping it because of the generic spacing guard.
                candidates = Enumerable.Range(lower, Math.Max(1, upper - lower + 1))
                    .Where(note => note % 12 == pitchClass && (outer is not byte outerValue || note != outerValue))
                    .Select(note => (byte)note)
                    .ToArray();
            }
            if (candidates.Length == 0)
            {
                continue;
            }

            var prior = selected.Count > 0
                ? selected[^1]
                : previous.Count > 0 ? previous[^1] : (byte)64;
            selected.Add(candidates
                .OrderBy(note => Math.Abs(note - prior))
                .ThenBy(note => Math.Abs(note - 65))
                .ThenBy(_ => DeterministicNoise.Unit(seed, eventIndex, eventIndex + selected.Count, 6319))
                .First());
        }

        return selected.Distinct().Order().ToArray();
    }

    private static long GetTemplateDuration(LatinChorusStage stage, LatinMontunoEvent item, long available)
    {
        var maximum = stage == LatinChorusStage.Mambo ? 300 : 360;
        if (item.TieAcrossBar)
        {
            return Math.Clamp(available - 4, 300, 520);
        }

        return Math.Clamp(available - 42, 150, maximum);
    }

    private static (IReadOnlyList<long>[] Sentence, int Index) SelectSentence(
        LatinChorusStage stage,
        int seed,
        int previousCellIndex)
    {
        var source = stage switch
        {
            LatinChorusStage.Opening or LatinChorusStage.HeadOut => OpeningSentences,
            LatinChorusStage.Ponchando => PonchandoSentences,
            LatinChorusStage.Mambo => MamboSentences,
            _ => MontunoSentences
        };
        var index = (int)(DeterministicNoise.Unit(seed, (int)stage, 6197) * source.Length) % source.Length;
        var stageBase = 6000 + (int)stage * 100;
        var previousSentence = previousCellIndex >= stageBase && previousCellIndex < stageBase + 100
            ? (previousCellIndex - stageBase) / 10
            : -1;
        if (source.Length > 1 && index == previousSentence)
        {
            index = (index + 1) % source.Length;
        }

        return (source[index], index);
    }

    private static long GetDuration(
        LatinChorusStage stage,
        long offset,
        long nextOffset,
        int seed,
        int barIndex,
        int hitIndex)
    {
        var available = nextOffset - offset;
        if (offset >= 1680)
        {
            available = Math.Max(available, 420);
        }

        var opening = stage is LatinChorusStage.Opening or LatinChorusStage.HeadOut;
        var maximum = stage switch
        {
            LatinChorusStage.Opening or LatinChorusStage.HeadOut => 680,
            LatinChorusStage.Ponchando => 390,
            LatinChorusStage.Montuno => 280,
            _ => 230
        };
        var duration = Math.Min(maximum, Math.Max(130, available - 58));
        if (opening && DeterministicNoise.Unit(seed, barIndex, hitIndex, 6211) > 0.88)
        {
            duration = Math.Min(duration, 320);
        }

        return duration;
    }

    private static IReadOnlyList<long> BuildOffsets(
        TuneBar bar,
        IReadOnlyList<long> baseOffsets,
        BarArrangement arrangement,
        LatinChorusStage stage,
        int seed,
        int barIndex)
    {
        var offsets = baseOffsets.ToList();
        foreach (var change in bar.ChordChanges.Skip(1))
        {
            var changeTick = (long)change.StartBeat * SessionConstants.Ppq;
            if (!offsets.Any(offset => offset >= changeTick && offset - changeTick <= SessionConstants.Ppq / 2))
            {
                offsets.Add(changeTick);
            }
        }

        if (stage is LatinChorusStage.Opening or LatinChorusStage.HeadOut or LatinChorusStage.Ponchando)
        {
            var structural = bar.ChordChanges.Skip(1)
                .Select(change => (long)change.StartBeat * SessionConstants.Ppq)
                .ToHashSet();
            if (arrangement.Function == PhraseFunction.Space && offsets.Count > 2)
            {
                var removable = offsets.Where(offset => !structural.Contains(offset)).ToArray();
                if (removable.Length > 0)
                {
                    offsets.Remove(removable
                        .OrderBy(offset => DeterministicNoise.Unit(seed, barIndex, (int)offset, 6205))
                        .First());
                }
            }
        }
        var maximumOffsets = stage switch
        {
            LatinChorusStage.Opening or LatinChorusStage.HeadOut => 4,
            LatinChorusStage.Ponchando => 5,
            LatinChorusStage.Mambo => 7,
            _ => 6
        };
        return offsets.Distinct().Order().Take(maximumOffsets).ToArray();
    }

    private static ChordSpec ResolveChord(TuneBar bar, ChordSpec nextBarChord, long offset)
    {
        if (offset >= 1680)
        {
            return nextBarChord;
        }

        var anticipated = bar.ChordChanges.FirstOrDefault(change =>
            change.StartBeat * SessionConstants.Ppq > offset &&
            change.StartBeat * SessionConstants.Ppq - offset <= SessionConstants.Ppq / 2);
        return anticipated?.Chord ?? bar.GetChordAtTick(Math.Min(offset, bar.BarTicks - 1));
    }

    private static IReadOnlyList<byte> BuildLatinVoicing(
        ChordSpec chord,
        IReadOnlyList<byte> previous,
        int seed,
        int barIndex,
        int hitIndex)
    {
        var root = chord.RootPitchClass;
        var quality = chord.Symbol.ToLowerInvariant();
        var allowSixth = (quality.Contains('6') && !quality.Contains("13", StringComparison.Ordinal)) ||
            quality.Contains("dim", StringComparison.Ordinal);
        var pitchClasses = chord.BassPitchClasses
            .Where(pitchClass =>
            {
                var interval = Mod12(pitchClass - root);
                return interval is 0 or 3 or 4 or 5 or 6 or 7 or 8 or 10 or 11 ||
                    allowSixth && interval == 9;
            })
            .Distinct()
            .ToArray();
        if (pitchClasses.Length < 3)
        {
            pitchClasses = chord.BassPitchClasses.Distinct().Take(3).ToArray();
        }

        return PianoVoicingVocabulary.Choose(
            pitchClasses,
            previous,
            voiceCount: Math.Min(3, pitchClasses.Length),
            lower: 50,
            upper: 78,
            targetCenter: 64.0,
            PianoVoicingStyle.Latin,
            chord.RootPitchClass,
            seed,
            barIndex,
            hitIndex);
    }

    private static IReadOnlyList<byte> ShapeMontunoVoicing(
        IReadOnlyList<byte> voicing,
        long offset,
        int hitIndex,
        int claveSide,
        int sentenceIndex,
        LatinChorusStage stage,
        byte? previousTop)
    {
        if (stage is LatinChorusStage.Opening or LatinChorusStage.HeadOut or LatinChorusStage.Ponchando || voicing.Count < 3)
        {
            return voicing;
        }

        // The two-bar cell alternates an octave-doubled chord-tone line with
        // compact chord punctuation. Mambo chooses the octave on selected
        // syncopations and phrase anchors, rather than on every hit or on an
        // arbitrary alternating index.
        var localOffset = offset % SessionConstants.BarTicks;
        var isMamboOctaveAttack = stage == LatinChorusStage.Mambo &&
            IsMamboOctaveAttack(localOffset, claveSide, sentenceIndex);
        var chordPunctuation = stage == LatinChorusStage.Mambo
            ? !isMamboOctaveAttack
            : hitIndex % 2 == 1;
        if (chordPunctuation)
        {
            return voicing;
        }

        var contours = claveSide == 0
            ? new[]
            {
                new[] { 0, 1, 2, 1, 0, 2, 1 },
                new[] { 1, 2, 0, 2, 1, 0, 2 },
                new[] { 2, 1, 0, 1, 2, 0, 1 }
            }
            : new[]
            {
                new[] { 1, 2, 1, 0, 2, 0, 1 },
                new[] { 2, 0, 2, 1, 0, 1, 2 },
                new[] { 0, 2, 1, 2, 0, 1, 0 }
            };
        var contour = contours[sentenceIndex % contours.Length];
        var contourStep = stage == LatinChorusStage.Mambo
            ? MamboContourStep(localOffset, claveSide)
            : hitIndex % contour.Length;
        var pitchClass = voicing[contour[contourStep % contour.Length] % voicing.Count] % 12;
        var preferredTop = previousTop ?? 72;
        var anchor = Enumerable.Range(50, 19)
            .Where(note => note % 12 == pitchClass && note + 12 <= 82)
            .OrderBy(note => Math.Abs(note + 12 - preferredTop))
            .ThenBy(note => Math.Abs(note + 12 - 72))
            .First();
        return new[] { (byte)anchor, (byte)(anchor + 12) };
    }

    private static int MamboContourStep(long localOffset, int claveSide) =>
        claveSide == 0
            ? localOffset switch
            {
                1200 => 2,
                1680 => 1,
                _ => 0
            }
            : localOffset switch
            {
                240 => 1,
                720 => 2,
                1200 => 0,
                1680 => 1,
                _ => 0
            };

    private static bool IsMamboOctaveAttack(long localOffset, int claveSide, int sentenceIndex)
    {
        if (claveSide == 0)
        {
            return localOffset is 720 or 1200 or 1680 ||
                sentenceIndex == 2 && localOffset == 0;
        }

        return localOffset is 240 or 720 or 1200 or 1680;
    }

    private static IReadOnlyList<byte> AddExplicitColorInside(
        IReadOnlyList<byte> rendered,
        ChordSpec chord,
        int hitIndex)
    {
        if (rendered.Count < 3 || hitIndex % 4 != 1)
        {
            return rendered;
        }

        var quality = chord.Symbol.ToLowerInvariant();
        var interval = quality.Contains("b9", StringComparison.Ordinal) ? 1 :
            quality.Contains("#9", StringComparison.Ordinal) ? 3 :
            quality.Contains("#11", StringComparison.Ordinal) ? 6 :
            quality.Contains("b13", StringComparison.Ordinal) ? 8 :
            quality.Contains("13", StringComparison.Ordinal) ? 9 :
            quality.Contains("11", StringComparison.Ordinal) ? 5 :
            quality.Contains("9", StringComparison.Ordinal) ? 2 : -1;
        if (interval < 0)
        {
            return rendered;
        }

        var colorPitchClass = Mod12(chord.RootPitchClass + interval);
        if (!chord.PianoPitchClasses.Contains(colorPitchClass)) return rendered;

        var top = rendered[^1];
        var inner = Enumerable.Range(48, Math.Max(0, top - 48))
            .Where(note => note % 12 == colorPitchClass)
            .OrderBy(note => Math.Abs(note - rendered[1]))
            .FirstOrDefault(-1);
        if (inner < 0) return rendered;

        return new[] { rendered[0], (byte)inner, top }.Distinct().Order().ToArray();
    }

    private static int Mod12(int value) => (value % 12 + 12) % 12;
}
