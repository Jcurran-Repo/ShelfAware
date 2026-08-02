// Browser half of the guided walkthrough: where the visitor got to, and the ring around whatever the
// current step is talking about.
//
// Progress is per BROWSER, not per household — one member finishing the tour must not silently retire it
// for everyone else in the house, and it isn't pantry data, so it has no business in AppSettings (which
// "delete my data" clears wholesale).
(function () {
    const KEY = 'shelfaware.tour';
    const SPOT = 'tour-spot';
    const ACTIVE = 'tour-active';

    // Cancels an in-flight wait for an anchor. A visitor clicking Next faster than a page loads would
    // otherwise leave the previous step's poll running, to ring an element the tour has moved on from.
    let pending = 0;

    function clearSpot() {
        document.querySelectorAll('.' + SPOT).forEach(el => el.classList.remove(SPOT));
    }

    // The anchor usually isn't in the DOM yet: the tour navigates, and the page it lands on loads its
    // data asynchronously before it renders anything to ring. Poll briefly rather than couple the tour
    // to any page's lifecycle, and give up quietly — a step with no ring still reads.
    function waitFor(selector, token, deadline) {
        if (token !== pending) return;
        const el = document.querySelector(selector);
        if (el) {
            el.classList.add(SPOT);
            const still = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
            el.scrollIntoView({ block: 'center', behavior: still ? 'auto' : 'smooth' });
            return;
        }
        if (performance.now() < deadline) requestAnimationFrame(() => waitFor(selector, token, deadline));
    }

    window.shelfawareTour = {
        // → { step, started, done }. Anything unreadable (private mode, hand-edited, an older shape)
        // reads as "never started", which is the safe direction: at worst the visitor is offered the
        // tour again. `started` is what separates "left off on step one" from "never opened it" — the
        // tour resumes the first but must not ambush the second.
        load: function () {
            try {
                const raw = localStorage.getItem(KEY);
                if (!raw) return { step: 0, started: false, done: false };
                const saved = JSON.parse(raw);
                return {
                    step: Number(saved.step) || 0,
                    started: saved.started === true,
                    done: saved.done === true,
                };
            } catch { return { step: 0, started: false, done: false }; }
        },

        // Only ever called by a running tour, so `started` is true by construction.
        save: function (step, done) {
            try { localStorage.setItem(KEY, JSON.stringify({ step: step, started: true, done: done })); }
            catch { /* storage disabled — the tour just won't resume across visits, which is survivable */ }
        },

        // Ring the current step's anchor (and scroll it into view), replacing any previous ring.
        highlight: function (selector) {
            pending++;
            clearSpot();
            if (!selector) return;
            waitFor(selector, pending, performance.now() + 3000);
        },

        // Tour panel up or down. The body class is what lets the stylesheet get the panel and the voice
        // assistant out of each other's way on a narrow screen.
        setActive: function (active) {
            pending++;
            clearSpot();
            document.body.classList.toggle(ACTIVE, active === true);
        },
    };
})();
