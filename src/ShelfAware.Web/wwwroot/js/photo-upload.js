// Circuit-independent photo staging + upload, shared by the receipt Upload page and the shelf-census
// (Count Stock) page.
//
// WHY THIS EXISTS: Blazor Server's <InputFile> hands the picked file to .NET over the SignalR circuit.
// On mobile, opening the native file/camera picker backgrounds the page and can briefly drop that circuit;
// the `change` event then fires while it's down, so .NET never receives it and the file vanishes silently
// (the "filename shows, but no Extract button" bug). This module instead:
//   1. captures + resizes the photo in a plain browser `change` listener (runs regardless of circuit state),
//   2. holds the resized bytes in JS memory,
//   3. hands the .NET side only the file NAMES — retrying until the circuit is back — so the staged list
//      re-appears after a reconnect, and
//   4. POSTs the bytes to /api/receipts/extract over plain HTTPS (no circuit involved) when the user
//      extracts.
//
// State is per-uploader (closed over by init's return value), NOT module-level, so navigating between the
// two photo pages can't leak one page's staged files into the other.

import { resolveActive } from './ai-settings.js';

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

/**
 * Wire an uploader to a file input.
 * @param inputEl the <input type=file>
 * @param dotNetRef a DotNetObjectReference whose OnStaged(files, problems) renders the staged list
 * @param maxEdgePx longest-edge the photo is downscaled to before upload (the server's MaxImageEdgePx)
 * @param maxFiles the most files that may be staged at once (extract these first, or remove one)
 * @param endpoint the POST url that ingests one receipt/photo from its parts
 * @returns an object the .NET side keeps and calls: remove / clear / extract / extractOne / dispose
 */
export function init(inputEl, dotNetRef, maxEdgePx, maxFiles, endpoint) {
    const staged = []; // { id, name, blob, mediaType }
    let nextId = 1;

    async function onChange() {
        const files = Array.from(inputEl.files || []);
        // Clearing the value is what lets the SAME file (or another camera snap the OS also names
        // "image.jpg") fire a fresh change event next time — the vanilla equivalent of the @key trick.
        inputEl.value = '';
        if (files.length === 0) return;

        const problems = [];
        for (const file of files) {
            if (staged.length >= maxFiles) {
                problems.push(`You can upload up to ${maxFiles} at a time — extract these first, or remove one.`);
                break;
            }
            try {
                const prepared = await prepare(file, maxEdgePx);
                staged.push({ id: nextId++, name: file.name || 'photo.jpg', blob: prepared.blob, mediaType: prepared.mediaType });
            } catch {
                problems.push(`“${file.name || 'that file'}” couldn't be read — take or pick it again.`);
            }
        }
        await notify(problems);
    }
    inputEl.addEventListener('change', onChange);

    // Hand the current staged list (names only) to .NET, retrying across a circuit reconnect: on mobile the
    // picker can drop the circuit, so the first notify after a pick may land while .NET is unreachable.
    async function notify(problems) {
        const meta = staged.map(s => ({ id: s.id, name: s.name }));
        for (let attempt = 0; attempt < 60; attempt++) {
            try {
                await dotNetRef.invokeMethodAsync('OnStaged', meta, problems);
                return;
            } catch {
                await delay(500); // ~30s of patience; a resumed circuit succeeds well within it
            }
        }
    }

    async function post(items, token) {
        const form = new FormData();
        for (const it of items) form.append('files', it.blob, it.name);

        const headers = { 'RequestVerificationToken': token };
        // BYOK: forward the visitor's own key so the server can extract on it. Blank on a managed
        // deployment (and ignored there anyway). Never goes in the URL — headers ride inside TLS.
        const ai = resolveActive();
        if (ai && ai.apiKey) {
            headers['X-AI-Provider'] = ai.provider || 'Anthropic';
            headers['X-AI-Key'] = ai.apiKey;
            headers['X-AI-Extraction-Model'] = ai.extractionModel || '';
            headers['X-AI-Chat-Model'] = ai.chatModel || '';
            headers['X-AI-Base-Url'] = ai.baseUrl || '';
        }

        // Returns a structured result the .NET side maps to UploadResult — never throws, so a failed
        // upload comes back as { ok: false, error } rather than a JSException the caller has to unwrap.
        let resp;
        try {
            resp = await fetch(endpoint, { method: 'POST', body: form, headers, credentials: 'same-origin' });
        } catch {
            return { ok: false, error: 'Network error — check your connection and try again.' };
        }
        const text = await resp.text();
        let data = null;
        try { data = text ? JSON.parse(text) : null; } catch { /* non-JSON error body */ }
        if (!resp.ok) return { ok: false, error: (data && data.error) || `Upload failed (${resp.status}).` };
        return { ok: true, outcome: data };
    }

    return {
        remove(id) {
            const i = staged.findIndex(s => s.id === id);
            if (i >= 0) staged.splice(i, 1);
            return notify([]);
        },
        clear() { staged.length = 0; },
        // POST every staged file as ONE receipt (a single photo, or the pages of one long receipt).
        extract(token) { return post(staged, token); },
        // POST one staged file on its own (the batch case: several separate receipts, one call each).
        extractOne(token, id) {
            const s = staged.find(x => x.id === id);
            return s ? post([s], token) : Promise.resolve(null);
        },
        dispose() { inputEl.removeEventListener('change', onChange); },
    };

    // --- helpers ---

    async function prepare(file, maxEdge) {
        // PDFs (print-to-PDF order pages) are taken as-is — there's nothing to resize.
        if (file.type === 'application/pdf') return { blob: file, mediaType: 'application/pdf' };

        // Everything else is drawn through an <img> onto a canvas and re-encoded as JPEG — the same
        // technique Blazor's RequestImageFileAsync uses, so it decodes whatever the browser can (incl.
        // HEIC on WebKit) and applies EXIF orientation. Runs entirely in the browser (needs img-src blob:
        // in the CSP, which is set), so it works with the circuit down.
        const img = await loadImage(file);
        const scale = Math.min(1, maxEdge / Math.max(img.naturalWidth, img.naturalHeight));
        const width = Math.max(1, Math.round(img.naturalWidth * scale));
        const height = Math.max(1, Math.round(img.naturalHeight * scale));
        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;
        canvas.getContext('2d').drawImage(img, 0, 0, width, height);
        const blob = await new Promise((resolve, reject) =>
            canvas.toBlob(b => b ? resolve(b) : reject(new Error('encode failed')), 'image/jpeg', 0.9));
        return { blob, mediaType: 'image/jpeg' };
    }

    function loadImage(file) {
        return new Promise((resolve, reject) => {
            const url = URL.createObjectURL(file);
            const img = new Image();
            img.onload = () => { URL.revokeObjectURL(url); resolve(img); };
            img.onerror = () => { URL.revokeObjectURL(url); reject(new Error('decode failed')); };
            img.src = url;
        });
    }
}
