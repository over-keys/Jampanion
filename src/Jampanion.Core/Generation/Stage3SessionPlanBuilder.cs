using Jampanion.Core.Analysis;
using Jampanion.Core.Music;
using Jampanion.Core.Playback;

namespace Jampanion.Core.Generation;

public static class Stage3SessionPlanBuilder
{
    public const int PreEndingBarCount = SessionConstants.BarsPerSegment;

    public static Stage3SegmentPlan BuildSegment(
        TuneForm form,
        int segmentIndex,
        RhythmFeel feel,
        int chorus,
        ArrangementContext inputContext,
        int sessionSeed = 0,
        PerformanceGuidance? performanceGuidance = null,
        bool isHeadOut = false,
        int tempoBpm = 0)
    {
        ArgumentNullException.ThrowIfNull(form);
        return BuildSegmentFromBars(
            form,
            form.Bars,
            form.Bars.Count,
            form.SegmentCount,
            segmentIndex,
            feel,
            chorus,
            inputContext,
            sessionSeed,
            performanceGuidance ?? PerformanceGuidance.Neutral,
            finalFollowingChord: null,
            isEndingForm: false,
            isHeadOut: isHeadOut,
            tempoBpm: ResolveTempo(form, tempoBpm));
    }

    public static Stage3SegmentPlan BuildEndingLeadInSegment(
        TuneForm form,
        int segmentIndex,
        RhythmFeel feel,
        int chorus,
        ArrangementContext inputContext,
        int sessionSeed = 0,
        PerformanceGuidance? performanceGuidance = null,
        int tempoBpm = 0)
    {
        ArgumentNullException.ThrowIfNull(form);
        return BuildSegmentFromBars(
            form,
            form.EndingFormBars,
            form.EndingLeadInBarCount,
            form.EndingLeadInSegmentCount,
            segmentIndex,
            feel,
            chorus,
            inputContext,
            sessionSeed,
            performanceGuidance ?? PerformanceGuidance.Neutral,
            form.TonicChord,
            isEndingForm: true,
            isHeadOut: false,
            tempoBpm: ResolveTempo(form, tempoBpm));
    }

    public static Stage3SegmentPlan BuildPreEndingSegment(
        TuneForm form,
        RhythmFeel feel,
        int chorus,
        ArrangementContext inputContext,
        int sessionSeed = 0,
        PerformanceGuidance? performanceGuidance = null,
        int tempoBpm = 0)
    {
        ArgumentNullException.ThrowIfNull(form);
        return BuildEndingLeadInSegment(
            form,
            form.EndingLeadInSegmentCount - 1,
            feel,
            chorus,
            inputContext,
            sessionSeed,
            performanceGuidance,
            tempoBpm);
    }

    private static Stage3SegmentPlan BuildSegmentFromBars(
        TuneForm form,
        IReadOnlyList<TuneBar> sourceBars,
        int playableBarCount,
        int segmentCount,
        int segmentIndex,
        RhythmFeel feel,
        int chorus,
        ArrangementContext inputContext,
        int sessionSeed,
        PerformanceGuidance performanceGuidance,
        ChordSpec? finalFollowingChord,
        bool isEndingForm,
        bool isHeadOut,
        int tempoBpm)
    {
        ArgumentNullException.ThrowIfNull(inputContext);

        if (segmentIndex is < 0 || segmentIndex >= segmentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        }

        var startBar = segmentIndex * SessionConstants.BarsPerSegment;
        var barCount = Math.Min(SessionConstants.BarsPerSegment, playableBarCount - startBar);
        var endBarExclusive = startBar + barCount;
        var followingChord = endBarExclusive < sourceBars.Count
            ? sourceBars[endBarExclusive].ChordChanges[0].Chord
            : finalFollowingChord ?? sourceBars[0].ChordChanges[0].Chord;

        return BuildRange(
            form,
            sourceBars,
            segmentIndex,
            startBar,
            barCount,
            followingChord,
            feel,
            chorus,
            inputContext,
            sessionSeed,
            performanceGuidance,
            playableBarCount,
            isEndingForm,
            isHeadOut,
            tempoBpm);
    }

    private static Stage3SegmentPlan BuildRange(
        TuneForm form,
        IReadOnlyList<TuneBar> sourceBars,
        int segmentIndex,
        int startBar,
        int barCount,
        ChordSpec followingChord,
        RhythmFeel feel,
        int chorus,
        ArrangementContext inputContext,
        int sessionSeed,
        PerformanceGuidance performanceGuidance,
        int playableBarCount,
        bool isEndingForm,
        bool isHeadOut,
        int tempoBpm)
    {
        if (chorus < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chorus));
        }

        // A main-form HEAD OUT should retain the warmer first-solo texture.
        // Only a separate Coda/ending form is a genuine HeadOut arrangement.
        var arrangementChorus = isHeadOut ? 2 : chorus;

        var bars = Enumerable.Range(startBar, barCount)
            .Select(index => sourceBars[index])
            .ToArray();
        var timeFeel = TimeFeelProfile.Resolve(form.AccompanimentStyle, tempoBpm);
        var planningFeel = form.AccompanimentStyle == AccompanimentStyle.Swing
            ? feel
            : RhythmFeel.TwoBeat;
        var seed = unchecked(
            sessionSeed * 486_187_739 +
            chorus * 1_009 +
            segmentIndex * 97 +
            (int)planningFeel * 7_919);
        var endBarExclusive = startBar + barCount;
        var endingBoundary = endBarExclusive >= playableBarCount
            ? BoundaryStrength.Chorus
            : sourceBars[endBarExclusive - 1].Section != sourceBars[endBarExclusive].Section
                ? BoundaryStrength.Section
                : BoundaryStrength.Phrase;
        // Accompaniment follows the form, not the most recent MIDI phrase. MIDI
        // analysis is reserved for HEAD OUT detection in the UI layer. The only
        // accepted override is an explicit structural high-stage request (used by
        // the non-automatic feel controls); all live input fields are discarded.
        var styleGuidance = ResolveStructuralStyleGuidance(
            form.AccompanimentStyle,
            planningFeel,
            arrangementChorus,
            startBar,
            playableBarCount,
            isEndingForm,
            performanceGuidance);
        var arrangements = ApplyTransitionLeadIn(
            StyleRhythmSectionPlanner.Plan(
                form.AccompanimentStyle,
                Stage3ArrangementPlanner.Plan(seed, planningFeel, barCount, styleGuidance, endingBoundary),
                arrangementChorus,
                isEndingForm),
            endBarExclusive,
            playableBarCount,
            isEndingForm,
            isHeadOut,
            startBar,
            chorus >= 2);
        // The bass keeps a dedicated, form-derived pulse plan. It never follows
        // piano/drum foreground roles, so a conversational space cannot remove a
        // walking downbeat or a written harmony arrival.
        var bassArrangements = StyleRhythmSectionPlanner.Plan(
            form.AccompanimentStyle,
            Stage3ArrangementPlanner.Plan(
                seed,
                planningFeel,
                barCount,
                PerformanceGuidance.Neutral,
                endingBoundary),
            arrangementChorus,
            isEndingForm);

        // Bass keeps every structural downbeat through the handoff. It receives
        // the marker only so pickup dynamics can relax slightly; no bass events
        // are removed here.
        bassArrangements = bassArrangements
            .Select((bar, index) => arrangements[index].IsTransitionLeadIn
                ? bar with { IsTransitionLeadIn = true }
                : bar)
            .ToArray();

        BassGenerationResult bass;
        PianoGenerationResult piano;
        DrumGenerationResult drums;
        var precedingTwoBeatTransitionRun = 0;
        if (form.AccompanimentStyle == AccompanimentStyle.Swing && startBar > 0)
        {
            // The bass generator runs one four-bar segment at a time. Carry the
            // actual two-beat change run into the next segment so the alternating
            // approach rule cannot restart at an arbitrary segment boundary.
            for (var priorBar = startBar - 1; priorBar >= 0; priorBar--)
            {
                var beatThreeCurrent = sourceBars[priorBar].GetChordAtBeat(3);
                var beatThreeNext = sourceBars[priorBar + 1].GetChordAtBeat(0);
                if (beatThreeCurrent == beatThreeNext) break;
                precedingTwoBeatTransitionRun++;

                var beatOneCurrent = sourceBars[priorBar].GetChordAtBeat(1);
                var beatOneNext = sourceBars[priorBar].GetChordAtBeat(2);
                if (beatOneCurrent == beatOneNext) break;
                precedingTwoBeatTransitionRun++;
            }
        }
        if (form.AccompanimentStyle == AccompanimentStyle.BossaNova)
        {
            var bossaStage = BossaChorusArc.Resolve(arrangementChorus, isEndingForm);
            var bossaBassStage = bossaStage;
            bass = BossaBassLineGenerator.Generate(
                bars,
                followingChord,
                bassArrangements,
                inputContext.PreviousBassNote,
                inputContext.RecentBassNotes,
                inputContext.PreviousBassDirection,
                inputContext.PreviousBassDirectionRun,
                seed + 11,
                bossaBassStage,
                styleGuidance);
            piano = BossaPianoCompingGenerator.Generate(
                bars,
                followingChord,
                arrangements,
                inputContext.PreviousPianoVoicing,
                inputContext.PreviousPianoCellIndex,
                seed + 23,
                bossaStage,
                styleGuidance);
            drums = BossaDrumGrooveGenerator.Generate(
                arrangements,
                inputContext.PreviousDrumPatternIndex,
                inputContext.PreviousFillVariant,
                inputContext.PreviousSectionEndedWithFill,
                inputContext.PreviousDrumCompPatternIndex,
                seed + 37,
                bossaStage,
                styleGuidance);
        }
        else if (form.AccompanimentStyle == AccompanimentStyle.AfroCubanLatin)
        {
            var latinStage = LatinChorusArc.Resolve(arrangementChorus, isEndingForm);
            var latinBassStage = latinStage;
            bass = LatinBassLineGenerator.Generate(
                bars,
                followingChord,
                bassArrangements,
                inputContext.PreviousBassNote,
                inputContext.RecentBassNotes,
                inputContext.PreviousBassDirection,
                inputContext.PreviousBassDirectionRun,
                seed + 11,
                latinBassStage,
                styleGuidance);
            piano = LatinPianoMontunoGenerator.Generate(
                bars,
                followingChord,
                arrangements,
                inputContext.PreviousPianoVoicing,
                inputContext.PreviousPianoCellIndex,
                seed + 23,
                latinStage,
                styleGuidance);
            drums = LatinDrumGrooveGenerator.Generate(
                arrangements,
                inputContext.PreviousDrumPatternIndex,
                inputContext.PreviousFillVariant,
                inputContext.PreviousSectionEndedWithFill,
                inputContext.PreviousDrumCompPatternIndex,
                seed + 37,
                latinStage,
                styleGuidance);
        }
        else if (form.AccompanimentStyle == AccompanimentStyle.JazzBallad)
        {
            var balladStages = bars
                .Select((_, localBar) => BalladChorusArc.Resolve(
                    arrangementChorus,
                    startBar + localBar,
                    Math.Max(1, playableBarCount / 2),
                    isEndingForm))
                .ToArray();
            bass = BalladBassLineGenerator.Generate(
                bars,
                followingChord,
                bassArrangements,
                balladStages,
                inputContext.PreviousBassNote,
                inputContext.RecentBassNotes,
                inputContext.PreviousBassDirection,
                inputContext.PreviousBassDirectionRun,
                seed + 11,
                styleGuidance,
                prepareNextFourFeel: !isEndingForm
                    && !isHeadOut
                    && arrangementChorus == 2
                    && endBarExclusive >= playableBarCount,
                timeFeel: timeFeel);
            piano = BalladPianoCompingGenerator.Generate(
                bars,
                followingChord,
                arrangements,
                balladStages,
                inputContext.PreviousPianoVoicing,
                inputContext.PreviousPianoCellIndex,
                seed + 23,
                styleGuidance,
                previousSegmentEndedOnFourAnd: inputContext.PreviousPianoEndedOnFourAnd,
                timeFeel: timeFeel);
            drums = BalladDrumGrooveGenerator.Generate(
                arrangements,
                balladStages,
                inputContext.PreviousDrumPatternIndex,
                inputContext.PreviousFillVariant,
                inputContext.PreviousSectionEndedWithFill,
                inputContext.PreviousRidePhraseIndex,
                inputContext.PreviousDrumCompPatternIndex,
                seed + 37,
                styleGuidance,
                timeFeel);
        }
        else if (form.AccompanimentStyle == AccompanimentStyle.JazzWaltz)
        {
            var waltzStage = WaltzChorusArc.Resolve(arrangementChorus, isEndingForm);
            var waltzBassStage = waltzStage;
            var hemiolaPlan = WaltzHemiolaPlanner.Plan(
                bars,
                arrangements,
                seed + 47,
                waltzStage,
                styleGuidance);
            var prepareNextWalking = !isEndingForm
                && !isHeadOut
                && arrangementChorus == 2
                && endBarExclusive >= playableBarCount;
            var waltzBassWalkingByBar = bars
                .Select((_, localBar) => WaltzBassLineGenerator.IsWalkingAtBar(
                    waltzBassStage,
                    startBar + localBar,
                    playableBarCount,
                    prepareNextWalking && localBar == bars.Length - 1))
                .ToArray();
            bass = WaltzBassLineGenerator.Generate(
                bars,
                followingChord,
                bassArrangements,
                inputContext.PreviousBassNote,
                inputContext.RecentBassNotes,
                inputContext.PreviousBassDirection,
                inputContext.PreviousBassDirectionRun,
                seed + 11,
                waltzBassStage,
                startBar,
                playableBarCount,
                styleGuidance,
                prepareNextWalking: prepareNextWalking,
                timeFeel: timeFeel);
            piano = WaltzPianoCompingGenerator.Generate(
                bars,
                followingChord,
                arrangements,
                inputContext.PreviousPianoVoicing,
                inputContext.PreviousPianoCellIndex,
                seed + 23,
                waltzStage,
                hemiolaPlan,
                styleGuidance,
                waltzBassWalkingByBar,
                timeFeel);
            drums = WaltzDrumGrooveGenerator.Generate(
                arrangements,
                inputContext.PreviousDrumPatternIndex,
                inputContext.PreviousFillVariant,
                inputContext.PreviousSectionEndedWithFill,
                inputContext.PreviousRidePhraseIndex,
                inputContext.PreviousDrumCompPatternIndex,
                seed + 37,
                waltzStage,
                hemiolaPlan,
                styleGuidance,
                timeFeel);
        }
        else
        {
            bass = BassLineGenerator.Generate(
                bars,
                followingChord,
                feel,
                bassArrangements,
                inputContext.PreviousBassNote,
                inputContext.RecentBassNotes,
                inputContext.PreviousBassDirection,
                inputContext.PreviousBassDirectionRun,
                seed + 11,
                styleGuidance,
                prepareNextFourFeel: form.AccompanimentStyle == AccompanimentStyle.Swing
                    && !isEndingForm
                    && !isHeadOut
                    && arrangementChorus == 2
                    && feel == RhythmFeel.TwoBeat
                    && endBarExclusive >= playableBarCount,
                initialTwoBeatTransitionRun: precedingTwoBeatTransitionRun,
                firstSoloTwoBeat: form.AccompanimentStyle == AccompanimentStyle.Swing
                    && !isEndingForm
                    && arrangementChorus == 2
                    && feel == RhythmFeel.TwoBeat,
                timeFeel: timeFeel);
            piano = PianoCompingGenerator.Generate(
                bars,
                followingChord,
                feel,
                arrangements,
                inputContext.PreviousPianoVoicing,
                inputContext.PreviousPianoCellIndex,
                seed + 23,
                styleGuidance,
                // Retain the head's relaxed vocabulary into the first solo
                // chorus; the solo still has space, but should not feel like a
                // sudden drop in piano presence at the chorus boundary.
                restrainedOpening: form.AccompanimentStyle == AccompanimentStyle.Swing && arrangementChorus <= 2,
                previousSegmentEndedOnFourAnd: inputContext.PreviousPianoEndedOnFourAnd,
                timeFeel: timeFeel);
            drums = DrumGrooveGenerator.Generate(
                feel,
                arrangements,
                inputContext.PreviousDrumPatternIndex,
                inputContext.PreviousFillVariant,
                inputContext.PreviousSectionEndedWithFill,
                inputContext.PreviousRidePhraseIndex,
                inputContext.PreviousDrumCompPatternIndex,
                seed + 37,
                styleGuidance,
                timeFeel);
        }

        // All musical choices, no-chord exclusions, and note-duration boundaries
        // are resolved by the generators while they still have the harmonic and
        // rhythmic context.  The segment builder only assembles the already
        // selected notes; it must not rewrite the performance afterwards.
        IReadOnlyList<ScheduledNote> notes = bass.Notes.Concat(piano.Notes).Concat(drums.Notes).ToArray();

        var segment = new SegmentPlan(segmentIndex, planningFeel, notes, (long)barCount * form.BarTicks);
        var outputContext = new ArrangementContext(
            bass.LastNote,
            piano.LastVoicing,
            piano.LastCellIndex,
            drums.LastPatternIndex,
            drums.LastFillVariant,
            drums.SectionEndedWithFill,
            bass.RecentNotes,
            bass.LastDirection,
            bass.DirectionRun,
            drums.LastRidePhraseIndex,
            drums.LastCompPatternIndex,
            piano.EndedOnFourAnd);

        return new Stage3SegmentPlan(
            segment,
            outputContext,
            arrangements,
            piano.CellIndices,
            drums.PatternIndices,
            styleGuidance);
    }

    private static PerformanceGuidance ResolveStructuralStyleGuidance(
        AccompanimentStyle style,
        RhythmFeel feel,
        int chorus,
        int startBar,
        int formBars,
        bool isEndingForm,
        PerformanceGuidance requestedGuidance)
    {
        var progressThroughForm = Math.Clamp(
            (startBar + Math.Min(2.0, Math.Max(1, formBars - startBar))) /
            Math.Max(1, formBars),
            0,
            1);
        var opening = isEndingForm ? 0.16 : style switch
        {
            AccompanimentStyle.Swing => chorus switch
            {
                <= 1 => 0.20,
                2 => 0.42,
                3 => 0.64,
                _ => 0.84
            },
            AccompanimentStyle.BossaNova => chorus switch
            {
                <= 1 => 0.22,
                2 => 0.42,
                3 => 0.62,
                _ => 0.82
            },
            AccompanimentStyle.AfroCubanLatin => chorus switch
            {
                <= 1 => 0.22,
                2 => 0.40,
                3 => 0.62,
                _ => 0.84
            },
            AccompanimentStyle.JazzWaltz => chorus switch
            {
                <= 1 => 0.20,
                2 => 0.39,
                3 => 0.61,
                _ => 0.82
            },
            AccompanimentStyle.JazzBallad => chorus switch
            {
                <= 1 => 0.18,
                2 => 0.34,
                3 => 0.62,
                _ => 0.84
            },
            _ => 0.30
        };
        var withinFormLift = isEndingForm
            ? 0.02
            : style == AccompanimentStyle.JazzBallad
                ? 0.17
                : 0.12;
        var development = Math.Clamp(opening + progressThroughForm * withinFormLift, 0.12, 0.96);
        var structuralPeak = !isEndingForm && (style, chorus) switch
        {
            (AccompanimentStyle.Swing, >= 4) => feel == RhythmFeel.FourBeat,
            (AccompanimentStyle.BossaNova, >= 4) => true,
            (AccompanimentStyle.AfroCubanLatin, >= 4) => true,
            (AccompanimentStyle.JazzWaltz, >= 4) => true,
            (AccompanimentStyle.JazzBallad, >= 4) => true,
            (AccompanimentStyle.JazzBallad, 3) => startBar >= Math.Max(1, formBars / 2),
            _ => false
        };
        var highStage = requestedGuidance.HighStage || structuralPeak;
        var intensity = development switch
        {
            >= 0.72 => PerformanceIntensity.High,
            >= 0.36 => PerformanceIntensity.Medium,
            _ => PerformanceIntensity.Low
        };

        return new PerformanceGuidance(
            HasRecentInput: false,
            Intensity: intensity,
            Energy: development,
            ShortEnergy: development,
            Density: Math.Clamp(0.20 + development * 0.72, 0, 1),
            AverageVelocity: 53 + development * 23,
            Motion: Math.Clamp(0.18 + development * 0.64, 0, 1),
            PhraseActivity: 0,
            PhraseEndedRecently: false,
            HighEnergySustained: highStage,
            HighEnergyBars: highStage ? 4.0 : 0,
            AveragePitch: 64,
            HighStage: highStage);
    }

    private static int ResolveTempo(TuneForm form, int tempoBpm)
    {
        var resolved = tempoBpm == 0 ? form.DefaultTempoBpm : tempoBpm;
        if (resolved is < 40 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(tempoBpm), "Tempo must be between 40 and 300 BPM.");
        }

        return resolved;
    }

    private static IReadOnlyList<BarArrangement> ApplyTransitionLeadIn(
        IReadOnlyList<BarArrangement> arrangements,
        int endBarExclusive,
        int playableBarCount,
        bool isEndingForm,
        bool isHeadOut,
        int startBar,
        bool transitionEligible)
    {
        if (arrangements.Count == 0)
        {
            return arrangements;
        }

        var result = arrangements.ToArray();
        if (isHeadOut && startBar == 0)
        {
            // The head's first bar is a gentle landing point. Dedicated drum
            // generators use this marker to suppress an arrival accent.
            result[0] = result[0] with { IsHeadOutEntry = true };
        }

        // The final two bars are not a hard switch. They retain the harmonic
        // pulse while foreground density tapers into the next head. A separate
        // head-out segment is already the landing texture, so it is not tapered
        // again at its own end.
        if (transitionEligible && !isEndingForm && !isHeadOut && endBarExclusive >= playableBarCount)
        {
            var first = Math.Max(0, result.Length - 2);
            for (var index = first; index < result.Length; index++)
            {
                result[index] = result[index] with { IsTransitionLeadIn = true };
            }
        }

        return result;
    }
}
