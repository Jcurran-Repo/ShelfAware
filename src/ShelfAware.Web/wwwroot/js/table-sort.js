// Per-device persistence of a table's chosen column sort. A sort preference is a UI convenience, not
// pantry data — so it lives in localStorage (like the theme, tour, and listening calibration), never in
// the household's data. Keyed per table id, so each table remembers its own. Every access is wrapped:
// localStorage can throw or be unavailable (private windows, blocked site data), and a missing/blocked
// value must just mean "use the default sort", never an error.

const KEY = id => `shelfaware.sort.${id}`;

export function get(id) {
    try {
        const raw = localStorage.getItem(KEY(id));
        if (!raw) return null;
        const v = JSON.parse(raw);
        // Only trust a well-formed record; anything else → default.
        return typeof v?.col === 'string' && typeof v?.desc === 'boolean' ? v : null;
    } catch {
        return null;
    }
}

export function set(id, col, desc) {
    try {
        localStorage.setItem(KEY(id), JSON.stringify({ col, desc }));
    } catch {
        /* storage unavailable — the sort still works this session, it just isn't remembered */
    }
}
