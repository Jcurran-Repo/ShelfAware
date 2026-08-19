// Circuit-independent photo staging, upload, AND staging UI — shared by the receipt Upload page and the
// shelf-census (Count Stock) page.
//
// WHY THIS EXISTS: Blazor Server's <InputFile> hands the picked file to .NET over the SignalR circuit, and
// so does every DOM update. On mobile, opening the native file/camera picker backgrounds the page and drops
// that circuit, and it is slow to come back (often only when you next tap something). So ANYTHING that waits
// on the circuit to show the picked file — the filename, the Extract button — can appear seconds late or not
// at all (the "filename shows, but no Extract button" bug, and its sequel where the pick showed nothing).
//
// This module keeps the ENTIRE pick-and-stage flow off the circuit:
//   1. it captures + resizes each photo in a plain browser `change` listener (runs regardless of circuit),
//   2. it holds the resized bytes in JS memory,
//   3. it RENDERS the staged list, the "one receipt" checkbox, and the Extract button itself, into a mount
//      element — so they appear instantly, no circuit involved, and
//   4. on Extract it POSTs the bytes to the endpoint over plain HTTPS (still no circuit) and hands the .NET
//      side only the FINISHED outcome, retried until the circuit is back — which by then it is, because the
//      user just tapped Extract (interaction is exactly what wakes a mobile circuit).
// The only circuit-dependent moment left is drawing the review/done screen from that outcome.
//
// State is per-uploader (closed over by init), NOT module-level, so navigating between the two photo pages
// can't leak one page's staged files into the other.

import { resolveActive } from './ai-settings.js';

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

/**
 * Wire an uploader to a file input and its staging mount.
 * @param inputEl the <input type=file>
 * @param mountEl an empty element this module fills with the staged list + Extract button (Blazor must
 *                render it with no children of its own, so its diff never fights this module's DOM)
 * @param dotNetRef a DotNetObjectReference whose OnExtracted(results, combined) draws the next screen
 * @param config { maxEdgePx, maxFiles, endpoint, extractLabel, busyLabel, noun, allowCombine, token,
 *                 reconnectMessage }
 * @returns { dispose } — the .NET side keeps this and calls dispose when the input leaves the DOM
 */
export function init(inputEl, mountEl, dotNetRef, config) {
    const staged = []; // { id, name, blob, mediaType }
    let nextId = 1;
    let combine = false;    // "these are all one receipt" (receipt page only, 2+ files)
    let busy = false;       // an extract is in flight — no new picks, buttons disabled
    let status = null;      // { text, isError } shown under the button
    let disposed = false;

    async function onChange() {
        const files = Array.from(inputEl.files || []);
        // Clearing the value is what lets the SAME file (or another camera snap the OS also names
        // "image.jpg") fire a fresh change event next time — the vanilla equivalent of the @key trick.
        inputEl.value = '';
        if (files.length === 0) return;
        if (busy) { status = { text: 'Hang on — still reading the last photos…', isError: false }; render(); return; }

        const problems = [];
        try {
            for (const file of files) {
                if (staged.length >= config.maxFiles) {
                    problems.push(`You can add up to ${config.maxFiles} at a time — ${config.extractLabel.toLowerCase()} these first, or remove one.`);
                    break;
                }
                try {
                    const prepared = await prepare(file, config.maxEdgePx);
                    staged.push({ id: nextId++, name: file.name || 'photo.jpg', blob: prepared.blob, mediaType: prepared.mediaType });
                } catch {
                    problems.push(`“${file.name || 'that file'}” couldn't be read — take or pick it again.`);
                }
            }
        } finally {
            // ALWAYS render — a pick must resolve to a staged file OR a visible problem, never silence, and
            // it renders client-side so it can't be lost to a dropped circuit.
            if (staged.length < 2) combine = false; // the "one receipt" choice only exists for 2+
            status = problems.length ? { text: problems.join(' '), isError: true } : null;
            render();
        }
    }
    inputEl.addEventListener('change', onChange);

    // --- the staging UI, built client-side into the mount (never waits on the circuit) ------------------

    function render() {
        if (disposed || !mountEl) return;
        mountEl.textContent = ''; // rebuild from scratch — the list is tiny
        if (staged.length > 0) {
            const ul = el('ul', 'staged-files');
            for (const s of staged) ul.appendChild(stagedRow(s));
            mountEl.appendChild(ul);

            const count = document.createElement('p');
            count.textContent = countText();
            mountEl.appendChild(count);

            if (config.allowCombine && staged.length > 1) mountEl.appendChild(combineCheck());

            const btn = el('button');
            btn.type = 'button';
            btn.textContent = config.extractLabel;
            btn.disabled = busy;
            btn.addEventListener('click', doExtract);
            mountEl.appendChild(btn);
        }
        if (status) {
            const p = el('p', status.isError ? 'error' : 'spinner');
            if (status.isError) p.setAttribute('role', 'alert');
            p.textContent = status.text;
            mountEl.appendChild(p);
        }
    }

    function stagedRow(s) {
        const li = document.createElement('li');
        const name = document.createElement('span');
        name.textContent = s.name;
        li.appendChild(name);
        const x = el('button', 'icon-btn');
        x.type = 'button';
        x.disabled = busy;
        x.setAttribute('aria-label', `Remove ${s.name}`);
        const glyph = document.createElement('span');
        glyph.setAttribute('aria-hidden', 'true');
        glyph.textContent = '✕';
        x.appendChild(glyph);
        x.addEventListener('click', () => removeStaged(s.id));
        li.appendChild(x);
        return li;
    }

    function combineCheck() {
        const label = el('label', 'combine-check');
        const cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.checked = combine;
        cb.disabled = busy;
        cb.addEventListener('change', () => { combine = cb.checked; render(); });
        label.appendChild(cb);
        label.appendChild(document.createTextNode(' These are all one receipt (multiple photos/pages of a single long receipt)'));
        return label;
    }

    function countText() {
        const n = staged.length;
        if (config.noun === 'photo') return `${n} photo${n === 1 ? '' : 's'} selected.`;
        const tail = n > 1 && !combine ? ` — ${n} receipts` : '';
        return `${n} file${n === 1 ? '' : 's'} selected${tail}.`;
    }

    function removeStaged(id) {
        if (busy) return;
        const i = staged.findIndex(s => s.id === id);
        if (i >= 0) staged.splice(i, 1);
        if (staged.length < 2) combine = false;
        status = null;
        render();
    }

    // --- extract: POST off-circuit, hand the finished outcome to .NET (retried across a reconnect) -------

    async function doExtract() {
        if (busy || staged.length === 0) return;
        busy = true;
        status = { text: config.busyLabel, isError: false };
        render();
        const fileCount = staged.length; // photos POSTed — .NET uses it for "found in N photos" copy
        try {
            // A stack of separate receipts is POSTed one at a time (each is its own receipt); a single photo,
            // one long receipt (combine ticked), or a census read goes as ONE POST of everything.
            const batch = config.allowCombine && staged.length > 1 && !combine;
            const results = [];
            if (batch) {
                for (let i = 0; i < staged.length; i++) {
                    status = { text: `Reading ${i + 1} of ${staged.length}…`, isError: false };
                    render();
                    results.push({ ...(await post([staged[i]])), name: staged[i].name });
                }
            } else {
                const only = staged.length === 1 ? staged[0].name : config.noun === 'photo' ? 'photos' : 'receipt';
                results.push({ ...(await post(staged)), name: only });
            }
            const outcome = await deliver(results, !batch, fileCount);
            if (outcome.delivered && !outcome.keep) {
                // .NET took the result and doesn't need the photos kept (moved to review/done, or the receipt
                // is safe in the queue). Drop our staged bytes so a return to staging starts fresh; render()
                // is a no-op if the mount was already torn down. When .NET asks to KEEP (a census whose read
                // failed — it has no audit copy), the photos stay so a retry is one tap.
                staged.length = 0;
                combine = false;
                status = null;
            }
        } finally {
            busy = false;
            render();
        }
    }

    // Deliver the finished result(s) to .NET, retrying across a circuit reconnect. This is the ONE
    // circuit-dependent moment left — but the user just tapped Extract, and interaction is what wakes a
    // mobile circuit, so it lands quickly in practice. If it truly never comes back, say so client-side and
    // keep the photos. OnExtracted returns whether to KEEP the staged photos (true for a census whose read
    // failed); a void return (the receipt page) reads as false, i.e. clear. fileCount is how many photos were
    // POSTed (the census page's "found in N photos" copy; the receipt page ignores it).
    async function deliver(results, combined, fileCount) {
        for (let attempt = 0; attempt < 120; attempt++) { // ~60s of patience
            try {
                const keep = await dotNetRef.invokeMethodAsync('OnExtracted', results, combined, fileCount);
                return { delivered: true, keep: keep === true };
            } catch {
                await delay(500);
            }
        }
        status = { text: config.reconnectMessage, isError: true };
        return { delivered: false, keep: true };
    }

    async function post(items) {
        const form = new FormData();
        for (const it of items) form.append('files', it.blob, it.name);

        const headers = { 'RequestVerificationToken': config.token };
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

        // Returns a structured result the .NET side maps to its result DTO — never throws, so a failed
        // upload comes back as { ok: false, error } rather than a JSException the caller has to unwrap.
        let resp;
        try {
            resp = await fetch(config.endpoint, { method: 'POST', body: form, headers, credentials: 'same-origin' });
        } catch {
            return { ok: false, error: 'Network error — check your connection and try again.' };
        }
        const text = await resp.text();
        let data = null;
        try { data = text ? JSON.parse(text) : null; } catch { /* non-JSON error body */ }
        if (!resp.ok) return { ok: false, error: (data && data.error) || `Upload failed (${resp.status}).` };
        return { ok: true, outcome: data };
    }

    render(); // nothing staged yet → an empty mount

    return {
        dispose() {
            disposed = true;
            inputEl.removeEventListener('change', onChange);
            if (mountEl) mountEl.textContent = '';
        },
    };

    // --- helpers ---------------------------------------------------------------------------------------

    function el(tag, cls) { const e = document.createElement(tag); if (cls) e.className = cls; return e; }

    async function prepare(file, maxEdge) {
        // PDFs (print-to-PDF order pages) are taken as-is — there's nothing to resize.
        if (file.type === 'application/pdf') return { blob: file, mediaType: 'application/pdf' };

        // Everything else is decoded, drawn onto a canvas, and re-encoded as JPEG — so the server always
        // gets a modest JPEG (incl. transcoding a HEIC the browser can decode). See decode() for the two
        // decode paths and the timeout. Runs entirely in the browser (needs img-src blob: in the CSP, which
        // is set), so it works with the circuit down.
        const img = await decode(file);
        const w0 = img.naturalWidth || img.width;
        const h0 = img.naturalHeight || img.height;
        const scale = Math.min(1, maxEdge / Math.max(w0, h0));
        const width = Math.max(1, Math.round(w0 * scale));
        const height = Math.max(1, Math.round(h0 * scale));
        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;
        canvas.getContext('2d').drawImage(img, 0, 0, width, height);
        if (typeof img.close === 'function') img.close(); // release an ImageBitmap; a no-op for <img>
        const blob = await new Promise((resolve, reject) =>
            canvas.toBlob(b => b ? resolve(b) : reject(new Error('encode failed')), 'image/jpeg', 0.9));
        return { blob, mediaType: 'image/jpeg' };
    }

    // Decode a picked file to something drawImage accepts. TWO paths, because a specific photo can defeat
    // one and not the other, and a single hung decode is the "filename never appeared" bug: the <img>
    // element applies EXIF orientation and decodes HEIC where the browser supports it, while
    // createImageBitmap copes with some large/edge-case images <img> silently won't. Each is bounded by a
    // timeout so a decode that neither resolves NOR rejects (seen on mobile with large photos) can't hang
    // the pick forever — it falls through to the fallback, then to a visible "couldn't be read".
    async function decode(file) {
        try {
            return await withTimeout(loadImage(file), 15000);
        } catch {
            if (typeof createImageBitmap === 'function') {
                return await withTimeout(createImageBitmap(file, { imageOrientation: 'from-image' }), 15000);
            }
            throw new Error('decode failed');
        }
    }

    function withTimeout(promise, ms) {
        let timer;
        const timeout = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('decode timed out')), ms); });
        return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
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
