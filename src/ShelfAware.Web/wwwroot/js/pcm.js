// Turning a recording into something an in-process recognizer can read.
//
// The ear used to be a cloud service that accepted whatever container MediaRecorder produced, so the
// capture scripts handed over webm/opus (or mp4/AAC on Safari) and let the provider decode it. A model
// running inside the app has no decoder: it wants raw samples. Decoding opus on the SERVER would mean
// ffmpeg or an equivalent — a second thing to install beside the app, which is exactly the property that
// made running the model in-process worth doing. So the conversion happens here, where a decoder already
// exists and is free: every browser can decode the format it just recorded in.
//
// ONE definition, imported by both capture paths (voice.js and cooklisten.js). Two copies of a sample
// -rate conversion is two chances to send audio the ear mishears at a pitch nobody can explain.

// What the recognizer wants. Moonshine and Whisper are both trained at 16 kHz; sending 48 kHz would mean
// the model resampling it anyway, at four times the bytes over the circuit on the way.
const TARGET_RATE = 16000;

/// Convert a recorded Blob into a 16 kHz mono 16-bit WAV, as { bytes, mimeType }.
/// Returns null if this browser cannot decode its own recording, which is the caller's cue to send the
/// original bytes instead — a cloud ear still understands those, and a local one fails with a sentence
/// that says so rather than transcribing noise.
export async function toMonoWav16k(blob) {
    try {
        const buffer = await blob.arrayBuffer();
        const Ctx = window.AudioContext || window.webkitAudioContext;
        if (!Ctx || typeof OfflineAudioContext === 'undefined') return null;

        // decodeAudioData detaches the buffer it is given, so it gets its own copy — the caller may still
        // want the original bytes if this whole path bails out below.
        const ctx = new Ctx();
        let decoded;
        try {
            decoded = await ctx.decodeAudioData(buffer.slice(0));
        } finally {
            // Don't leave an AudioContext open per utterance: browsers cap how many a page may hold, and
            // a cook-along opens a listening window every few seconds.
            if (ctx.state !== 'closed') await ctx.close().catch(() => {});
        }

        const samples = await resampleToMono(decoded);
        return { bytes: encodeWav(samples, TARGET_RATE), mimeType: 'audio/wav' };
    } catch (err) {
        // A browser that can't decode what it recorded is a real possibility at the edges (an exotic
        // codec, a truncated blob). Fall back rather than lose the utterance — but say so: on a box with
        // a local ear this is the difference between "the model is broken" and "this browser didn't
        // convert", and the server can only see the second half of that.
        console.warn('[pcm] could not convert the recording to 16 kHz PCM; sending it as recorded.', err);
        return null;
    }
}

// Resample to 16 kHz mono. OfflineAudioContext does it properly (it interpolates rather than dropping
// samples, which is what makes the difference between speech and a robot); the manual path is the
// fallback for a browser that refuses to build a context at this rate.
async function resampleToMono(decoded) {
    const frames = Math.max(1, Math.ceil(decoded.duration * TARGET_RATE));
    try {
        const offline = new OfflineAudioContext(1, frames, TARGET_RATE);
        const source = offline.createBufferSource();
        source.buffer = decoded;
        source.connect(offline.destination);
        source.start();
        const rendered = await offline.startRendering();
        return rendered.getChannelData(0);
    } catch (err) {
        console.warn('[pcm] OfflineAudioContext refused 16 kHz; resampling by hand.', err);
        return linearResample(decoded);
    }
}

// Straight linear interpolation, averaging the channels. Good enough for speech and it depends on
// nothing — which is the point of having it.
function linearResample(decoded) {
    const channels = [];
    for (let c = 0; c < decoded.numberOfChannels; c++) channels.push(decoded.getChannelData(c));

    const ratio = decoded.sampleRate / TARGET_RATE;
    const out = new Float32Array(Math.max(1, Math.floor(channels[0].length / ratio)));
    for (let i = 0; i < out.length; i++) {
        const at = i * ratio;
        const lo = Math.floor(at);
        const hi = Math.min(lo + 1, channels[0].length - 1);
        const frac = at - lo;
        let sum = 0;
        for (const data of channels) sum += data[lo] * (1 - frac) + data[hi] * frac;
        out[i] = sum / channels.length;
    }
    return out;
}

// 16-bit PCM in a WAV container — the mirror of WaveAudio.Encode/Decode on the server. Clamped, not
// scaled: a sample just outside ±1 multiplied by 32767 and cast wraps to the opposite extreme, which is
// a click in the middle of a word and, to a recognizer, a word it never heard.
function encodeWav(samples, sampleRate) {
    const bytes = new Uint8Array(44 + samples.length * 2);
    const view = new DataView(bytes.buffer);

    writeAscii(view, 0, 'RIFF');
    view.setUint32(4, bytes.length - 8, true);
    writeAscii(view, 8, 'WAVE');
    writeAscii(view, 12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true);            // PCM
    view.setUint16(22, 1, true);            // mono
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, sampleRate * 2, true); // byte rate
    view.setUint16(32, 2, true);            // block align
    view.setUint16(34, 16, true);           // bits per sample
    writeAscii(view, 36, 'data');
    view.setUint32(40, samples.length * 2, true);

    for (let i = 0; i < samples.length; i++) {
        const sample = Math.max(-1, Math.min(1, samples[i]));
        view.setInt16(44 + i * 2, sample * 32767, true);
    }
    return bytes;
}

function writeAscii(view, at, text) {
    for (let i = 0; i < text.length; i++) view.setUint8(at + i, text.charCodeAt(i));
}

// Chunked to avoid blowing the argument limit of String.fromCharCode on large buffers. Shared here
// because both capture paths need it and both used to carry their own copy.
export function bytesToBase64(bytes) {
    let binary = '';
    const chunkSize = 0x8000;
    for (let i = 0; i < bytes.length; i += chunkSize) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
    }
    return btoa(binary);
}
