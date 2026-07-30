# Jampanion Web v16 comprehensive review

## Review basis

The Web source was reviewed against the current desktop window structure,
settings window, ViewModel behavior, Core ChordPro/iReal conversion, and browser
runtime code. The review covered startup, PWA caching, responsive layout, SONG,
MIX, chord-sheet rendering/editing, local song management, MIDI, audio scheduling,
and deployment.

## Corrected high-priority findings

### Startup and cache consistency

The Blazor boot script was requested before the local-development Service Worker
was unregistered. A still-controlling worker could therefore serve an older
framework or JavaScript file during the same reload. v16 performs local worker
unregistration and Jampanion-cache deletion first, then injects the Blazor boot
script. Production uses versioned module/style URLs and a network-first worker for app
code, so an older installed worker cannot keep serving a mixed v15/v16 shell.
The large immutable SoundFont remains cache-first.

### App icon parity

The independent Web SVG was removed. The browser tab, Apple touch icon,
installable PWA icons, maskable icons, and loading screen now use the desktop
application's piano-and-note artwork. The manifest separates normal and maskable
resources instead of claiming one image is both.

### iReal responsiveness and transaction safety

A whole iReal export was previously converted in one uninterrupted WebAssembly
call. v16 extracts individual song payloads, invokes the same Core parser and
validator for each song, updates progress, and yields to the browser between
songs. Browser storage is staged; partial writes and the metadata index are
rolled back if a later write fails.

### Metadata preservation

The editable Web model previously regenerated only directives it understood.
Consequently, saving an imported iReal song could discard composer/subtitle and
original-style metadata. v16 preserves non-modeled header directives and comments
while continuing to regenerate Jampanion-owned title, ID, key, meter, tempo,
playback style, section-style, coda, and grid directives.

### Library refresh and legacy data

Refresh library now re-reads the browser metadata index. Old all-in-one library
migration writes URI-escaped song keys, and existing unescaped legacy keys are
repaired lazily when opened.

### Responsive layout

The stylesheet had accumulated conflicting media rules, including a rule that
hid Theme Return and Energy and a later rule that restored them. It also retained
obsolete search-dropdown and settings-drawer selectors. v16 replaces the file
with one deterministic layout system:

- Desktop: 252 px SONG/MIX sidebar plus chord sheet.
- Below the desktop minimum: SONG, chord sheet, then MIX.
- Four chart cells share all available row width with no fixed 860/190 px minima.
- Theme Return and Energy remain present.
- Mobile reductions affect wrapping and gutters, not feature availability.

## Corrected medium-priority findings

- SONG and Chord Sheet Save buttons now have separate dirty-state enablement.
- New unsaved songs use millisecond IDs to avoid rapid-create collisions.
- Settings closes with Escape.
- Obsolete custom SONG autocomplete state and handlers were removed after the
  stable native text/datalist implementation replaced them.
- The Service Worker precache contains every required icon and uses reload
  requests during installation.
- Local iReal keys avoid collisions with both built-in and local song IDs.
- Chord fitting runs only after chart-affecting changes or actual width changes,
  not on every playback status update.
- Audio initialization remains outside the first interactive render and begins
  only after a browser user gesture.
- Live tempo changes now resume scheduling at the exact rebased position; the
  first 120 ms look-ahead window is no longer skipped.
- Blazor WebAssembly package references use the .NET 10.0.10 security patch, and
  esbuild uses 0.28.1.

## Confirmed behavior

- Built-in song metadata is loaded lazily; a full chart is parsed only when the
  song is selected.
- Browser-local songs take precedence over built-ins with the same ID/title.
- Deleting all local songs removes indexed and orphaned browser song records but
  preserves built-in charts.
- Key changes are blocked during playback; tempo changes can be applied during
  playback; style changes are queued at a safe four-bar boundary.
- Cue Ending replaces only the future continuation beyond the protected launch
  region or current look-ahead window.
- MIX values display 0–100 and are converted to MIDI controller values before
  they are sent to the synthesizer.
- Scale uses the desktop 60–150% stops. Chord labels use desktop maximum sizes
  based on chord count and shrink to a 7 px lower bound only when necessary.

## Intentional browser-specific differences

These are platform constraints rather than unfinished desktop-parity work:

- Browser-local storage replaces a desktop file-system song folder.
- Web MIDI availability and permission are browser-dependent.
- The Web synth does not expose desktop ASIO or arbitrary OS MIDI output setup.
- The first audio start includes AudioWorklet/SoundFont initialization.
- Browser storage quota requires `.cho` export for durable, transferable backup.

## Remaining review notes

- The npm dependencies use exact top-level versions, but this source bundle still
  has no `package-lock.json`. A lock file should be generated and committed from
  a machine with direct npm registry access, then the workflow can move from
  `npm install` to `npm ci`.
- Native HTML datalist presentation and Web MIDI device naming remain
  browser-controlled and cannot be pixel-identical to Avalonia.
- Browser-local mixer, Theme Return, and Scale values currently reset when the
  page is reloaded; song settings themselves persist when Save is used.

## Validation boundary

Static syntax, structure, asset, hash, and packaging checks were performed in the
artifact environment. A .NET 10 SDK was not available there, so the final Blazor
compile and interactive audio/MIDI checks must run on the user's Mac or through
GitHub Actions.
