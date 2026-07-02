// Voice I/O helpers for the browser side of the ElevenLabs loop.
// Recording: capture one push-to-talk utterance via MediaRecorder and hand the bytes to .NET as
// base64 (the server does STT -> chat -> TTS). Playback: play the synthesized audio .NET hands back.
// Kept deliberately small and stateless-per-call; the reasoning lives on the server.

let mediaRecorder = null;
let chunks = [];
let stream = null;
let currentAudio = null;

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
    return true;
}

// Stop capturing and return { audio: base64, mimeType, size } for the recorded utterance, or null
// if nothing was captured. Also releases the microphone.
export async function stop() {
    if (!mediaRecorder) return null;
    const recorder = mediaRecorder;
    const localStream = stream;

    const result = await new Promise(resolve => {
        recorder.onstop = async () => {
            const type = (recorder.mimeType || 'audio/webm').split(';')[0];
            const blob = new Blob(chunks, { type });
            if (blob.size === 0) { resolve(null); return; }
            const buffer = await blob.arrayBuffer();
            resolve({ audio: bytesToBase64(new Uint8Array(buffer)), mimeType: type, size: blob.size });
        };
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

// Chunked to avoid blowing the argument limit of String.fromCharCode on large buffers.
function bytesToBase64(bytes) {
    let binary = '';
    const chunkSize = 0x8000;
    for (let i = 0; i < bytes.length; i += chunkSize) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
    }
    return btoa(binary);
}
