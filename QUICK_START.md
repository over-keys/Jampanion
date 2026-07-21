# Jampanion Quick Start

Jampanion is for practicing jam-session accompaniment with ChordPro charts.

1. Extract the Windows ZIP and run `Jampanion.exe`, or use `Jampanion-macOS-x64.zip` / `Jampanion-macOS-arm64.zip` on macOS.
2. Choose a song in `SONG`. Click the selected title again to clear the search field.
3. Choose a style, key, accidental spelling, and tempo as needed.
4. Press `Space` or `Start Session` to begin. During playback, press `Space` or `Back to Head` to return at the next suitable chorus boundary. Use `Stop` to stop.

`Auto` theme return is experimental and off by default. Enable it in `THEME RETURN` only when you want automatic detection. It uses the final two bars: a drop in activity can be interpreted as a return, while a strong continuation into the next chorus can keep the solo going.

MIDI input and output are optional. Manually selected ports are remembered for the next launch. On first launch, Microsoft GS Wavetable Synth is preferred, followed by FluidSynth when available. Without external MIDI output, use the built-in trio output.

Imported or edited charts are plain-text `.cho` files in `Documents\Jampanion\Songs`.

On macOS, open `Jampanion.app`. Public release ZIPs are Developer ID signed and notarized; an Actions artifact-only ZIP is for engineering checks and may be blocked by macOS Gatekeeper. If needed, run `chmod +x Jampanion.app/Contents/MacOS/Jampanion` once in Terminal for older packages.

詳しい日本語の操作説明は [Jampanion_日本語説明書.md](Jampanion_日本語説明書.md) を参照してください。
