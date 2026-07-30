import { WorkletSynthesizer } from "spessasynth_lib";

const PIANO_CHANNEL = 2;
const BASS_CHANNEL = 1;
const DRUMS_CHANNEL = 9;
const LOOK_AHEAD_SECONDS = 0.12;
const SCHEDULER_INTERVAL_MS = 24;

let audioContext;
let synthesizer;
let scheduledEvents = [];
let eventCursor = 0;
let playbackStart = null;
let playbackDuration = 0;
let schedulerTimer = null;
let mixerState = {
    pianoEnabled: true,
    bassEnabled: true,
    drumsEnabled: true,
    pianoVolume: 100,
    bassVolume: 100,
    drumsVolume: 100
};

async function ensureSynthesizer() {
    if (synthesizer) {
        return;
    }

    const AudioContextClass = window.AudioContext || window.webkitAudioContext;
    if (!AudioContextClass) {
        throw new Error("This browser does not support Web Audio.");
    }

    audioContext = new AudioContextClass({ latencyHint: "interactive" });
    const processorUrl = new URL("./spessasynth_processor.min.js", import.meta.url);
    await audioContext.audioWorklet.addModule(processorUrl.href);

    synthesizer = new WorkletSynthesizer(audioContext, {
        oneOutput: true,
        eventsEnabled: false
    });
    synthesizer.setLogLevel(false, true, false);

    const soundFontUrl = new URL("../soundfonts/FluidR3_Jampanion.sf3", import.meta.url);
    const response = await fetch(soundFontUrl);
    if (!response.ok) {
        throw new Error(`SoundFont download failed (${response.status}).`);
    }

    await synthesizer.soundBankManager.addSoundBank(await response.arrayBuffer(), "jampanion");
    await synthesizer.isReady;
    configurePrograms();
    setMixer(mixerState);
}

function configurePrograms() {
    synthesizer.programChange(PIANO_CHANNEL, 0);
    synthesizer.programChange(BASS_CHANNEL, 32);
    synthesizer.programChange(DRUMS_CHANNEL, 0);
}

function schedulerTick() {
    if (!synthesizer || !audioContext || playbackStart === null) {
        return;
    }

    const horizon = audioContext.currentTime + LOOK_AHEAD_SECONDS;
    while (eventCursor < scheduledEvents.length) {
        const note = scheduledEvents[eventCursor];
        const startTime = playbackStart + note.startSeconds;
        if (startTime > horizon) {
            break;
        }

        const endTime = startTime + Math.max(0.01, note.durationSeconds);
        synthesizer.noteOn(note.channel, note.noteNumber, note.velocity, { time: startTime });
        synthesizer.noteOff(note.channel, note.noteNumber, { time: endTime });
        eventCursor += 1;
    }

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

export async function startSession(events, mixer) {
    await ensureSynthesizer();
    await audioContext.resume();
    stopSession();

    scheduledEvents = [...events].sort((left, right) =>
        left.startSeconds - right.startSeconds || left.channel - right.channel);
    eventCursor = 0;
    playbackDuration = scheduledEvents.reduce(
        (maximum, note) => Math.max(maximum, note.startSeconds + note.durationSeconds),
        0);
    playbackStart = audioContext.currentTime + 0.08;
    configurePrograms();
    setMixer(mixer);

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
        pianoVolume: clampMidi(mixer?.pianoVolume),
        bassVolume: clampMidi(mixer?.bassVolume),
        drumsVolume: clampMidi(mixer?.drumsVolume)
    };

    if (!synthesizer) {
        return;
    }

    synthesizer.controllerChange(
        PIANO_CHANNEL,
        7,
        mixerState.pianoEnabled ? mixerState.pianoVolume : 0);
    synthesizer.controllerChange(
        BASS_CHANNEL,
        7,
        mixerState.bassEnabled ? mixerState.bassVolume : 0);
    synthesizer.controllerChange(
        DRUMS_CHANNEL,
        7,
        mixerState.drumsEnabled ? mixerState.drumsVolume : 0);
}

export function getPosition() {
    if (!audioContext || playbackStart === null) {
        return 0;
    }

    return Math.max(0, audioContext.currentTime - playbackStart);
}

export async function dispose() {
    stopSession();
    if (synthesizer) {
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
        return 100;
    }
    return Math.max(0, Math.min(127, Math.round(number)));
}
