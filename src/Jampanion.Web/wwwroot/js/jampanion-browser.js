let scheduledAudioPreload = false;
let globalShortcutReference = null;
let globalShortcutHandler = null;
let lastVisibleCurrentBar = null;


export async function loadSongIndex(indexKey, legacyKey, sourcePrefix) {
    const current = window.localStorage.getItem(indexKey);
    if (current) {
        return current;
    }

    const legacy = window.localStorage.getItem(legacyKey);
    if (!legacy) {
        return null;
    }

    let songs;
    try {
        songs = JSON.parse(legacy);
    } catch {
        return null;
    }
    if (!Array.isArray(songs)) {
        return null;
    }

    const index = [];
    for (let position = 0; position < songs.length; position += 1) {
        const song = songs[position];
        const id = song?.Id ?? song?.id;
        const title = song?.Title ?? song?.title;
        const source = song?.Source ?? song?.source;
        if (typeof id !== "string" || typeof title !== "string" ||
            typeof source !== "string" || !id || !title || !source) {
            continue;
        }

        index.push({ Id: id, Title: title });
        window.localStorage.setItem(`${sourcePrefix}${encodeURIComponent(id)}`, source);

        // Keep the first interactive paint responsive even for a large legacy
        // all-in-one song library.
        if (position > 0 && position % 8 === 0) {
            await yieldToBrowser(1);
        }
    }

    const serialized = JSON.stringify(index);
    window.localStorage.setItem(indexKey, serialized);
    window.localStorage.removeItem(legacyKey);
    return serialized;
}

export function storageGet(key) {
    return window.localStorage.getItem(key);
}

export function storageSet(key, value) {
    window.localStorage.setItem(key, value);
}

export function storageRemove(key) {
    window.localStorage.removeItem(key);
}

export function downloadText(fileName, content) {
    const blob = new Blob([content], { type: "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
}

export function selectElementText(id) {
    requestAnimationFrame(() => {
        const element = document.getElementById(id);
        if (!element || typeof element.select !== "function") {
            return;
        }
        element.select();
    });
}

export function focusElement(id, selectAll = true) {
    requestAnimationFrame(() => {
        const element = document.getElementById(id);
        if (!element) {
            return;
        }
        element.focus();
        if (selectAll && typeof element.select === "function") {
            element.select();
        } else if ("selectionStart" in element && typeof element.value === "string") {
            const end = element.value.length;
            element.setSelectionRange(end, end);
        }
    });
}

export function blurElement(id) {
    requestAnimationFrame(() => {
        document.getElementById(id)?.blur();
    });
}

export function scrollElementIntoView(id) {
    requestAnimationFrame(() => {
        document.getElementById(id)?.scrollIntoView({ block: "nearest" });
    });
}

export function yieldToBrowser(frameCount = 1) {
    const frames = Math.max(1, Number(frameCount) || 1);
    return new Promise((resolve) => {
        let remaining = frames;
        const step = () => {
            remaining -= 1;
            if (remaining <= 0) {
                window.setTimeout(resolve, 0);
                return;
            }
            window.requestAnimationFrame(step);
        };
        window.requestAnimationFrame(step);
    });
}

let chordFitFrame = 0;
let observedChartElement = null;
let chordResizeObserver = null;
let lastObservedChartWidth = -1;
const chordMeasureCanvas = document.createElement("canvas");
const chordMeasureContext = chordMeasureCanvas.getContext("2d");

function maximumChordFontSize(chordCount, scale) {
    const count = Math.max(1, Number(chordCount) || 1);
    const base = count === 1 ? 22 : count === 2 ? 20 : count === 3 ? 18 : 16;
    return Math.max(8, base * scale);
}

function measuredLineWidth(text, fontSize) {
    if (!chordMeasureContext) {
        return text.length * fontSize * 0.62;
    }
    chordMeasureContext.font = `600 ${fontSize}px Arial, sans-serif`;
    return text.split("\n").reduce(
        (maximum, line) => Math.max(maximum, chordMeasureContext.measureText(line).width),
        0);
}

function chordLabelFits(text, fontSize, availableWidth, availableHeight) {
    const lines = Math.max(1, text.split("\n").length);
    const lineHeight = fontSize * 1.05;
    return measuredLineWidth(text, fontSize) <= availableWidth + 0.5 &&
        lines * lineHeight <= availableHeight + 0.5;
}

function fitOneChordLabel(label, scale) {
    const segment = label.parentElement;
    if (!segment) {
        return;
    }

    const maximum = maximumChordFontSize(label.dataset.chordCount, scale);
    const minimum = 8;
    const availableWidth = Math.max(8, segment.clientWidth - 13);
    const availableHeight = Math.max(8, segment.clientHeight - 6);
    const text = label.textContent || "";

    let fontSize = maximum;
    if (!chordLabelFits(text, maximum, availableWidth, availableHeight)) {
        if (!chordLabelFits(text, minimum, availableWidth, availableHeight)) {
            fontSize = minimum;
        } else {
            let lower = minimum;
            let upper = maximum;
            for (let iteration = 0; iteration < 12; iteration += 1) {
                const candidate = (lower + upper) / 2;
                if (chordLabelFits(text, candidate, availableWidth, availableHeight)) {
                    lower = candidate;
                } else {
                    upper = candidate;
                }
            }
            fontSize = Math.floor(lower * 4) / 4;
        }
    }

    const nextValue = `${fontSize}px`;
    if (label.style.fontSize !== nextValue) {
        label.style.fontSize = nextValue;
    }
}

function runChordLabelFit() {
    chordFitFrame = 0;
    const chart = document.querySelector(".chart-grid");
    if (!chart) {
        return;
    }

    const scaleText = getComputedStyle(chart).getPropertyValue("--chart-scale");
    const scale = Math.max(0.6, Math.min(1.5, Number.parseFloat(scaleText) || 1));
    for (const label of chart.querySelectorAll(".chord-segment > span")) {
        fitOneChordLabel(label, scale);
    }
}

function scheduleChordLabelFit() {
    if (chordFitFrame) {
        return;
    }
    chordFitFrame = requestAnimationFrame(runChordLabelFit);
}

function observeChartWidth() {
    const chart = document.querySelector(".chart-grid");
    const scroll = document.querySelector(".chart-scroll");
    const target = scroll || chart;
    if (!target || target === observedChartElement) {
        return;
    }

    chordResizeObserver?.disconnect();
    observedChartElement = target;
    lastObservedChartWidth = target.getBoundingClientRect().width;
    chordResizeObserver = new ResizeObserver((entries) => {
        const width = entries[0]?.contentRect?.width ?? target.getBoundingClientRect().width;
        if (Math.abs(width - lastObservedChartWidth) < 0.5) {
            return;
        }
        lastObservedChartWidth = width;
        scheduleChordLabelFit();
    });
    chordResizeObserver.observe(target);
}

export function fitChordLabels() {
    observeChartWidth();
    scheduleChordLabelFit();
    if (document.fonts?.ready) {
        void document.fonts.ready.then(scheduleChordLabelFit);
    }
}

export function keepCurrentChartRowVisible() {
    const currentBar = document.querySelector('.bar-cell[data-current="true"]');
    if (!currentBar || currentBar === lastVisibleCurrentBar) {
        return;
    }

    lastVisibleCurrentBar = currentBar;
    const scroll = currentBar.closest(".chart-scroll");
    const row = currentBar.closest(".chart-row");
    if (!scroll || !row || scroll.scrollHeight <= scroll.clientHeight + 1) {
        return;
    }

    const scrollRect = scroll.getBoundingClientRect();
    const rowRect = row.getBoundingClientRect();
    const rowCenter = rowRect.top + rowRect.height / 2;
    const safeTop = scrollRect.top + scrollRect.height * 0.20;
    const safeBottom = scrollRect.top + scrollRect.height * 0.80;
    if (rowCenter >= safeTop && rowCenter <= safeBottom) {
        return;
    }

    const rowTopWithinScroll = rowRect.top - scrollRect.top + scroll.scrollTop;
    const desired = rowTopWithinScroll + rowRect.height / 2 - scroll.clientHeight * 0.20;
    scroll.scrollTo({
        top: Math.max(0, Math.min(desired, scroll.scrollHeight - scroll.clientHeight)),
        behavior: "auto"
    });
}

export function registerGlobalShortcuts(dotNetReference) {
    globalShortcutReference = dotNetReference;
    if (globalShortcutHandler) {
        return;
    }

    globalShortcutHandler = (event) => {
        if (event.repeat || event.altKey || event.ctrlKey || event.metaKey) {
            return;
        }

        if (event.key === "Escape" && document.querySelector(".settings-window")) {
            event.preventDefault();
            void globalShortcutReference?.invokeMethodAsync("HandleEscapeShortcutAsync");
            return;
        }

        if (event.code !== "Space") {
            return;
        }

        const target = event.target;
        if (target instanceof HTMLInputElement ||
            target instanceof HTMLTextAreaElement ||
            target instanceof HTMLSelectElement ||
            target instanceof HTMLButtonElement ||
            target?.isContentEditable) {
            return;
        }

        event.preventDefault();
        void globalShortcutReference?.invokeMethodAsync("HandleSpaceShortcutAsync");
    };
    window.addEventListener("keydown", globalShortcutHandler, true);
}

export function unregisterGlobalShortcuts() {
    if (globalShortcutHandler) {
        window.removeEventListener("keydown", globalShortcutHandler, true);
    }
    globalShortcutHandler = null;
    globalShortcutReference = null;
}

window.addEventListener("resize", scheduleChordLabelFit, { passive: true });

// Keep the first interactive paint independent of the synth. Startup only
// warms the browser cache for the small bundled SoundFont request. Full
// SpessaSynth/AudioWorklet initialization starts after the first genuine user
// interaction during idle time, or immediately when Start Session needs it.
export function scheduleAudioPreload() {
    if (scheduledAudioPreload) {
        return;
    }
    scheduledAudioPreload = true;

    // Warm only the browser HTTP cache. Parsing the SF3 and constructing the
    // AudioWorklet on the main thread can block Blazor input for several
    // seconds, so full audio initialization is reserved for Start Session.
    const soundFontUrl = new URL("../soundfonts/FluidR3_Jampanion.sf3", import.meta.url);
    const warmCache = () => {
        void fetch(soundFontUrl, { cache: "force-cache" }).catch(() => {});
    };

    if ("requestIdleCallback" in window) {
        window.requestIdleCallback(warmCache, { timeout: 4000 });
    } else {
        window.setTimeout(warmCache, 500);
    }
}

export function confirmAction(message) {
    return window.confirm(String(message ?? "Are you sure?"));
}

export async function clearLocalSongStorage(indexKey, legacyKey, sourcePrefix) {
    const keys = [];
    for (let index = 0; index < window.localStorage.length; index += 1) {
        const key = window.localStorage.key(index);
        if (key && key.startsWith(sourcePrefix)) {
            keys.push(key);
        }
    }

    for (let index = 0; index < keys.length; index += 1) {
        window.localStorage.removeItem(keys[index]);
        if (index > 0 && index % 32 === 0) {
            await yieldToBrowser(1);
        }
    }

    window.localStorage.removeItem(indexKey);
    window.localStorage.removeItem(legacyKey);
    return keys.length;
}
