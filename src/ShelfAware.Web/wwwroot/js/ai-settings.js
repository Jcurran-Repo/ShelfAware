// Bring-your-own-key settings, held in the visitor's own browser (localStorage) — never sent to or
// stored on the server. The Settings page writes them; the app reads them on each circuit start and
// applies them to that circuit's AI config. Keys only ever leave the browser to the provider the
// visitor chose.
const KEY = 'shelfaware.ai';

export function load() {
    try {
        const raw = localStorage.getItem(KEY);
        return raw ? JSON.parse(raw) : null;
    } catch {
        return null;
    }
}

export function save(settings) {
    try {
        localStorage.setItem(KEY, JSON.stringify(settings));
        return true;
    } catch {
        return false;
    }
}

export function clear() {
    try { localStorage.removeItem(KEY); } catch { /* ignore */ }
}
