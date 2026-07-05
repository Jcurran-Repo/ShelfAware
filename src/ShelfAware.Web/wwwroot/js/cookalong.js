// Hands-free recipe cook-along via the ElevenLabs Agents realtime SDK (Phase 4b). The server mints a
// short-lived signed URL (the API key stays server-side); we open a conversation, inject the recipe as
// a dynamic variable, and let the agent handle turn-taking + barge-in natively. State is reported back
// to .NET for the UI. The SDK is loaded from a CDN so the app keeps its no-build-step JS setup.

let Conversation = null;
let convo = null;

async function ensureSdk() {
    if (!Conversation) {
        ({ Conversation } = await import('https://esm.sh/@elevenlabs/client'));
    }
    return Conversation;
}

export function isSupported() {
    return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.WebSocket);
}

// Fetch a signed URL from our server, then open the realtime conversation with the recipe injected.
// dotnetRef receives: OnStatus(string), OnMode(string 'speaking'|'listening'), OnTranscript(source, text).
export async function start(recipe, dotnetRef) {
    await stop();
    dotnetRef.invokeMethodAsync('OnStatus', 'connecting');

    let signedUrl;
    try {
        const resp = await fetch('/api/cookalong/signed-url');
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
