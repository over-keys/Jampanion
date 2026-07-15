# Jampanion

Jampanion is an adaptive jazz backing partner for jam-session practice.
It reads ChordPro charts, follows the form, and generates piano, bass, and drums
for Swing, Jazz Ballad, Bossa Nova, Jazz Waltz, and Afro-Cuban Latin/Mambo.

The arrangement develops gradually from the head through the solo choruses and
returns to the head without interrupting the chart view. MIDI input can guide
the energy analysis, but the generated rhythm section keeps its musical form
and bass time independently.

## Included charts

The repository contains 18 built-in standards, including Autumn Leaves,
All The Things You Are, Beautiful Love, Bye Bye Blackbird, Candy,
Confirmation, The Days Of Wine And Roses, Girl From Ipanema, I Love You,
I'll Close My Eyes, It Could Happen To You, Just Friends,
On Green Dolphin Street, Softly As In A Morning Sunrise,
Someday My Prince Will Come, Stella By Starlight, There Is No Greater Love,
and There Will Never Be Another You.

Bulk-imported personal song libraries are not included in this repository.

## Quick operation

- Press `Space` to start a stopped session. During playback, press `Space` to queue `Back to Head`.
- `Auto` theme return is experimental and is off by default. Turn it on in `THEME RETURN` when needed; `Manual` uses the `Back to Head` button only.
- Automatic theme return evaluates the final two bars. A quieter ending can be detected as a return, while continued solo energy into the next chorus can keep the solo going.
- Click the selected title in `SONG` to clear the field and begin a new search. Selecting a result displays its title again.
- MIDI port choices are remembered after manual selection. On first launch, Microsoft GS Wavetable Synth is preferred, followed by FluidSynth when available.

## Run on Windows

Extract the Windows package and start `Jampanion.exe`.

The default song folder is:

```text
Documents\Jampanion\Songs
```

An existing `Documents\AI Jam\Songs` folder is still read for migration
compatibility. Imported or edited charts remain plain-text `.cho` files.

## Run on macOS

Choose `Jampanion-macOS-x64.zip` for Intel Macs or `Jampanion-macOS-arm64.zip` for Apple Silicon Macs. Extract the ZIP and open `Jampanion.app`. If macOS reports that the app is not executable, run this once from Terminal inside the extracted folder:

```bash
chmod +x Jampanion.app/Contents/MacOS/Jampanion
```

The built-in trio uses CoreAudio on macOS. External MIDI availability depends on the connected device and macOS MIDI setup.

## Build

Requires .NET SDK 10.

```powershell
dotnet restore Jampanion.sln
dotnet build Jampanion.sln -c Release
.\scripts\package-win-x64.ps1
```

The Windows package is written to `artifacts\package`. macOS builds can be published with `-r osx-x64` or `-r osx-arm64` and the same self-contained publish options.

## Project layout

- `src/Jampanion`: Avalonia desktop application
- `src/Jampanion.Core`: chart parsing, arrangement, and generation logic
- `src/Jampanion/Live`: MIDI, playback, audio, settings, and song services
- `scripts`: build and packaging scripts

Third-party notices are in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).

For a short user guide, see [QUICK_START.md](QUICK_START.md).
