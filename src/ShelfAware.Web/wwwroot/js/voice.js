// Voice I/O helpers for the browser side of the voice loop.
// Recording: capture one push-to-talk utterance via MediaRecorder and hand the bytes to .NET as
// base64 (the server does STT -> chat -> TTS). Playback: play the synthesized audio .NET hands back.
// Kept deliberately small and stateless-per-call; the reasoning lives on the server.

import { toMonoWav16k, bytesToBase64 } from './pcm.js';

let mediaRecorder = null;
let chunks = [];
let stream = null;
let currentAudio = null;
let holdTimer = null;

// A hard cap on one hold, the twin of conversation.js's MAX_TURN_MS and the cook-along's clamped MaxMs.
// ⚠️ It is not a nicety: 16 kHz 16-bit PCM is a fixed 32 KB/s, ~43 KB/s once base64'd, and Program.cs
// caps a SignalR message at 4 MB — so a button held about a hundred seconds (a phone in a pocket)
// used to blow the hub limit and close the circuit with nothing said. Opus hid that for years by being
// seven times smaller; converting in the browser is what brought it within reach.
const MAX_HOLD_MS = 60000;

export function isSupported() {
    return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.MediaRecorder);
}

// Begin capturing. Resolves once the mic is live (may prompt for permission on first use).
export async function start() {
    chunks = [];
    stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    const mime = pickMimeType();
    mediaRecorder = mime ? new MediaRecorder(stream, { mimeType: mime }) : new MediaRecorder(stream);
    mediaRecorder.ondataavailable = e => { if (e.data && e.data.size > 0) chunks.push(e.data); };
    mediaRecorder.start();
    // Stop the RECORDER at the cap, and only the recorder: the button is still held, so .NET hasn't
    // called stop() yet. It gets the first MAX_HOLD_MS of audio when it does, which is a long utterance
    // truncated rather than an utterance lost.
    holdTimer = setTimeout(() => {
        if (mediaRecorder && mediaRecorder.state === 'recording') mediaRecorder.stop();
    }, MAX_HOLD_MS);
    return true;
}

// Stop capturing and return { audio: base64, mimeType } for the recorded utterance, or null if nothing
// was captured. Also releases the microphone.
export async function stop() {
    if (!mediaRecorder) return null;
    const recorder = mediaRecorder;
    const localStream = stream;
    clearTimeout(holdTimer);
    holdTimer = null;

    const packaged = async () => {
        const type = (recorder.mimeType || 'audio/webm').split(';')[0];
        const blob = new Blob(chunks, { type });
        if (blob.size === 0) return null;
        // 16 kHz PCM, so an ear running inside the app can read it without a codec. A cloud ear
        // reads WAV just as happily, so there is one capture shape rather than one per provider.
        // Null means this browser couldn't decode its own recording: send the original and let the
        // server say what it can do with it.
        const wav = await toMonoWav16k(blob);
        if (wav) return { audio: bytesToBase64(wav.bytes), mimeType: wav.mimeType };
        const buffer = await blob.arrayBuffer();
        return { audio: bytesToBase64(new Uint8Array(buffer)), mimeType: type };
    };

    // The cap above may already have stopped it, and stop() on an inactive recorder throws — so ask
    // what state it is in rather than assuming the button ended the recording.
    const result = recorder.state === 'inactive'
        ? await packaged()
        : await new Promise(resolve => {
            recorder.onstop = async () => resolve(await packaged());
            recorder.stop();
        });

    if (localStream) localStream.getTracks().forEach(t => t.stop());
    mediaRecorder = null;
    stream = null;
    chunks = [];
    return result;
}

// Play base64-encoded audio the server synthesized. Resolves when playback ends (or errors out).
export function play(base64, mimeType) {
    stopPlayback();
    return new Promise(resolve => {
        const audio = new Audio(`data:${mimeType};base64,${base64}`);
        currentAudio = audio;
        audio.onended = audio.onerror = () => { if (currentAudio === audio) currentAudio = null; resolve(); };
        audio.play().catch(() => resolve());
    });
}

export function stopPlayback() {
    if (currentAudio) {
        currentAudio.pause();
        currentAudio = null;
    }
}

// Prefer Opus in WebM (small, widely supported); fall back to whatever the browser offers (Safari = mp4).
function pickMimeType() {
    const candidates = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus'];
    for (const c of candidates) {
        if (window.MediaRecorder && MediaRecorder.isTypeSupported(c)) return c;
    }
    return '';
}
