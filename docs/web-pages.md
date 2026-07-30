# Jampanion Web and GitHub Pages

`src/Jampanion.Web` is a Blazor WebAssembly application that directly references
`Jampanion.Core`. The browser version therefore uses the same ChordPro parser,
session planning types, and accompaniment generators as the desktop application.

## Implemented browser adapters

- SpessaSynth AudioWorklet output using the bundled four-preset SF3
- Desktop-style Song/Mix sidebar and desktop-style chord sheet without beat or bar-number clutter
- Global style selection and rehearsal-mark-specific style overrides
- Key transposition and sharp/flat spelling
- Direct chord-position and rehearsal-mark editing, with the desktop-style compact chart display
- Browser-local song library
- ChordPro import and export
- Web MIDI input and vibraphone MIDI thru where Web MIDI is supported
- MIDI energy display and conservative automatic ending cue
- Start Session, Cue Ending, Stop, and Panic
- PWA caching and GitHub Pages deployment

## Audio architecture

- `Jampanion.Core` generates timestamped piano, bass, and drum notes.
- `spessasynth_lib` renders them in an AudioWorklet.
- A short look-ahead scheduler reduces UI-thread timing jitter.
- Cue Ending replaces only future, not-yet-scheduled events and starts HeadOut on
  the next chorus.
- `FluidR3_Jampanion.sf3` is committed with the web source and verified by hash.

## Bundled SoundFont

```text
src/Jampanion.Web/wwwroot/soundfonts/FluidR3_Jampanion.sf3
```

SHA-256:

```text
2e4aa17f20743930c87ada7cc1fee2228ecd2bb0e2de75a83cd590c53bcd0d63
```

Presets:

- Bank 0, program 0: Yamaha Grand Piano
- Bank 0, program 11: Vibraphone
- Bank 0, program 32: Acoustic Bass
- Bank 128, program 0: Standard drum kit

## GitHub Pages deployment

1. Merge the web source and `.github/workflows/deploy-pages.yml` into `main`.
2. Open **Settings → Pages**.
3. Set **Build and deployment → Source** to **GitHub Actions**.
4. Run the Pages workflow or push a relevant source change.

The workflow rewrites the Blazor base path for the repository site and creates a
matching `404.html` fallback. It then serves the exact published directory under
the repository subpath and runs a headless-Chrome smoke test. The deploy job does
not run unless the Blazor shell, Start session control, and chord-sheet workspace
are actually rendered.

## Local development

```bash
chmod +x run-web-local.sh
./run-web-local.sh
```

Open `http://localhost:5279/`.

## Browser-specific limits

- Web MIDI depends on browser support and permission. The internal synth works
  without Web MIDI.
- Browser-local charts are device/browser-specific until exported as `.cho`.
- The Web audio output does not expose desktop ASIO configuration.

## Lazy chart loading

The built-in selector opens each embedded `.cho` only far enough to read its
title and ID header. `TuneCatalog` and `DefaultSongCatalog.All` are intentionally
not touched during Web startup because they would load or materialize every
built-in chart. Only the currently selected chart body is read and parsed into
editable bars and an active `TuneForm`. Browser-local charts use a metadata-only
index and separate per-song storage, so each chart body is also read and parsed
only when selected.

Storage, focus, and file downloads use `jampanion-browser.js`, which has no audio
library dependency. Startup only warms the browser cache for the bundled SF3.
Full SpessaSynth and AudioWorklet initialization is deferred until Start Session
or MIDI thru explicitly requires audio. Startup idle time only warms the HTTP
cache for the bundled SF3; it does not parse the SoundFont on the UI thread.

## IReal Pro import

The Song Library settings include **Import iReal Pro**. Browser file input accepts `.html`, `.htm`, and `.txt` files containing `irealb://` shared links. Conversion uses the same `IRealProSongParser` in `Jampanion.Core` as the desktop app. Each song is passed separately through the same Core converter and validator,
with a browser yield and progress update between songs. Converted charts are
assigned collision-free browser-library IDs and staged in local storage; if a
write fails, the newly written charts and metadata index are rolled back.
