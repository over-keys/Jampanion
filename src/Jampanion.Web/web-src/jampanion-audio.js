import { WorkletSynthesizer } from "spessasynth_lib";

const VIBRAPHONE_CHANNEL = 0;
const BASS_CHANNEL = 1;
const PIANO_CHANNEL = 2;
const DRUMS_CHANNEL = 9;
const LOOK_AHEAD_SECONDS = 0.12;
const SCHEDULER_INTERVAL_MS = 24;
const AUDIO_BUILD_ID = "jampanion-audio-v22";
const SHARED_AUDIO_CONTEXT_KEY = "__jampanionAudioContext";
// SpessaSynth applies a 0.6 panning gain correction to each channel. Compensate
// for it at the master stage so the browser synth has comparable output to the
// desktop synth without changing the per-channel mixer values.
const WEB_MASTER_GAIN = 1 / 0.6;

let audioContext;
let synthesizer;
let synthesizerPromise;
let scheduledEvents = [];
let eventCursor = 0;
let scheduledThroughSeconds = 0;
let playbackStart = null;
let playbackDuration = 0;
let schedulerTimer = null;
let midiAccess = null;
let activeMidiInput = null;
let activeMidiOutput = null;
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

    configurePlaybackAudioSession();
    audioContext = getOrCreateAudioContext(AudioContextClass);

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
    synthesizer.setSystemParameter("gain", WEB_MASTER_GAIN);
    configurePrograms();
    setMixer(mixerState);
    return synthesizer;
}

function getOrCreateAudioContext(AudioContextClass) {
    if (audioContext && audioContext.state !== "closed") {
        return audioContext;
    }

    const sharedContext = window[SHARED_AUDIO_CONTEXT_KEY];
    if (sharedContext && sharedContext.state !== "closed" &&
        typeof sharedContext.resume === "function") {
        audioContext = sharedContext;
        return audioContext;
    }

    try {
        audioContext = new AudioContextClass({ latencyHint: "interactive" });
    } catch {
        // Older WebKit builds accept the legacy constructor without options.
        audioContext = new AudioContextClass();
    }
    window[SHARED_AUDIO_CONTEXT_KEY] = audioContext;
    return audioContext;
}

function configurePlaybackAudioSession() {
    // iOS Safari defaults Web Audio to the ambient audio session, which is
    // silenced by the hardware Ring/Silent switch. The Audio Session API is
    // unavailable on older Safari and other browsers, so keep this optional.
    const audioSession = navigator.audioSession;
    if (!audioSession || typeof audioSession !== "object") {
        return;
    }
    try {
        audioSession.type = "playback";
    } catch {
        // Unsupported or restricted implementations can continue with the
        // normal Web Audio behavior.
    }
}

function resumeAudioAfterPageWake() {
    if (!audioContext || audioContext.state === "closed") {
        return;
    }
    void resumeAudioContext().catch(() => {
        // A user gesture may still be required by older iOS Safari after a
        // page interruption; the next Start/Resume interaction retries it.
    });
}

if (typeof document !== "undefined") {
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible") {
            resumeAudioAfterPageWake();
        }
    });
}
if (typeof window !== "undefined") {
    window.addEventListener("pageshow", resumeAudioAfterPageWake);
    window.addEventListener("focus", resumeAudioAfterPageWake);
}

function resumeAudioContext() {
    if (!audioContext) {
        const AudioContextClass = window.AudioContext || window.webkitAudioContext;
        if (!AudioContextClass) {
            throw new Error("This browser does not support Web Audio.");
        }
        audioContext = getOrCreateAudioContext(AudioContextClass);
    }
    configurePlaybackAudioSession();
    return audioContext.state !== "running" && audioContext.state !== "closed"
        ? audioContext.resume()
        : Promise.resolve();
}

function configurePrograms() {
    if (activeMidiOutput) {
        sendExternalMessage([0xc0 | VIBRAPHONE_CHANNEL, 11]);
        sendExternalMessage([0xc0 | PIANO_CHANNEL, 0]);
        sendExternalMessage([0xc0 | BASS_CHANNEL, 32]);
        sendExternalMessage([0xc0 | DRUMS_CHANNEL, 0]);
        return;
    }
    if (!synthesizer) {
        return;
    }

    synthesizer.programChange(VIBRAPHONE_CHANNEL, 11);
    synthesizer.programChange(PIANO_CHANNEL, 0);
    synthesizer.programChange(BASS_CHANNEL, 32);
    synthesizer.programChange(DRUMS_CHANNEL, 0);
}

function sendExternalMessage(message, audioTime = null) {
    if (!activeMidiOutput) {
        return;
    }

    if (audioTime === null || !audioContext) {
        activeMidiOutput.send(message);
        return;
    }

    const delayMilliseconds = Math.max(
        0,
        (audioTime - audioContext.currentTime) * 1000);
    activeMidiOutput.send(message, performance.now() + delayMilliseconds);
}

function scheduleNote(note, startTime, endTime) {
    if (activeMidiOutput) {
        sendExternalMessage(
            [0x90 | note.channel, note.noteNumber, note.velocity],
            startTime);
        sendExternalMessage(
            [0x80 | note.channel, note.noteNumber, 0],
            endTime);
        return;
    }

    synthesizer.noteOn(note.channel, note.noteNumber, note.velocity, { time: startTime });
    synthesizer.noteOff(note.channel, note.noteNumber, { time: endTime });
}

function clearExternalOutput(output = activeMidiOutput) {
    if (!output) {
        return;
    }

    if (typeof output.clear === "function") {
        output.clear();
    }
    for (const channel of [VIBRAPHONE_CHANNEL, BASS_CHANNEL, PIANO_CHANNEL, DRUMS_CHANNEL]) {
        output.send([0xb0 | channel, 120, 0]);
        output.send([0xb0 | channel, 123, 0]);
    }
}

async function ensureMidiAccess() {
    if (!navigator.requestMIDIAccess) {
        throw new Error("Web MIDI is not supported by this browser.");
    }
    midiAccess ??= await navigator.requestMIDIAccess({ sysex: false });
    return midiAccess;
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
            scheduleNote(note, startTime, endTime);
        }
        eventCursor += 1;
    }

    const relativeHorizon = horizon - playbackStart;
    if (Number.isFinite(relativeHorizon))
    {
        scheduledThroughSeconds = Math.max(scheduledThroughSeconds, relativeHorizon);
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
    // Start the resume call before any await so a directly invoked Start
    // handler can still satisfy iOS Safari's transient user-activation rule.
    const resumePromise = resumeAudioContext();
    await ensureSynthesizer();
    await resumePromise;
    await resumeAudioContext();
}

export async function startSession(events, mixer) {
    const resumePromise = resumeAudioContext();
    await ensureSynthesizer();
    await resumePromise;
    await resumeAudioContext();
    stopSession();

    scheduledEvents = sortEvents(events);
    eventCursor = 0;
    playbackDuration = scheduledEvents.reduce(
        (maximum, note) => Math.max(maximum, note.startSeconds + note.durationSeconds),
        0);
    playbackStart = audioContext.currentTime + 0.08;
    configurePrograms();
    setMixer(mixer);

    // Queue only the normal look-ahead window. The .NET plan expansion
    // yields between four-bar builds, so future blocks remain replaceable.
    scheduledThroughSeconds = 0;
    schedulerTick();
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

    // Keep exactly the events already handed to the AudioWorklet. Unscheduled
    // notes before the boundary remain in the prefix and continue normally;
    // notes at and after the boundary come from the replacement plan.
    eventCursor = findCursorAt(scheduledThroughSeconds + 0.0001);
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
        if (activeMidiOutput) {
            clearExternalOutput();
        } else {
            synthesizer.stopAll(true);
        }
        configurePrograms();
        setMixer(mixerState);
        playbackStart = audioContext.currentTime - safePosition;
    }
    scheduledEvents = sortEvents(events);
    if (rebasePosition) {
        scheduledThroughSeconds = safePosition;
    }
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
    scheduledThroughSeconds = 0;
    playbackDuration = 0;
    playbackStart = null;
    clearExternalOutput();
    if (synthesizer) {
        synthesizer.controllerChange(PIANO_CHANNEL, 7, 0);
        synthesizer.controllerChange(BASS_CHANNEL, 7, 0);
        synthesizer.controllerChange(DRUMS_CHANNEL, 7, 0);
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

    if (activeMidiOutput) {
        sendExternalMessage([0xb0 | PIANO_CHANNEL, 7,
            mixerState.pianoEnabled ? mixerState.pianoVolume : 0]);
        sendExternalMessage([0xb0 | BASS_CHANNEL, 7,
            mixerState.bassEnabled ? mixerState.bassVolume : 0]);
        sendExternalMessage([0xb0 | DRUMS_CHANNEL, 7,
            mixerState.drumsEnabled ? mixerState.drumsVolume : 0]);
        sendExternalMessage([0xb0 | VIBRAPHONE_CHANNEL, 7,
            mixerState.midiThruEnabled ? mixerState.vibraphoneVolume : 0]);
        return;
    }
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
    const access = await ensureMidiAccess();
    return [...access.inputs.values()].map(input => ({
        id: input.id,
        name: input.name || input.manufacturer || "MIDI input"
    }));
}

export async function getMidiOutputs() {
    if (!navigator.requestMIDIAccess) {
        return [];
    }
    const access = await ensureMidiAccess();
    return [...access.outputs.values()].map(output => ({
        id: output.id,
        name: output.name || output.manufacturer || "MIDI output"
    }));
}

export function getSelectedMidiOutputId() {
    return activeMidiOutput?.id || "";
}

export async function selectMidiOutput(outputId) {
    if (playbackStart !== null) {
        throw new Error("Stop the session before changing MIDI output.");
    }

    const access = await ensureMidiAccess();
    const requestedId = String(outputId || "");
    if (activeMidiOutput?.id === requestedId) {
        return;
    }

    const previousOutput = activeMidiOutput;
    activeMidiOutput = null;
    clearExternalOutput(previousOutput);
    if (synthesizer) {
        synthesizer.stopAll(true);
    }

    if (requestedId) {
        const output = access.outputs.get(requestedId);
        if (!output) {
            configurePrograms();
            setMixer(mixerState);
            throw new Error("The selected MIDI output is no longer available.");
        }
        if (typeof output.open === "function") {
            await output.open();
        }
        activeMidiOutput = output;
    }

    configurePrograms();
    setMixer(mixerState);
}

export async function selectMidiInput(inputId, dotNetReference) {
    const access = await ensureMidiAccess();
    if (activeMidiInput) {
        activeMidiInput.onmidimessage = null;
        activeMidiInput = null;
    }
    midiDotNetReference = dotNetReference || null;
    if (!inputId) {
        return;
    }

    const input = access.inputs.get(inputId);
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
        if (mixerState.midiThruEnabled) {
            const command = status & 0xf0;
            const supported = command === 0x80 || command === 0x90 ||
                command === 0xb0 || command === 0xd0 || command === 0xe0;
            if (supported) {
                const channelStatus = command | VIBRAPHONE_CHANNEL;
                const message = command === 0xd0
                    ? [channelStatus, data1]
                    : [channelStatus, data1, data2];
                if (activeMidiOutput) {
                    activeMidiOutput.send(message);
                } else if (synthesizer) {
                    synthesizer.sendMessage(message);
                }
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
    if (activeMidiOutput) {
        clearExternalOutput();
        if (typeof activeMidiOutput.close === "function") {
            await activeMidiOutput.close();
        }
    }
    activeMidiOutput = null;
    if (synthesizer) {
        synthesizer.disconnect();
        synthesizer.destroy();
        synthesizer = null;
    }
    if (audioContext && audioContext.state !== "closed") {
        await audioContext.close();
    }
    if (window[SHARED_AUDIO_CONTEXT_KEY] === audioContext) {
        window[SHARED_AUDIO_CONTEXT_KEY] = null;
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
