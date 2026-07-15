// The ear for the built-in cook-along: opens a listening window between steps and hands one utterance
// back to .NET (which does STT -> grammar -> maybe the brain).
//
// Why this and not voice.js: push-to-talk re-acquires the microphone for every utterance, which is fine
// for a single bark but wrong for a loop that listens after every step — it would re-prompt, click, and
// waste a permission round-trip each time. Here the stream is held open for the session and each window
// records from it.
//
// Why half-duplex (we only listen BETWEEN steps, never while speaking): listening over our own playback
// is the hard problem — it needs echo cancellation good enough to hear "stop" under the voice saying the
// word "stop". Listening at a step boundary needs none of that, and a step boundary is where a cook
// actually talks. It costs the ability to interrupt mid-sentence, which is the honest trade.
//
// Two things keep this from being expensive:
//   1. A noise gate. A kitchen has an extractor fan, a tap, and a cook humming. Windows that never rise
//      above the room are dropped here and never become a speech-to-text call.
//   2. Endpointing. The window closes when they stop talking, not on a fixed timer, so a short "next"
//      returns immediately and a long question isn't guillotined mid-sentence.

let stream = null;
let audioCtx = null;
let analyser = null;
let source = null;
let noiseFloor = 0;
let cancelled = false;

const CUE_SECONDS = 0.13;

// Calibration lives in the browser because only the browser can hear the room — but the numbers it
// produces belong to the device, not the household: two people in one kitchen still have different
// microphones, and one of them is on a phone. Its OWN key, not the AI settings' key, because that store
// has a session-only mode for secrets and a calibration should outlive a closed tab.
const SETTINGS_KEY = 'shelfaware.listening';

export function loadSettings() {
    try {
        const raw = localStorage.getItem(SETTINGS_KEY);
        return raw ? JSON.parse(raw) : null;
    } catch {
        return null; // storage disabled/full — the caller falls back to defaults
    }
}

export function saveSettings(settings) {
    try {
        localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
        return true;
    } catch {
        return false;
    }
}

export function clearSettings() {
    try {
        localStorage.removeItem(SETTINGS_KEY);
    } catch {
        // Nothing to forget, or nowhere to forget it from.
    }
}

export function isSupported() {
    return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.MediaRecorder
        && (window.AudioContext || window.webkitAudioContext));
}

// Take the microphone for the whole cook-along and learn what this room sounds like when nobody's
// talking. Resolves { ok, floor } or { ok: false, error } — a refused mic must not throw into .NET,
// the reader just carries on without ears.
export async function startSession() {
    try {
        cancelled = false;
        stream = await navigator.mediaDevices.getUserMedia({
            // We do listen shortly after our own audio stops, so let the browser cancel what it can.
            audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true }
        });
        const Ctx = window.AudioContext || window.webkitAudioContext;
        audioCtx = new Ctx();
        if (audioCtx.state === 'suspended') await audioCtx.resume();
        source = audioCtx.createMediaStreamSource(stream);
        analyser = audioCtx.createAnalyser();
        analyser.fftSize = 2048;
        source.connect(analyser);

        noiseFloor = await measureFloorInternal(600);
        return { ok: true, floor: round(noiseFloor) };
    } catch (e) {
        await endSession();
        return { ok: false, error: (e && e.name) || 'unknown' };
    }
}

export async function endSession() {
    cancelled = true;
    try {
        if (stream) stream.getTracks().forEach(t => t.stop());
        if (source) source.disconnect();
        if (audioCtx && audioCtx.state !== 'closed') await audioCtx.close();
    } catch {
        // Tearing down a dead audio graph is not worth reporting — the session is over either way.
    }
    stream = null; audioCtx = null; analyser = null; source = null;
}

// A short tone so the cook knows the mic is open. Hands-free is unusable without it — with your hands in
// a bowl you can't look at the screen to find out whether it's your turn. Rising = listening, falling =
// done. Synthesized rather than shipped as an asset: it's two oscillator notes, and we already hold an
// AudioContext for the noise gate.
export async function beep(rising) {
    if (!audioCtx || audioCtx.state === 'closed') return;
    try {
        const t = audioCtx.currentTime;
        const osc = audioCtx.createOscillator();
        const gain = audioCtx.createGain();
        osc.connect(gain);
        gain.connect(audioCtx.destination);
        osc.frequency.setValueAtTime(rising ? 660 : 520, t);
        osc.frequency.linearRampToValueAtTime(rising ? 880 : 390, t + 0.08);
        // Quiet, and shaped — a square-edged blip reads as an error sound.
        gain.gain.setValueAtTime(0.0001, t);
        gain.gain.exponentialRampToValueAtTime(0.05, t + 0.01);
        gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.12);
        osc.start(t);
        osc.stop(t + CUE_SECONDS);
        // Outlast the tone before handing back, with a little room to settle. Returning early would open
        // the mic while the cue was still sounding, and the gate would hear US — a window that records
        // its own beep, transcribes nothing, and opens another one.
        await wait(CUE_SECONDS * 1000 + 60);
    } catch {
        // A cue we couldn't play is a worse UI, not a broken one — the window still opens.
    }
}

/// Listen for one utterance. Returns { heard: false } if the room never rose above its own noise floor
/// (no speech-to-text call is worth making for that), else { heard: true, audio, mimeType }.
///
/// `settings` is ListeningSettings from .NET — { silenceMs, openMs, maxMs, floorMultiple, minThreshold }.
/// The numbers live there, not here: they have defaults, clamping and a calibration policy that all want
/// to be testable without a microphone, and two sets of defaults would eventually disagree.
export async function listen(settings) {
    if (!stream || !analyser) return { heard: false, error: 'no-session' };

    const { openMs, maxMs, silenceMs } = settings;
    const threshold = speechThreshold(settings);
    const recorder = newRecorder();
    if (!recorder) return { heard: false, error: 'no-recorder' };

    const chunks = [];
    recorder.ondataavailable = e => { if (e.data && e.data.size > 0) chunks.push(e.data); };
    recorder.start();

    let spoke = false;
    let lastLoudAt = 0;
    const startedAt = Date.now();

    while (!cancelled) {
        const level = rms();
        const now = Date.now();
        const elapsed = now - startedAt;

        if (level > threshold) { spoke = true; lastLoudAt = now; }

        // They spoke and have now been quiet long enough to have finished.
        if (spoke && now - lastLoudAt >= silenceMs) break;
        // Nobody said anything in the window we opened — don't hold the mic hostage.
        if (!spoke && elapsed >= openMs) break;
        // Someone is monologuing (or the room is loud). Take what we have.
        if (elapsed >= maxMs) break;

        await wait(40);
    }

    const blob = await stopAndCollect(recorder, chunks);
    if (cancelled || !spoke || !blob || blob.size === 0) return { heard: false };

    const buffer = await blob.arrayBuffer();
    return {
        heard: true,
        audio: bytesToBase64(new Uint8Array(buffer)),
        mimeType: (blob.type || 'audio/webm').split(';')[0],
    };
}

// What counts as speech in THIS room. Relative to the floor measured at session start, so a kitchen with
// the extractor fan running doesn't read as continuous talking, with an absolute minimum so a silent room
// doesn't make the gate infinitely twitchy (any multiple of nearly-zero is still nearly-zero).
function speechThreshold(settings) {
    return Math.max(noiseFloor * settings.floorMultiple, settings.minThreshold);
}

// ---- Calibration -------------------------------------------------------------------------------
// Two primitives; .NET drives the sequence. Deliberately: the wizard's steps, the arithmetic turning
// measurements into settings, and the "we never heard you" case are all decisions, and decisions belong
// somewhere they can be read and tested. All this half does is hear.

/// The room with nobody talking. Also refreshes the session's floor, so calibrating re-baselines the
/// gate immediately rather than at the next cook-along.
export async function measureFloor(ms) {
    if (!analyser) return 0;
    noiseFloor = await measureFloorInternal(ms);
    return round(noiseFloor);
}

/// Measure ONE spoken utterance without transcribing it. Returns
/// { spoke, peak, longestPauseMs, utteranceMs }.
///
/// longestPauseMs is the point of the whole exercise: the longest gap BETWEEN words inside one utterance.
/// It's what decides whether we cut someone off mid-sentence, and it's unguessable for anyone else.
/// Note `endSilenceMs` must be generous — a calibration that gave up after 900ms of quiet could never
/// observe a 1200ms pause, which is exactly the person it most needs to measure.
export async function measureUtterance(threshold, maxWaitMs, endSilenceMs, hardCapMs) {
    if (!analyser) return { spoke: false, peak: 0, longestPauseMs: 0, utteranceMs: 0 };

    let peak = 0, longestPause = 0;
    let spoke = false, firstLoudAt = 0, lastLoudAt = 0, quietSince = 0;
    const startedAt = Date.now();

    while (!cancelled) {
        const level = rms();
        const now = Date.now();

        if (level > threshold) {
            peak = Math.max(peak, level);
            if (!spoke) { spoke = true; firstLoudAt = now; }
            else if (quietSince > 0) longestPause = Math.max(longestPause, now - quietSince);
            quietSince = 0;
            lastLoudAt = now;
        } else if (spoke && quietSince === 0) {
            quietSince = now;
        }

        if (spoke && quietSince > 0 && now - quietSince >= endSilenceMs) break; // they finished
        if (!spoke && now - startedAt >= maxWaitMs) break;                      // nobody spoke
        if (now - startedAt >= hardCapMs) break;
        await wait(25);
    }

    return {
        spoke,
        peak: round(peak),
        longestPauseMs: Math.round(longestPause),
        utteranceMs: spoke ? Math.round(lastLoudAt - firstLoudAt) : 0,
    };
}

async function measureFloorInternal(ms) {
    const readings = [];
    const until = Date.now() + ms;
    while (Date.now() < until) {
        readings.push(rms());
        await wait(25);
    }
    if (readings.length === 0) return 0;
    // Median, not mean: one cupboard door slamming during calibration shouldn't set the floor for the
    // whole session.
    readings.sort((a, b) => a - b);
    return readings[Math.floor(readings.length / 2)];
}

function rms() {
    const buf = new Float32Array(analyser.fftSize);
    analyser.getFloatTimeDomainData(buf);
    let sum = 0;
    for (let i = 0; i < buf.length; i++) sum += buf[i] * buf[i];
    return Math.sqrt(sum / buf.length);
}

function newRecorder() {
    try {
        const mime = pickMimeType();
        return mime ? new MediaRecorder(stream, { mimeType: mime }) : new MediaRecorder(stream);
    } catch {
        return null;
    }
}

function stopAndCollect(recorder, chunks) {
    return new Promise(resolve => {
        recorder.onstop = () => {
            const type = (recorder.mimeType || 'audio/webm').split(';')[0];
            resolve(chunks.length ? new Blob(chunks, { type }) : null);
        };
        try { recorder.stop(); } catch { resolve(null); }
    });
}

// Prefer Opus in WebM (small, widely supported); fall back to whatever the browser offers (Safari = mp4).
function pickMimeType() {
    const candidates = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus'];
    for (const c of candidates) {
        if (window.MediaRecorder && MediaRecorder.isTypeSupported(c)) return c;
    }
    return '';
}

// Chunked to avoid blowing the argument limit of String.fromCharCode on large buffers.
function bytesToBase64(bytes) {
    let binary = '';
    const chunkSize = 0x8000;
    for (let i = 0; i < bytes.length; i += chunkSize) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
    }
    return btoa(binary);
}

const wait = ms => new Promise(r => setTimeout(r, ms));
const round = n => Math.round(n * 10000) / 10000;
