// Hands-free conversation capture (v2.1, no barge-in). Opens the mic once, then per turn records until
// it detects you've stopped talking (energy/RMS-based voice-activity detection), and hands the clip to
// .NET. Because there's no barge-in, the mic simply isn't recorded while the reply plays — so no echo
// cancellation needed. The server keeps IPantryChat as the brain; this is just the "ears".

let stream = null, audioCtx = null, analyser = null, timeData = null;
let recorder = null, chunks = [], rafId = null, aborted = false, currentAudio = null;

// Tunable VAD thresholds (per-mic/room; conservative defaults).
const SPEECH_RMS = 0.02;    // RMS above this counts as speech
const SILENCE_MS = 1200;    // trailing silence that ends a turn
const NO_SPEECH_MS = 7000;  // give up the turn if nobody speaks
const MAX_TURN_MS = 20000;  // hard cap per utterance

export function isSupported() {
    return !!(navigator.mediaDevices?.getUserMedia && window.MediaRecorder && (window.AudioContext || window.webkitAudioContext));
}

// Open the mic + analyser once for the whole conversation.
export async function start() {
    aborted = false;
    stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } });
    const AC = window.AudioContext || window.webkitAudioContext;
    audioCtx = new AC();
    const source = audioCtx.createMediaStreamSource(stream);
    analyser = audioCtx.createAnalyser();
    analyser.fftSize = 512;
    source.connect(analyser);
    timeData = new Uint8Array(analyser.fftSize);
    return true;
}

// Record one utterance, auto-stopping after trailing silence. Returns { audio, mimeType } or null
// (aborted, or nobody spoke before the timeout).
export async function captureTurn() {
    if (!stream || aborted) return null;
    const mime = pickMime();
    recorder = mime ? new MediaRecorder(stream, { mimeType: mime }) : new MediaRecorder(stream);
    chunks = [];
    recorder.ondataavailable = e => { if (e.data && e.data.size) chunks.push(e.data); };

    return await new Promise(resolve => {
        let settled = false;
        const finish = (withClip) => {
            if (settled) return;
            settled = true;
            stopRaf();
            const rec = recorder;
            if (!withClip) {
                try { rec.onstop = null; if (rec.state !== 'inactive') rec.stop(); } catch { /* ignore */ }
                resolve(null);
                return;
            }
            rec.onstop = async () => {
                const type = (rec.mimeType || 'audio/webm').split(';')[0];
                const blob = new Blob(chunks, { type });
                if (!blob.size) { resolve(null); return; }
                const buf = await blob.arrayBuffer();
                resolve({ audio: bytesToBase64(new Uint8Array(buf)), mimeType: type });
            };
            try { rec.stop(); } catch { resolve(null); }
        };

        let speaking = false;
        const t0 = performance.now();
        let lastVoice = t0;
        const tick = () => {
            if (aborted) return finish(false);
            analyser.getByteTimeDomainData(timeData);
            let sum = 0;
            for (let i = 0; i < timeData.length; i++) { const v = (timeData[i] - 128) / 128; sum += v * v; }
            const rms = Math.sqrt(sum / timeData.length);
            const now = performance.now();

            if (rms > SPEECH_RMS) { speaking = true; lastVoice = now; }
            if (!speaking && now - t0 > NO_SPEECH_MS) return finish(false);     // nobody spoke
            if (speaking && now - lastVoice > SILENCE_MS) return finish(true);  // trailing silence -> done
            if (now - t0 > MAX_TURN_MS) return finish(speaking);               // hard cap
            rafId = requestAnimationFrame(tick);
        };
        recorder.start();
        rafId = requestAnimationFrame(tick);
    });
}

// Play the synthesized reply; resolves when it finishes (mic isn't recorded during this).
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
    if (currentAudio) { currentAudio.pause(); currentAudio = null; }
}

// End the conversation: abort any pending capture, stop playback, release the mic.
export function stop() {
    aborted = true;
    stopRaf();
    stopPlayback();
    if (recorder && recorder.state !== 'inactive') { try { recorder.onstop = null; recorder.stop(); } catch { /* ignore */ } }
    recorder = null;
    if (stream) { stream.getTracks().forEach(t => t.stop()); stream = null; }
    if (audioCtx) { try { audioCtx.close(); } catch { /* ignore */ } audioCtx = null; }
}

function stopRaf() { if (rafId) { cancelAnimationFrame(rafId); rafId = null; } }

function pickMime() {
    const candidates = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4'];
    for (const c of candidates) if (window.MediaRecorder.isTypeSupported(c)) return c;
    return '';
}

function bytesToBase64(bytes) {
    let binary = '';
    const chunkSize = 0x8000;
    for (let i = 0; i < bytes.length; i += chunkSize) binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
    return btoa(binary);
}
