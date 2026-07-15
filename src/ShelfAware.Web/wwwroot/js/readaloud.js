// Step-by-step audio player for recipe read-aloud (button-controlled — no barge-in here).
// The server synthesizes each segment (recipe name, then each cooking step) to audio and loads them
// here as a playlist; this owns sequential playback + pause/resume/prev/next/stop and auto-advance,
// and reports the current segment index back to .NET so the UI can highlight the step being read.
//
// The playlist STREAMS: .NET loads segment 0 and starts playback immediately, then appends the rest
// as they finish synthesizing, so you hear the recipe about as fast as one round-trip instead of
// waiting for the whole narration. That means playback can legitimately outrun the playlist — when it
// does we park on `wantIndex` and resume from append(), rather than mistaking "nothing queued yet"
// for "the recipe is over".

let segments = [];      // [{ base64, mime }]
let index = 0;          // segment currently playing
let wantIndex = -1;     // >= 0: playback outran synthesis; play this the moment it arrives
let expectingMore = false;
let autoAdvance = true; // false = hands-free: stop at each step and let .NET listen
let audio = null;
let dotnet = null;      // DotNetObjectReference: OnIndex(int), OnFinished(), OnBuffering(bool), OnStepFinished(int)
let stopped = true;

export function load(segs, dotnetRef, more, auto) {
    stop();
    segments = segs || [];
    dotnet = dotnetRef;
    index = 0;
    wantIndex = -1;
    expectingMore = !!more;
    // Button-driven reading runs the recipe end to end; hands-free stops after each step so the cook
    // gets a turn. Same playlist either way — the difference is only who says "next".
    autoAdvance = auto !== false;
    stopped = false;
}

// Queue another segment. Resumes playback if we were parked waiting for exactly this one.
export function append(seg) {
    segments.push(seg);
    if (wantIndex >= 0 && wantIndex < segments.length && !stopped) {
        const i = wantIndex;
        wantIndex = -1;
        notifyBuffering(false);
        playFrom(i);
    }
}

// No further segments are coming (synthesis finished, or failed and gave up).
export function endOfStream() {
    expectingMore = false;
    if (wantIndex >= 0) {
        wantIndex = -1;
        notifyBuffering(false);
        finish();
    }
}

export function playFrom(i) {
    if (i < 0) i = 0;
    if (i >= segments.length) {
        // Past the end: either the narration is still being synthesized, or it's genuinely over.
        if (expectingMore && !stopped) {
            wantIndex = i;
            notifyBuffering(true);
        } else {
            finish();
        }
        return;
    }
    wantIndex = -1;
    index = i;
    stopped = false;
    playCurrent();
}

// An ENDED clip reports paused === true, and play()ing one seeks back to zero and reads the step again.
// That matters because "hold on" is most naturally said during the listening window — i.e. exactly when
// the step's audio has just finished — so pausing and resuming there must be a no-op, not a rewind.
function isLive() {
    return !!audio && !audio.ended && !stopped;
}

export function pause() {
    if (isLive()) audio.pause();
}

// Returns whether there was actually something to resume, so the caller can tell "carry on reading"
// from "there was nothing playing — keep listening".
export function resume() {
    if (!isLive() || !audio.paused) return false;
    audio.play().catch(() => {});
    return true;
}

// Flip play/paused and return the resulting paused state (so .NET can label the button).
export function togglePause() {
    if (!isLive()) return false; // nothing playing: the button shouldn't offer to pause silence
    if (audio.paused) { resume(); return false; }
    pause();
    return true;
}

export function next() {
    playFrom(index + 1);
}

export function prev() {
    playFrom(index - 1);
}

export function stop() {
    stopped = true;
    expectingMore = false;
    wantIndex = -1;
    stopAudio();
    index = 0;
}

function playCurrent() {
    stopAudio();
    const seg = segments[index];
    if (!seg) { finish(); return; }
    audio = new Audio(`data:${seg.mime};base64,${seg.base64}`);
    audio.onended = () => { if (!stopped) segmentEnded(); };
    audio.onerror = () => { if (!stopped) segmentEnded(); };
    notifyIndex();
    audio.play().catch(() => {});
}

function segmentEnded() {
    if (autoAdvance) { next(); return; }
    if (dotnet) dotnet.invokeMethodAsync('OnStepFinished', index);
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
    wantIndex = -1;
    stopAudio();
    if (dotnet) dotnet.invokeMethodAsync('OnFinished');
}

function notifyIndex() {
    if (dotnet) dotnet.invokeMethodAsync('OnIndex', index);
}

function notifyBuffering(on) {
    if (dotnet) dotnet.invokeMethodAsync('OnBuffering', on);
}
