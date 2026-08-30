// The /about wishlist's client-side dedup: a per-browser flag so a return visitor isn't asked again,
// and a stop against honest double-clicks. Progressive enhancement only — with JS off (or storage
// blocked) the form just shows and a re-submit is harmless, because the interest counter is soft by
// design. Loaded globally from App.razor like account.js; it no-ops on any page with no wishlist marks.
(function () {
    "use strict";
    var KEY = "shelfaware.wishlisted";

    function apply() {
        // A successful static-SSR submit rendered the done state — remember it for next time.
        if (document.querySelector("[data-wishlist-submitted]")) {
            try { localStorage.setItem(KEY, "1"); } catch (e) { /* private mode / storage blocked */ }
            return;
        }
        // A return visit: if this browser already reserved, swap the form for the thank-you note.
        var already = false;
        try { already = localStorage.getItem(KEY) === "1"; } catch (e) { already = false; }
        if (!already) return;
        document.querySelectorAll("[data-wishlist-form]").forEach(function (el) { el.hidden = true; });
        document.querySelectorAll("[data-wishlist-done]").forEach(function (el) { el.hidden = false; });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", apply);
    } else {
        apply();
    }
})();
