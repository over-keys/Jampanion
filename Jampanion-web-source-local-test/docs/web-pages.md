# Jampanion Web and GitHub Pages

`src/Jampanion.Web` is a standalone Blazor WebAssembly application. It references
`Jampanion.Core`, so the browser version uses the same chart parser and accompaniment
generators as the desktop version rather than a simplified JavaScript generator.

## Audio architecture

- Jampanion.Core generates timestamped piano, bass and drum notes.
- `spessasynth_lib` schedules them in an AudioWorklet.
- The scheduler sends only a short look-ahead window to reduce timing jitter while
  allowing Stop and Panic to react promptly.
- `FluidR3_Jampanion.sf3` is bundled with the web project. It is an MIT-licensed,
  browser-compressed FluidR3 subset containing only piano, vibraphone, acoustic bass
  and the standard drum kit.
- GitHub Actions verifies the committed SF3 by SHA-256 instead of downloading or
  converting an audio bank during deployment.
- The SF3, application shell and licenses are cached by the service worker after use.

## Bundled SoundFont

Path:

```text
src/Jampanion.Web/wwwroot/soundfonts/FluidR3_Jampanion.sf3
```

SHA-256:

```text
2e4aa17f20743930c87ada7cc1fee2228ecd2bb0e2de75a83cd590c53bcd0d63
```

The bank contains exactly these presets:

- Bank 0, program 0: Yamaha Grand Piano
- Bank 0, program 11: Vibraphone
- Bank 0, program 32: Acoustic Bass
- Bank 128, program 0: Standard drum kit

The source bank and derivation details are recorded in
`wwwroot/licenses/FluidR3_GM-MIT.txt`. The standalone subset utility is retained at
`scripts/Sf3Subset/subset_sf3.py` for provenance and future regeneration, but it is
not run by the Pages workflow.

## GitHub Pages deployment

1. Merge the web project and `.github/workflows/deploy-pages.yml` into `main`.
2. In GitHub, open **Settings → Pages**.
3. Set **Build and deployment → Source** to **GitHub Actions**.
4. Run **Deploy Jampanion Web to GitHub Pages**, or push a change under
   `src/Jampanion.Web`, `src/Jampanion.Core`, or the workflow.
5. The repository site is published at
   [https://over-keys.github.io/Jampanion/](https://over-keys.github.io/Jampanion/).

The workflow rewrites the Blazor `<base>` element to the repository subpath and
creates `404.html`, so direct navigation under GitHub Pages remains functional.

## Local development

Requirements:

- .NET SDK 10
- Node.js 24

Build the browser audio bundle:

```bash
cd src/Jampanion.Web
npm install
npm run build
```

Run the web app:

```bash
dotnet run --project src/Jampanion.Web/Jampanion.Web.csproj
```

No SoundFont download or conversion step is required.

## Current implementation boundary

This foundation provides:

- embedded song selection
- tempo selection
- two-bar count-in
- five-stage Opening → Groove → Developing → Peak → HeadOut session generation
- real Jampanion.Core piano, bass and drum output
- per-part enable and volume controls
- chord-sheet display with current-bar highlighting
- local song-file import
- Stop, Panic, PWA caching and GitHub Pages deployment

External Web MIDI, in-browser chart editing, live energy analysis, manual HeadOut
cueing, and IndexedDB song-library persistence remain separate browser adapters
and can be added without replacing the shared accompaniment core or audio engine.
