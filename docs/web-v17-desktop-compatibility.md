# Jampanion Web v17 desktop compatibility correction

> Superseded by v18 complete. The v17 conclusions about single-line slash
> chords and removal of all chord-divider lines were incorrect. The desktop
> application uses two-line slash chords and a thin divider at actual chord
> changes, while still showing no beat grid.


The released desktop application is the visual authority for the Web chord sheet.

## Removed Web-only presentation

- visible internal vertical divider lines that could be interpreted as beat lines
- multiline slash-chord presentation
- inline validation/status text inside the Chord Sheet toolbar
- unused placeholder and in-bar section-tag presentation rules left from earlier Web prototypes

## Preserved behavior

The transparent beat-position hit areas remain because the desktop application allows double-click editing or insertion at a beat position. They have no border, background, hover decoration, or visible grid. Chord positions and current-chord highlighting still use the underlying beat spans, but the presentation no longer exposes a Web-specific beat grid.

Responsive reordering remains Web-specific by necessity, while desktop widths retain the desktop hierarchy and dimensions.
