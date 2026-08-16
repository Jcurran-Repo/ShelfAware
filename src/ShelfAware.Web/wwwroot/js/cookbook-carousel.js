// Browser half of the cookbook's preview shelf: mouse click-drag, and telling .NET which preview
// settled in the centre so the detail panel, the "N of M" announcement, and the emphasised card stay
// in sync. Touch and trackpad swipe come free from CSS scroll-snap — this module only adds what they
// don't: a mouse drag, and a centred-card report.
//
// `index` is .NET's source of truth (it drives the detail panel below). The loop guard is simple:
// reporting NEVER scrolls, and a programmatic scroll pre-sets `lastReported` to its own target so the
// scroll it causes reports the same index, which .NET short-circuits — so the two can't ping-pong.
//
// One cookbook page exists at a time, so module-level state (a singleton per import URL) is fine, the
// same shape readaloud.js uses. `init` rebinds cleanly, so it survives the shelf being recreated when a
// filter empties the deck and then refills it.
let shelf = null;
let dotnet = null;
let pointer = null;   // { startX, startLeft } while a mouse drag is in progress
let dragged = false;  // a real drag happened — swallow the click it ends with
let settleTimer = 0;
let lastReported = -1;

export function init(el, dotnetRef) {
    unbind();
    shelf = el;
    dotnet = dotnetRef;
    lastReported = -1;

    shelf.addEventListener('scroll', onScroll, { passive: true });
    shelf.addEventListener('pointerdown', onPointerDown);
    // Capture phase, so a drag that ends over a preview swallows the click before it "picks" that card.
    shelf.addEventListener('click', onClickCapture, true);
    shelf.addEventListener('keydown', onKeyDown);
    window.addEventListener('pointermove', onPointerMove);
    window.addEventListener('pointerup', onPointerUp);
}

function reduceMotion() {
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

// Nearest-centre is exact where an IntersectionObserver only approximates: find the preview whose centre
// is closest to the shelf's centre. Uses viewport rects so it's robust to how the cards are positioned.
function centredIndex() {
    const cards = shelf.querySelectorAll('[data-card]');
    if (cards.length === 0) return -1;
    const box = shelf.getBoundingClientRect();
    const mid = box.left + box.width / 2;
    let best = -1;
    let bestDist = Infinity;
    cards.forEach(card => {
        const r = card.getBoundingClientRect();
        const d = Math.abs(r.left + r.width / 2 - mid);
        if (d < bestDist) { bestDist = d; best = Number(card.dataset.card); }
    });
    return best;
}

function onScroll() {
    // Debounce to the scroll SETTLE — a drag, momentum fling and snap all end in a quiet moment. Reporting
    // every frame would spam the circuit and fight a programmatic smooth-scroll still in flight.
    clearTimeout(settleTimer);
    settleTimer = setTimeout(report, 120);
}

function report() {
    if (!shelf || !dotnet) return;
    const i = centredIndex();
    if (i < 0 || i === lastReported) return;
    lastReported = i;
    dotnet.invokeMethodAsync('OnCentered', i);
}

// Centre the i-th preview — for keyboard paging, click-a-preview, and a filter reset. Pre-setting
// lastReported means the scroll this triggers won't be reported back to .NET as a fresh change.
export function scrollToIndex(i, smooth) {
    if (!shelf) return;
    const card = shelf.querySelector(`[data-card="${i}"]`);
    if (!card) return;
    lastReported = i;
    card.scrollIntoView({
        inline: 'center',
        block: 'nearest',
        behavior: smooth && !reduceMotion() ? 'smooth' : 'auto',
    });
}

// Stop the focused shelf from ALSO scrolling natively on the paging keys — Blazor's @onkeydown still fires
// (preventDefault doesn't stop propagation), so OnKey does the managed centre + scroll. Tab is left alone so
// focus can still leave the shelf.
function onKeyDown(e) {
    if (e.key === 'ArrowLeft' || e.key === 'ArrowRight' || e.key === 'Home' || e.key === 'End') {
        e.preventDefault();
    }
}

// ── mouse click-drag ── (touch/trackpad already scroll the shelf natively, so leave them to scroll-snap)
function onPointerDown(e) {
    if (e.pointerType !== 'mouse' || e.button !== 0) return;
    pointer = { startX: e.clientX, startLeft: shelf.scrollLeft };
    dragged = false;
}

function onPointerMove(e) {
    if (!pointer) return;
    // The button was released off-window, so no pointerup ever reached us. End the drag rather than keep
    // following a mouse with no button held.
    if (e.buttons === 0) { endDrag(false); return; }
    const dx = e.clientX - pointer.startX;
    if (!dragged && Math.abs(dx) > 4) {
        dragged = true;
        shelf.classList.add('is-dragging'); // suspends snap + suppresses text selection while dragging
    }
    if (dragged) {
        shelf.scrollLeft = pointer.startLeft - dx;
        e.preventDefault();
    }
}

function onPointerUp() {
    if (!pointer) return;
    endDrag(true); // a normal release is followed by a click; keep `dragged` set so onClickCapture swallows it
}

// End a drag (or a plain click that never moved). `clickFollows` is true for an in-window release, which the
// browser follows with a click that onClickCapture must swallow — so keep `dragged` set until then. An
// off-window release fires no click, so clear `dragged` now or the NEXT real click would be suppressed.
function endDrag(clickFollows) {
    pointer = null;
    if (dragged) shelf.classList.remove('is-dragging'); // snap re-engages → settles → reports the centre
    if (!clickFollows) dragged = false;
}

function onClickCapture(e) {
    if (!dragged) return;
    e.preventDefault();
    e.stopPropagation();
    dragged = false;
}

function unbind() {
    if (shelf) {
        shelf.removeEventListener('scroll', onScroll);
        shelf.removeEventListener('pointerdown', onPointerDown);
        shelf.removeEventListener('click', onClickCapture, true);
        shelf.removeEventListener('keydown', onKeyDown);
    }
    window.removeEventListener('pointermove', onPointerMove);
    window.removeEventListener('pointerup', onPointerUp);
    clearTimeout(settleTimer);
    shelf = null;
    dotnet = null;
    pointer = null;
    dragged = false;
}

export function dispose() {
    unbind();
}
