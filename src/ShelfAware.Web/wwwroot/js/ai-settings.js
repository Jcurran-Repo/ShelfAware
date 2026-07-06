// Bring-your-own-key settings, held in the visitor's own browser — never sent to or stored on the
// server. Keys only ever leave the browser to the provider the visitor chose. Persistent by default
// (localStorage, "enter once"); "session only" uses sessionStorage so it's gone when the tab closes
// (better on a shared computer).
//
// Shape:
//   { provider, providers: { Anthropic: {apiKey, extractionModel, chatModel}, OpenAI: {...} },
//     elevenLabs: { apiKey, agentId } }
const KEY = 'shelfaware.ai';

export function load() {
    try {
        const raw = sessionStorage.getItem(KEY) ?? localStorage.getItem(KEY);
        return raw ? JSON.parse(raw) : null;
    } catch {
        return null;
    }
}

// sessionOnly => sessionStorage (cleared on tab close) and drop the persistent copy; else localStorage
// and drop any session copy. Returns false if the browser refused (e.g. storage disabled).
export function save(settings, sessionOnly) {
    try {
        const json = JSON.stringify(settings);
        if (sessionOnly) {
            sessionStorage.setItem(KEY, json);
            localStorage.removeItem(KEY);
        } else {
            localStorage.setItem(KEY, json);
            sessionStorage.removeItem(KEY);
        }
        return true;
    } catch {
        return false;
    }
}

export function clear() {
    try {
        localStorage.removeItem(KEY);
        sessionStorage.removeItem(KEY);
    } catch {
        /* ignore */
    }
}

export function isSessionOnly() {
    try { return sessionStorage.getItem(KEY) != null; } catch { return false; }
}

// The active provider's flat config, for the circuit loader (AiSettingsLoader).
export function resolveActive() {
    const s = load();
    if (!s || !s.provider) return null;
    const p = (s.providers && s.providers[s.provider]) || {};
    return {
        provider: s.provider,
        apiKey: p.apiKey ?? '',
        extractionModel: p.extractionModel ?? '',
        chatModel: p.chatModel ?? '',
        baseUrl: p.baseUrl ?? '', // OpenAI-compatible/local only; honored server-side per AllowCustomEndpoint
    };
}

// The ElevenLabs voice credentials the browser uses directly (client-side voice, no server key).
export function voiceCreds() {
    const s = load();
    const el = (s && s.elevenLabs) || {};
    return { apiKey: el.apiKey ?? '', agentId: el.agentId ?? '' };
}
