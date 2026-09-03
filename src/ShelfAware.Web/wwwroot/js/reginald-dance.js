// Easter egg: Reginald (the mascot, "Eggs") occasionally dances. Click the header mascot to make him
// dance on demand, and once in a rare while he does a little jig on his own when the app (re)loads. Purely
// a visual delight — the clip is muted, decorative, and non-interactive; missing it loses nothing.
//
// Self-hosted `self` script (strict CSP: script-src 'self', media-src 'self' — the clip is in wwwroot/media).
// ⚠️ Click handling is DELEGATED on document, not attached to the mascot node: MainLayout is InteractiveServer,
// so Blazor REPLACES the prerendered .brand-mascot element after it connects — a handler bound to the node
// would be discarded. Delegation matches whatever .brand-mascot exists at click time, re-render or not.
(function () {
    'use strict';

    const CLIP = 'media/reginald-dance.mp4';
    const AUTO_CHANCE = 0.04;   // ~1 in 25 APP loads (a fresh load / sign-in — SPA nav doesn't re-run this) he
                                // dances unprompted. A rare treat; tune freely.
    const POP_PX = 88;          // how big he gets while dancing (pops out of the little header badge)
    const CLIP_MS = 6000;       // clip is ~5s; the watchdog cleanup grace (see dance())

    let dancing = false;        // one dance at a time

    function reducedMotion() {
        try { return window.matchMedia('(prefers-reduced-motion: reduce)').matches; }
        catch { return false; }
    }

    // Play the clip popped out and centred over the mascot, then restore. Positioned with the mascot's
    // live rect (fixed), so it works the same whether the badge is in the sidebar rail or the mobile top bar.
    function dance(anchor) {
        if (dancing || !anchor || !anchor.isConnected) return;
        dancing = true;

        const r = anchor.getBoundingClientRect();
        const clip = document.createElement('video');
        clip.className = 'reginald-dance-clip';
        clip.src = CLIP;
        clip.muted = true;          // required for the unprompted auto-play, and kinder on the click path too
        clip.playsInline = true;
        clip.setAttribute('aria-hidden', 'true');
        clip.style.width = clip.style.height = POP_PX + 'px';
        clip.style.left = Math.round(r.left + r.width / 2 - POP_PX / 2) + 'px';
        clip.style.top = Math.round(r.top + r.height / 2 - POP_PX / 2) + 'px';

        let watchdog = 0;
        const done = () => { clearTimeout(watchdog); clip.remove(); dancing = false; };
        clip.addEventListener('ended', done);
        clip.addEventListener('error', done);
        // ⚠️ Watchdog is load-bearing: iOS/PWA PAUSE a <video> on backgrounding (lock screen, app switch) and
        // a network stall can wedge it — WITHOUT ever firing `ended`. Without this, the flag would stay true
        // (egg untriggerable) and a frozen frame would sit fixed over the header until a full reload. This
        // guarantees cleanup + reset on every path. (done is idempotent, so a later `ended` is a harmless no-op.)
        watchdog = window.setTimeout(done, CLIP_MS);
        document.body.appendChild(clip);
        const p = clip.play();
        if (p && typeof p.catch === 'function') p.catch(done); // autoplay blocked / decode failed → clean up
    }

    // Explicit click plays even under reduced-motion (an opt-in gesture, not motion the user didn't ask for).
    document.addEventListener('click', function (e) {
        const m = e.target instanceof Element ? e.target.closest('.brand-mascot') : null;
        if (m) dance(m);
    });

    // The serendipity: once per document load, a small chance he starts dancing on his own — suppressed
    // under prefers-reduced-motion. Deferred so MainLayout has rendered the mascot; if it isn't there yet
    // (slow circuit), this load simply doesn't roll one — it's meant to be rare anyway.
    if (!reducedMotion() && Math.random() < AUTO_CHANCE) {
        window.setTimeout(function () {
            const marks = [...document.querySelectorAll('.brand-mascot')];
            // Pick the ON-SCREEN mascot by its live rect — not offsetParent, which can't tell the closed
            // mobile drawer (visibility:hidden, off-canvas at left ≈ -250px) from the visible bar.
            const onScreen = marks.find(m => { const r = m.getBoundingClientRect(); return r.width > 0 && r.left >= 0; });
            if (onScreen) dance(onScreen);
        }, 2500);
    }
})();
