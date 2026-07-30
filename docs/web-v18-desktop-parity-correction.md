# Jampanion Web v18 complete desktop parity correction

## Baseline

This bundle was produced from the SHA-256-verified v17 formal source bundle:

`548a25e7872fd916905357e8be6f8a244283c7161385419dd7d3785c45cf18d3`

The incomplete earlier v18 attempt was not used.

## Desktop-authoritative corrections

The released Avalonia application remains the authority for normal-width chord
sheet presentation and playback following. Direct comparison with
`MainWindow.axaml`, `MainWindow.axaml.cs`, and `MainWindowViewModel.cs` showed
that v17 had removed two real desktop behaviors together with the unwanted beat
grid:

1. Slash chords are displayed on two lines. The formatted chord part is on the
   first line; a leading space, slash, and bass note are on the second line.
2. Each chord segment has a thin right-hand divider. This marks an actual chord
   change, not every beat. Transparent beat editing regions remain invisible.

The Web implementation now also uses the desktop chord-layout constants and
measurement behavior: bold Arial/symbol fallback typography, chord-count maxima
of 22/20/18/16 px, 7 px minimum, width-only fitting, quarter-pixel rounding, and
a 6 px total width allowance for text inset and safety margin.

## Responsive behavior

Below the desktop minimum width, SONG, Chord Sheet, and MIX may reorder and stack
as a Web-specific responsive layout. Playback following remains invariant:

- current-bar and current-chord classes are unchanged by responsive CSS;
- automatic scrolling targets the current highlighted chord, falling back to the
  current bar;
- scroll visibility is re-evaluated after both width and height changes;
- a chord change within the same bar is no longer suppressed by a cached bar
  element.

## Intentionally not restored

- No visible beat grid or beat-position divider was added.
- No Web-only status text was added to the Chord Sheet toolbar.
- No obsolete placeholder or prototype section-tag decoration was restored.

## Validation boundary

JavaScript syntax and source/package integrity checks were run in the artifact
environment. The environment does not contain the .NET 10 SDK or the complete
`Jampanion.Core` project, so final Blazor compilation must run after overlaying
this bundle onto the current Jampanion repository, locally or through GitHub
Actions.
