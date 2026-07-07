// Hands-free recipe cook-along via the ElevenLabs Agents realtime SDK. The server mints a short-lived
// signed URL from the VISITOR's own ElevenLabs key (sent as request headers) — BYOK, no server key on the
// public deploy. We open a conversation, inject the recipe as a dynamic variable, and let the agent handle
// turn-taking + barge-in natively; state is reported back to .NET.
//
// The SDK loads from esm.sh at a PINNED, immutable version — keeping the no-build JS setup. A multi-module
// ESM SDK can't be practically self-hosted without a bundler, so instead the CSP restricts scripts to our
// origin + esm.sh only, and the version pin means a later package change can't silently apply.
const SDK_URL = 'https://esm.sh/@elevenlabs/client@1.14.0';

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

// Fetch a signed URL from our server, then open the realtime conversation with the recipe injected.
// dotnetRef receives: OnStatus(string), OnMode(string 'speaking'|'listening'), OnTranscript(source, text).
export async function start(recipe, dotnetRef) {
    await stop();
    dotnetRef.invokeMethodAsync('OnStatus', 'connecting');

    const creds = await voiceCreds();
    let signedUrl;
    try {
        const resp = await fetch('/api/cookalong/signed-url', {
            headers: creds.apiKey ? { 'X-EL-Key': creds.apiKey, 'X-EL-Agent': creds.agentId } : {},
        });
        if (!resp.ok) { dotnetRef.invokeMethodAsync('OnStatus', resp.status === 503 ? 'unconfigured' : 'error'); return false; }
        signedUrl = (await resp.json()).signed_url;
    } catch {
        dotnetRef.invokeMethodAsync('OnStatus', 'error');
        return false;
    }
    if (!signedUrl) { dotnetRef.invokeMethodAsync('OnStatus', 'error'); return false; }

    try {
        const C = await ensureSdk();
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
            // Greet on connect, then wait — so it doesn't sit silent OR barrel into step one unasked.
            // (Requires "First message" override enabled for the agent in the ElevenLabs dashboard; if it
            // isn't, the agent falls back to its own configured first message.)
            overrides: {
                agent: { firstMessage: "I'm ready to read you the recipe. Say 'next' whenever you want the first step." },
            },
            onConnect: () => dotnetRef.invokeMethodAsync('OnStatus', 'connected'),
            onDisconnect: () => dotnetRef.invokeMethodAsync('OnStatus', 'ended'),
            onError: () => dotnetRef.invokeMethodAsync('OnStatus', 'error'),
            onModeChange: (m) => dotnetRef.invokeMethodAsync('OnMode', modeOf(m)),
            onMessage: (msg) => {
                if (!msg || !msg.message) return;
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
        return true;
    } catch {
        dotnetRef.invokeMethodAsync('OnStatus', 'error');
        return false;
    }
}

export async function stop() {
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
