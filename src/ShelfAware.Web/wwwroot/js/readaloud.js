// Step-by-step audio player for recipe read-aloud (Phase 3, button-controlled — no barge-in yet).
// The server synthesizes each segment (recipe name, then each cooking step) to audio and loads them
// here as a playlist; this owns sequential playback + pause/resume/prev/next/stop and auto-advance,
// and reports the current segment index back to .NET so the UI can highlight the step being read.

let segments = [];   // [{ base64, mime }]
let index = 0;
let audio = null;
let dotnet = null;   // DotNetObjectReference: OnIndex(int), OnFinished()
let stopped = true;

export function load(segs, dotnetRef) {
    stop();
    segments = segs || [];
    dotnet = dotnetRef;
    index = 0;
    stopped = false;
}

export function playFrom(i) {
    if (i < 0 || i >= segments.length) { finish(); return; }
    index = i;
    stopped = false;
    playCurrent();
}

export function pause() {
    if (audio) audio.pause();
}

export function resume() {
    if (audio && audio.paused && !stopped) audio.play().catch(() => {});
}

// Flip play/paused and return the resulting paused state (so .NET can label the button).
export function togglePause() {
    if (!audio) return true;
    if (audio.paused) { resume(); return false; }
    pause();
    return true;
}

export function next() {
    if (index + 1 < segments.length) playFrom(index + 1);
    else finish();
}

export function prev() {
    playFrom(index > 0 ? index - 1 : 0);
}

export function stop() {
    stopped = true;
    stopAudio();
    index = 0;
}

function playCurrent() {
    stopAudio();
    const seg = segments[index];
    if (!seg) { finish(); return; }
    audio = new Audio(`data:${seg.mime};base64,${seg.base64}`);
    audio.onended = () => { if (!stopped) next(); };
    audio.onerror = () => { if (!stopped) next(); };
    notifyIndex();
    audio.play().catch(() => {});
}

function stopAudio() {
    if (audio) {
        audio.onended = null;
        audio.onerror = null;
        audio.pause();
        audio = null;
    }
}

function finish() {
    stopped = true;
    stopAudio();
    if (dotnet) dotnet.invokeMethodAsync('OnFinished');
}

function notifyIndex() {
    if (dotnet) dotnet.invokeMethodAsync('OnIndex', index);
}
