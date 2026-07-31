# Jampanion

Jampanion is a jazz rhythm section for jam-session practice. It reads ChordPro
charts, follows the form, and generates piano, bass, and drums that follow the
chart from the head through the solo choruses and back to the head.

Use the browser version immediately, or download the Windows and macOS desktop
apps for platform audio settings and full MIDI-device integration.

- [Open Jampanion Web](https://over-keys.github.io/Jampanion/)
- [Download the latest desktop release](https://github.com/over-keys/Jampanion/releases/latest)
- [Japanese user guide](Jampanion_日本語説明書.md)

## Features

- Piano, bass, and drums for Swing, Jazz Ballad, Bossa Nova,
  Jazz Waltz, and Afro-Cuban Latin/Mambo
- 18 built-in standards plus a personal song library
- Create a new chart by entering its title and number of bars
- Import ChordPro (`.cho`, `.chordpro`, `.chopro`) and iReal Pro shared-song
  exports
- Edit chords and rehearsal marks directly on the chord sheet
- Set a style for the whole song or override it for an individual section
- Change tempo during playback and queue style changes at a musical boundary
- Transpose charts and choose automatic, flat, or sharp chord spelling
- Follow the current bar and chord with automatic chart scrolling
- Mix piano, bass, drums, and MIDI thru independently
- Return to the head manually, or use the experimental MIDI-energy detector
- Use the built-in trio or an external MIDI output

The Web and desktop apps share the same chart parser, accompaniment generators,
and session-planning core. The accompaniment follows the chart form and selected
style; it does not change in response to the performer's playing intensity.
Browser playback starts after preparing the opening blocks, then expands the
arrangement incrementally so the interface and current playback remain
responsive.

## Quick start

1. Open [Jampanion Web](https://over-keys.github.io/Jampanion/) or launch the
   desktop app.
2. Search for a chart in `Song`.
3. Set the tempo, style, key, and accidental spelling.
4. Select `Start session` or press `Space`.
5. During playback, select `Back to head` or press `Space` to return at the next
   suitable chorus boundary. Select `Stop` to stop immediately.

Tempo can be changed while the session is playing. A style change is prepared
without stopping playback and takes effect at the next suitable four-bar
boundary; a rehearsal-mark-specific style remains authoritative.

`Theme Return` defaults to `Manual`. Its experimental `Auto` mode uses MIDI
performance energy near the end of the form to decide whether the solo should
continue or return to the head. The accompaniment keeps its form and bass time
even when no MIDI input is connected.

## Add a new song

New songs start as editable ChordPro charts in C, 4/4, Swing, and 120 BPM, with
one C chord in each bar. A chart can contain from 4 to 512 bars; the default is
32.

1. Open `Settings` → `Song Library` → `New song`.
2. Enter a title and the number of bars, then select `Create`.
3. Double-click a chord position or rehearsal mark to edit it. Right-click a
   rehearsal mark to assign a section style.
4. Select `Save` in `Song` or `Chord Sheet`.

The workflow is the same in the Web and desktop apps. Web songs are saved in
the current browser's local storage; double-click the title to rename a local
song, right-click it to delete it, and use `Export .cho` to back it up or move it
to another browser or the desktop app. The desktop app creates the `.cho` file
immediately in the configured song-library folder. The default folder is:

```text
Documents/Jampanion/Songs
```

## Import existing songs

Open `Settings` → `Song Library` and choose one of:

- `Import .cho` for a ChordPro chart
- `Import iReal Pro` for an iReal Pro shared-song export

The iReal Pro importer accepts:

| File | Required content |
| --- | --- |
| HTML (`.html`, `.htm`) | One or more `irealb://` shared-song links |
| Plain text (`.txt`) | An `irealb://` link by itself, or text containing one or more links |

Both single-song and multi-song shared links are supported. Imported songs must
use 4/4 or 3/4; Jampanion plays 3/4 as Jazz Waltz and supports Swing, Jazz
Ballad, Bossa Nova, and Latin/Mambo for 4/4 charts. An unrecognized iReal style
is kept as metadata and falls back to Swing in 4/4 or Jazz Waltz in 3/4. Native
iReal Pro database or backup files are not imported directly.

iReal Pro songs are converted into editable Jampanion ChordPro charts. The Web
app stores imported charts in browser local storage; the desktop app stores them
in its configured song-library folder. Personal and bulk-imported song
libraries are not included in this repository.

## Web and desktop

| Capability | Web | Desktop |
| --- | --- | --- |
| Built-in trio | Browser synth | Native built-in trio |
| New, import, and edit songs | Browser local storage | Configurable song folder |
| ChordPro export | Yes | Files are already stored as `.cho` |
| MIDI input and energy analysis | Web MIDI where supported | Native MIDI |
| External MIDI output | Web MIDI where supported | Native MIDI |
| Platform audio settings | Browser-managed | Windows ASIO/WinMM; macOS CoreAudio |

Web MIDI requires a compatible browser and permission; Chromium-based browsers
provide the broadest support. The internal browser synth works without MIDI
permission.

## Included charts

The repository contains 18 built-in standards:

Autumn Leaves, All The Things You Are, Beautiful Love, Bye Bye Blackbird,
Candy, Confirmation, The Days Of Wine And Roses, Girl From Ipanema, I Love You,
I'll Close My Eyes, It Could Happen To You, Just Friends,
On Green Dolphin Street, Softly As In A Morning Sunrise,
Someday My Prince Will Come, Stella By Starlight, There Is No Greater Love, and
There Will Never Be Another You.

## Install the desktop app

### Windows

Download `Jampanion-Windows-x64.zip`, extract it, and run `Jampanion.exe`.

### macOS

Download `Jampanion-macOS-arm64.zip` for Apple Silicon or
`Jampanion-macOS-x64.zip` for an Intel Mac. Extract the archive and open
`Jampanion.app`.

Release packages are Developer ID signed and notarized when the required Apple
credentials are configured. An Ad Hoc build may require one-time approval in
`System Settings` → `Privacy & Security` → `Open Anyway`.

## Build from source

Jampanion requires the .NET 10 SDK. The Web audio bundle also requires Node.js
20 or later.

```bash
dotnet restore Jampanion.sln
dotnet build Jampanion.sln -c Release
```

Run the Web app locally:

```bash
./run-web-local.sh
```

On Windows, use `run-web-local.ps1` for the Web app and
`scripts/package-win-x64.ps1` to create a desktop package. Signed macOS packages
are built by `.github/workflows/build-macos-release.yml`.

See [Jampanion Web and GitHub Pages](docs/web-pages.md) and
[macOS builds from GitHub Actions](docs/macos-actions-build.md) for the
deployment and release procedures.

## Project layout

- `src/Jampanion.Core`: chart parsing, arrangement, and generation logic
- `src/Jampanion.Web`: Blazor WebAssembly app and browser audio/MIDI adapters
- `src/Jampanion`: Avalonia desktop app
- `src/Jampanion/Live`: desktop MIDI, playback, audio, settings, and song
  services
- `scripts`: build, SoundFont, and packaging tools

Third-party notices are in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
For a shorter walkthrough, see [QUICK_START.md](QUICK_START.md).
