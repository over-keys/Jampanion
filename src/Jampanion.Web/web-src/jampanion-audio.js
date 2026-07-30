import { WorkletSynthesizer } from "spessasynth_lib";

const VIBRAPHONE_CHANNEL = 0;
const BASS_CHANNEL = 1;
const PIANO_CHANNEL = 2;
const DRUMS_CHANNEL = 9;
const LOOK_AHEAD_SECONDS = 0.12;
const SCHEDULER_INTERVAL_MS = 24;
const AUDIO_BUILD_ID = "jampanion-audio-v18";

let audioContext;
let synthesizer;
let synthesizerPromise;
let scheduledEvents = [];
let eventCursor = 0;
let playbackStart = null;
let playbackDuration = 0;
let schedulerTimer = null;
let midiAccess = null;
let activeMidiInput = null;
let midiDotNetReference = null;
let mixerState = {
    pianoEnabled: true,
    bassEnabled: true,
    drumsEnabled: true,
    midiThruEnabled: false,
    pianoVolume: 100,
    bassVolume: 100,
    drumsVolume: 100,
    vibraphoneVolume: 100
};

async function ensureSynthesizer() {
    if (synthesizer) {
        return synthesizer;
    }
    if (synthesizerPromise) {
        return synthesizerPromise;
    }

    synthesizerPromise = initializeSynthesizer();
    try {
        return await synthesizerPromise;
    } catch (error) {
        synthesizerPromise = null;
        throw error;
    }
}

async function initializeSynthesizer() {
    const AudioContextClass = window.AudioContext || window.webkitAudioContext;
    if (!AudioContextClass) {
        throw new Error("This browser does not support Web Audio.");
    }
    if (!window.AudioWorkletNode) {
        throw new Error("This browser does not support AudioWorkletNode.");
    }

    audioContext = new AudioContextClass({ latencyHint: "interactive" });

    const processorUrl = new URL("./spessasynth_processor.min.js", import.meta.url);
    processorUrl.searchParams.set("v", AUDIO_BUILD_ID);
    await audioContext.audioWorklet.addModule(processorUrl.href);

    try {
        synthesizer = new WorkletSynthesizer(audioContext, {
            oneOutput: false,
            eventsEnabled: false
        });
    } catch (error) {
        const cause = error?.cause;
        const detail = cause?.message || cause?.name || error?.message || String(error);
        throw new Error(`AudioWorkletNode creation failed: ${detail}`);
    }

    synthesizer.connect(audioContext.destination);
    synthesizer.setLogLevel(false, true, false);

    const soundFontUrl = new URL("../soundfonts/FluidR3_Jampanion.sf3", import.meta.url);
    const response = await fetch(soundFontUrl, { cache: "force-cache" });
    if (!response.ok) {
        throw new Error(`SoundFont download failed (${response.status}).`);
    }

    await synthesizer.soundBankManager.addSoundBank(await response.arrayBuffer(), "jampanion");
    await synthesizer.isReady;
    configurePrograms();
    setMixer(mixerState);
    return synthesizer;
}

function configurePrograms() {
    synthesizer.programChange(VIBRAPHONE_CHANNEL, 11);
    synthesizer.programChange(PIANO_CHANNEL, 0);
    synthesizer.programChange(BASS_CHANNEL, 32);
    synthesizer.programChange(DRUMS_CHANNEL, 0);
}

function schedulePendingThrough(horizon) {
    while (eventCursor < scheduledEvents.length) {
        const note = scheduledEvents[eventCursor];
        const startTime = playbackStart + note.startSeconds;
        if (startTime > horizon) {
            break;
        }

        if (startTime >= audioContext.currentTime - 0.02) {
            const endTime = startTime + Math.max(0.01, note.durationSeconds);
            synthesizer.noteOn(note.channel, note.noteNumber, note.velocity, { time: startTime });
            synthesizer.noteOff(note.channel, note.noteNumber, { time: endTime });
        }
        eventCursor += 1;
    }
}

function schedulerTick() {
    if (!synthesizer || !audioContext || playbackStart === null) {
        return;
    }

    schedulePendingThrough(audioContext.currentTime + LOOK_AHEAD_SECONDS);

    if (eventCursor >= scheduledEvents.length &&
        audioContext.currentTime > playbackStart + playbackDuration + 0.25) {
        clearScheduler();
    }
}

function clearScheduler() {
    if (schedulerTimer !== null) {
        window.clearInterval(schedulerTimer);
        schedulerTimer = null;
    }
}

function sortEvents(events) {
    return [...events].sort((left, right) =>
        left.startSeconds - right.startSeconds || left.channel - right.channel);
}

function findCursorAt(positionSeconds) {
    const safePosition = Math.max(0, Number(positionSeconds) || 0);
    let low = 0;
    let high = scheduledEvents.length;
    while (low < high) {
        const middle = (low + high) >>> 1;
        if (scheduledEvents[middle].startSeconds < safePosition) {
            low = middle + 1;
        } else {
            high = middle;
        }
    }
    return low;
}

export async function preloadAudio() {
    await ensureSynthesizer();
}

export async function primeAudio() {
    await ensureSynthesizer();
    await audioContext.resume();
}

export async function startSession(events, mixer) {
    await ensureSynthesizer();
    await audioContext.resume();
    stopSession();

    scheduledEvents = sortEvents(events);
    eventCursor = 0;
    playbackDuration = scheduledEvents.reduce(
        (maximum, note) => Math.max(maximum, note.startSeconds + note.durationSeconds),
        0);
    playbackStart = audioContext.currentTime + 0.08;
    configurePrograms();
    setMixer(mixer);

    // Queue the complete short launch plan before returning to .NET. The WASM
    // thread can then expand the selected song without starving the JS timer.
    schedulePendingThrough(Number.POSITIVE_INFINITY);
    schedulerTimer = window.setInterval(schedulerTick, SCHEDULER_INTERVAL_MS);
}

export function appendSession(events, durationSeconds) {
    if (!synthesizer || !audioContext || playbackStart === null) {
        return;
    }

    const additions = sortEvents(events);
    if (additions.length > 0) {
        scheduledEvents.push(...additions);
    }
    playbackDuration = Math.max(playbackDuration, Number(durationSeconds) || 0);
    schedulerTick();
    if (schedulerTimer === null) {
        schedulerTimer = window.setInterval(schedulerTick, SCHEDULER_INTERVAL_MS);
    }
}

export function replaceContinuation(events, durationSeconds, boundarySeconds) {
    if (!synthesizer || !audioContext || playbackStart === null) {
        return;
    }

    const boundary = Math.max(0, Number(boundarySeconds) || 0);
    const prefix = scheduledEvents.filter(note => note.startSeconds < boundary);
    scheduledEvents = prefix.concat(sortEvents(events));
    playbackDuration = Math.max(0, Number(durationSeconds) || 0);
    // The prefix was completely queued by startSession. Resume scheduling at
    // the replacement continuation without re-queuing the launch material.
    eventCursor = prefix.length;
    schedulerTick();
    if (schedulerTimer === null) {
        schedulerTimer = window.setInterval(schedulerTick, SCHEDULER_INTERVAL_MS);
    }
}

export function replaceSession(events, durationSeconds, positionSeconds, rebasePosition = false) {
    if (!synthesizer || !audioContext || playbackStart === null) {
        return;
    }
    clearScheduler();
    const safePosition = Math.max(0, Number(positionSeconds) || 0);
    if (rebasePosition) {
        synthesizer.stopAll(true);
        configurePrograms();
        setMixer(mixerState);
        playbackStart = audioContext.currentTime - safePosition;
    }
    scheduledEvents = sortEvents(events);
    playbackDuration = Math.max(0, Number(durationSeconds) || 0);
    // When the timeline is rebased (for a live tempo change), all previously
    // queued notes were stopped above. Start scheduling at the exact new
    // position so the first look-ahead window is not silently skipped. For a
    // non-rebased replacement, the old plan already owns that protected window.
    eventCursor = findCursorAt(safePosition + (rebasePosition ? 0 : LOOK_AHEAD_SECONDS));
    schedulerTick();
    schedulerTimer = window.setInterval(schedulerTick, SCHEDULER_INTERVAL_MS);
}

export function stopSession() {
    clearScheduler();
    scheduledEvents = [];
    eventCursor = 0;
    playbackDuration = 0;
    playbackStart = null;
    if (synthesizer) {
        synthesizer.stopAll(true);
    }
}

export function panic() {
    stopSession();
    if (synthesizer) {
        synthesizer.reset();
        configurePrograms();
        setMixer(mixerState);
    }
}

export function setMixer(mixer) {
    mixerState = {
        pianoEnabled: Boolean(mixer?.pianoEnabled),
        bassEnabled: Boolean(mixer?.bassEnabled),
        drumsEnabled: Boolean(mixer?.drumsEnabled),
        midiThruEnabled: Boolean(mixer?.midiThruEnabled),
        pianoVolume: clampMidi(mixer?.pianoVolume),
        bassVolume: clampMidi(mixer?.bassVolume),
        drumsVolume: clampMidi(mixer?.drumsVolume),
        vibraphoneVolume: clampMidi(mixer?.vibraphoneVolume)
    };

    if (!synthesizer) {
        return;
    }

    synthesizer.controllerChange(PIANO_CHANNEL, 7,
        mixerState.pianoEnabled ? mixerState.pianoVolume : 0);
    synthesizer.controllerChange(BASS_CHANNEL, 7,
        mixerState.bassEnabled ? mixerState.bassVolume : 0);
    synthesizer.controllerChange(DRUMS_CHANNEL, 7,
        mixerState.drumsEnabled ? mixerState.drumsVolume : 0);
    synthesizer.controllerChange(VIBRAPHONE_CHANNEL, 7,
        mixerState.midiThruEnabled ? mixerState.vibraphoneVolume : 0);
}

export function getPosition() {
    if (!audioContext || playbackStart === null) {
        return 0;
    }
    return Math.max(0, audioContext.currentTime - playbackStart);
}

export async function getMidiInputs() {
    if (!navigator.requestMIDIAccess) {
        return [];
    }
    midiAccess ??= await navigator.requestMIDIAccess({ sysex: false });
    return [...midiAccess.inputs.values()].map(input => ({
        id: input.id,
        name: input.name || input.manufacturer || "MIDI input"
    }));
}

export async function selectMidiInput(inputId, dotNetReference) {
    if (!navigator.requestMIDIAccess) {
        throw new Error("Web MIDI is not supported by this browser.");
    }
    midiAccess ??= await navigator.requestMIDIAccess({ sysex: false });
    if (activeMidiInput) {
        activeMidiInput.onmidimessage = null;
        activeMidiInput = null;
    }
    midiDotNetReference = dotNetReference || null;
    if (!inputId) {
        return;
    }

    const input = midiAccess.inputs.get(inputId);
    if (!input) {
        throw new Error("The selected MIDI input is no longer available.");
    }
    activeMidiInput = input;
    activeMidiInput.onmidimessage = event => {
        const data = event.data || [];
        const status = data[0] ?? 0;
        const data1 = data[1] ?? 0;
        const data2 = data[2] ?? 0;
        if (midiDotNetReference) {
            void midiDotNetReference.invokeMethodAsync("ReceiveMidiMessage", status, data1, data2);
        }
        if (synthesizer && mixerState.midiThruEnabled) {
            const command = status & 0xf0;
            const channelStatus = command | VIBRAPHONE_CHANNEL;
            if (command === 0x80 || command === 0x90 || command === 0xb0 || command === 0xe0) {
                synthesizer.sendMessage([channelStatus, data1, data2]);
            } else if (command === 0xd0) {
                synthesizer.sendMessage([channelStatus, data1]);
            }
        }
    };
}

export async function dispose() {
    stopSession();
    if (activeMidiInput) {
        activeMidiInput.onmidimessage = null;
    }
    activeMidiInput = null;
    midiDotNetReference = null;
    if (synthesizer) {
        synthesizer.disconnect();
        synthesizer.destroy();
        synthesizer = null;
    }
    if (audioContext && audioContext.state !== "closed") {
        await audioContext.close();
    }
    audioContext = null;
}

function clampMidi(value) {
    const number = Number(value);
    if (!Number.isFinite(number)) {
        return 127;
    }
    // The app exposes mixer values as 0-100, then converts them to MIDI CC 0-127.
    const percent = Math.max(0, Math.min(100, number));
    return Math.round(percent * 127 / 100);
}
