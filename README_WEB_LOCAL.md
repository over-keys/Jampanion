# Jampanion Web v18 complete

This source bundle adds the browser application to the root of the current
Jampanion repository. It references `src/Jampanion.Core`, so the desktop and Web
versions use the same ChordPro/iReal parsers and accompaniment generators.

## Requirements

- .NET SDK 10
- Node.js 20 or later
- npm
- The current Jampanion repository, including `src/Jampanion.Core`

## Local start

Extract the bundle into the repository root, then run:

```bash
cd ~/Codex/Jampanion-web-test
rm -rf src/Jampanion.Web/wwwroot/js
SKIP_NPM_INSTALL=1 ./run-web-local.sh
```

Use the following instead when `src/Jampanion.Web/node_modules` is absent:

```bash
./run-web-local.sh
```

Open `http://localhost:5279/`. After replacing an older source bundle, reload
Chrome with `Command + Shift + R`.


## v18 desktop chart parity correction

This release starts from the verified v17 source bundle and corrects the two
places where v17 inferred the desktop behavior incorrectly.

- Restores the desktop two-line slash-chord layout. The chord part is on the
  first line, and the slash plus bass note are on the second line.
- Restores only the thin divider at an actual chord-change boundary. Invisible
  beat-position hit areas remain borderless, so no beat grid is exposed.
- Matches desktop chord typography and fitting: Arial with the same symbol
  fallbacks, bold text, 22/20/18/16 px maxima by chord count, a 7 px lower
  bound, 0.25 px fitting steps, and desktop-equivalent text insets.
- Keeps the desktop current-bar, next-bar, and current-chord highlighting.
- Makes current-position scrolling respond to the highlighted chord itself and
  re-evaluate after responsive width or height changes, so narrow vertical
  layouts continue to follow playback.
- Responsive panel reordering remains intentionally Web-specific below the
  desktop minimum width.

## v16 review and corrections

- Uses the desktop application's piano-and-note icon for browser tabs, Apple
  touch icons, installable PWA icons, maskable icons, and the loading screen.
- Rebuilt the responsive layout without contradictory legacy media rules.
  At narrow widths the page order is SONG, Chord Sheet, MIX, and the chord sheet
  never requires a fixed desktop minimum width.
- Keeps Theme Return and Energy visible at narrow widths.
- Keeps the desktop control geometry at desktop widths, including the 40 × 20 px
  Auto/Manual track, 66 px switch area, 190 px sliders, and 252 px sidebar.
- Uses the desktop 60–150% chord-sheet scale range and width-dependent chord
  fitting, with a 7 px lower bound.
- Removes the retired custom search dropdown code. SONG remains a normal text
  input with browser-native filtered suggestions, avoiding focus loss during
  Blazor rerenders.
- Preserves unmodeled ChordPro header metadata, including composer/subtitle and
  original iReal style, when a local song is edited and saved.
- Refresh library now reloads the browser metadata index rather than only
  rebuilding the existing in-memory list.
- Song saves and iReal imports are staged and rolled back when browser storage
  fails, preventing half-written library state.
- Large iReal files are converted one song at a time with a browser yield and
  visible progress between songs. Each song still goes through the authoritative
  `Jampanion.Core` converter and validator.
- Repairs legacy local-song keys that were written without URI escaping.
- Clears old local-development service workers and Jampanion caches before the
  Blazor boot script is requested. Versioned module URLs and a network-first
  production worker prevent mixed old/new application assets after deployment.
- Settings closes with Escape. Space retains the desktop Start session / Back to
  head shortcut outside text fields and controls.
- Fixes the live-tempo replacement cursor so the first look-ahead window is
  scheduled after rebasing rather than skipped.
- Updates Blazor WebAssembly packages to the .NET 10.0.10 security patch and
  esbuild to 0.28.1.
- Audio and Service Worker cache identifiers are advanced to v16.

A detailed review matrix is in `docs/web-v16-review.md`.

## Browser storage

Imported and edited songs are stored in browser local storage. Use **Export .cho**
for portable backup. **Delete all local songs** removes browser-local imports,
new songs, and edited local copies but leaves built-in songs intact.

## Audio

The bundled SoundFont is verified before launch:

```text
src/Jampanion.Web/wwwroot/soundfonts/FluidR3_Jampanion.sf3
SHA-256 2e4aa17f20743930c87ada7cc1fee2228ecd2bb0e2de75a83cd590c53bcd0d63
```

The first Start session may take longer because the browser initializes the
AudioWorklet and parses the SoundFont only after a user gesture. Later starts
reuse the initialized synthesizer.

## Browser-specific limits

- Web MIDI support and permission depend on the browser. Chromium-based browsers
  provide the most complete support.
- Browser local storage is browser/profile specific and has a quota.
- The Web version does not expose desktop ASIO or operating-system MIDI output
  configuration.
- The Web version uses browser-local files rather than the desktop song-library
  folder.

## GitHub Pages

`.github/workflows/deploy-pages.yml` builds the browser audio assets, publishes
Blazor WebAssembly, rewrites the repository base path, and deploys the generated
site. Before upload, `scripts/verify-pages-smoke.sh` serves the published output
at the repository subpath and opens it in headless Chrome. Deployment is blocked
unless Blazor replaces the loading screen and renders the session controls and
chord-sheet workspace. Configure **Settings → Pages → Source** as **GitHub Actions**.
