// Hands-free recipe cook-along via the ElevenLabs Agents realtime SDK. The server mints a short-lived
// signed URL from the VISITOR's own ElevenLabs key (sent as request headers) — BYOK, no server key on the
// public deploy. We open a conversation, inject the recipe as a dynamic variable, and let the agent handle
// turn-taking + barge-in natively; state is reported back to .NET.
//
// The SDK loads from esm.sh at a PINNED, immutable version — keeping the no-build JS setup. A multi-module
// ESM SDK can't be practically self-hosted without a bundler, so instead the CSP restricts scripts to our
// origin + esm.sh only, and the version pin means a later package change can't silently apply.
const SDK_URL = 'https://esm.sh/@elevenlabs/client@1.14.0';

// SDK-bug shim (remove when upstream fixes it): startSession's libsampleratePath override IS honored on
// the input side, but @elevenlabs/client@1.14.0 forgets to pass it to Output.create, so the OUTPUT
// resampler always falls back to the jsdelivr CDN URL — which our strict CSP blocks (and on Firefox the
// output resampler always loads, since sampleRate constraints aren't supported there). Rather than open
// script-src to a third-party CDN, rewrite exactly that one known URL to our vendored copy of the same
// file. Match the full pinned URL — a version drift should fail visibly, not silently load a mismatch.
const JSDELIVR_LIBSAMPLERATE = 'https://cdn.jsdelivr.net/npm/@alexanderolsen/libsamplerate-js@2.1.2/dist/libsamplerate.worklet.js';
if (typeof AudioWorklet !== 'undefined' && AudioWorklet.prototype.addModule) {
    const origAddModule = AudioWorklet.prototype.addModule;
    AudioWorklet.prototype.addModule = function (url, options) {
        if (url === JSDELIVR_LIBSAMPLERATE) url = '/js/vendor/libsamplerate.worklet.js';
        return origAddModule.call(this, url, options);
    };
}

let Conversation = null;
let convo = null;

async function ensureSdk() {
    if (!Conversation) {
        ({ Conversation } = await import(SDK_URL));
    }
    return Conversation;
}

// The visitor's ElevenLabs credentials (their own key), read from their browser and sent to our signed-url
// endpoint so it mints with their key — never ours.
async function voiceCreds() {
    try {
        const m = await import('/js/ai-settings.js');
        return m.voiceCreds();
    } catch {
        return { apiKey: '', agentId: '' };
    }
}

export function isSupported() {
    return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.WebSocket);
}

// No config overrides are sent when the session opens — the agent greets with whatever first message
// is configured on it (or just listens). We used to override first_message for a tailored greeting, but
// ElevenLabs hard-terminates the conversation (WS close 1008) unless the override is explicitly allowed
// in the agent's dashboard config, which proved flaky to keep enabled and is never enabled on BYOK
// visitors' agents. The greeting wasn't worth that failure mode.

// Bumped by every start/stop; async callbacks compare against it so a stale session's disconnect can't
// fight a newer session (or resurrect one the user just stopped).
let generation = 0;

// Open the realtime conversation with the recipe injected. dotnetRef receives: OnStatus(string),
// OnMode(string 'speaking'|'listening'), OnTranscript(source, text).
export async function start(recipe, dotnetRef) {
    await stop();
    const gen = generation;
    dotnetRef.invokeMethodAsync('OnStatus', 'connecting');
    try {
        await open(recipe, dotnetRef, gen);
        return true;
    } catch (e) {
        if (gen === generation && !e?.statusReported) dotnetRef.invokeMethodAsync('OnStatus', 'error');
        return false;
    }
}

async function open(recipe, dotnetRef, gen) {
    const C = await ensureSdk();

    // A fresh signed URL per attempt — they're short-lived and consumed by the connection.
    const creds = await voiceCreds();
    let signedUrl;
    try {
        const resp = await fetch('/api/cookalong/signed-url', {
            headers: creds.apiKey ? { 'X-EL-Key': creds.apiKey, 'X-EL-Agent': creds.agentId } : {},
        });
        if (!resp.ok) {
            dotnetRef.invokeMethodAsync('OnStatus', resp.status === 503 ? 'unconfigured' : 'error');
            throw Object.assign(new Error('signed-url refused'), { statusReported: true });
        }
        signedUrl = (await resp.json()).signed_url;
    } catch (e) {
        if (e?.statusReported) throw e;
        dotnetRef.invokeMethodAsync('OnStatus', 'error');
        throw Object.assign(new Error('signed-url fetch failed'), { statusReported: true });
    }
    if (!signedUrl) {
        dotnetRef.invokeMethodAsync('OnStatus', 'error');
        throw Object.assign(new Error('signed-url missing'), { statusReported: true });
    }

    convo = await C.startSession({
        signedUrl,
        dynamicVariables: { recipe },
        // Self-hosted audio worklets: the SDK's defaults (a jsdelivr CDN URL for the resampler,
        // blob:/data: fallbacks for its own processors) are all blocked by the strict CSP
        // (script-src 'self' + esm.sh). Vendored at the SDK's pinned version — see js/vendor/README.md.
        workletPaths: {
            rawAudioProcessor: '/js/vendor/raw-audio-processor.worklet.js',
            audioConcatProcessor: '/js/vendor/audio-concat-processor.worklet.js',
        },
        libsampleratePath: '/js/vendor/libsamplerate.worklet.js',
        onConnect: () => { if (gen === generation) dotnetRef.invokeMethodAsync('OnStatus', 'connected'); },
        onDisconnect: (details) => {
            if (gen !== generation) return; // stale session — a newer start/stop owns the UI now
            // Surface abnormal close reasons — an opaque "ended" cost a whole debugging round once.
            if (details?.reason === 'error') {
                console.warn('[cook-along] disconnected:', details?.message ?? details?.closeReason ?? '(no reason given)');
            }
            dotnetRef.invokeMethodAsync('OnStatus', 'ended');
        },
        onError: () => { if (gen === generation) dotnetRef.invokeMethodAsync('OnStatus', 'error'); },
        onModeChange: (m) => { if (gen === generation) dotnetRef.invokeMethodAsync('OnMode', modeOf(m)); },
        onMessage: (msg) => {
            if (gen !== generation || !msg || !msg.message) return;
            const source = String(msg.source ?? 'ai');
            const text = String(msg.message);
            dotnetRef.invokeMethodAsync('OnTranscript', source, text);
            if (source !== 'user') return;
            // "Go to the assistant" hands control back to our own voice agent: end this session and
            // ask it to resume listening. Detected client-side (it's not one of the EL agent's own
            // step commands), same pattern as "stop listening" below.
            if (/\b(go(?:\s+back)?\s+to|back\s+to|take\s+me\s+to|open|switch\s+to|talk\s+to)\s+(?:the\s+)?assistant\b/i.test(text)) {
                stop();
                dotnetRef.invokeMethodAsync('OnHandOff');
                return;
            }
            // Client-side guarantee for "stop listening": the agent's own prompt handles "stop",
            // but the mic must close even if the agent misses or ignores the phrase.
            if (/\bstop listening\b/i.test(text)) {
                stop();
                dotnetRef.invokeMethodAsync('OnStatus', 'ended');
            }
        },
    });
}

export async function stop() {
    generation++; // invalidate any in-flight callbacks for the old session
    if (convo) {
        try { await convo.endSession(); } catch { /* already closed */ }
        convo = null;
    }
}

// onModeChange payload has been an object ({ mode }) or a bare string across SDK versions — normalize.
function modeOf(m) {
    if (!m) return 'listening';
    if (typeof m === 'string') return m;
    return m.mode ?? 'listening';
}
