// Colour-theme resolver. Loaded SYNCHRONOUSLY in <head> (before the stylesheet paints) so the
// effective theme is stamped on <html> before first paint — no flash. The strict production CSP
// blocks inline scripts, so this must be an external 'self' script rather than an inline one.
//
// Preference (localStorage "shelfaware.theme"): "light" | "dark" | absent(=auto).
// "auto" follows the OS (prefers-color-scheme). The resolved effective theme is written to
// data-theme = "light" | "dark" — which is the ONLY thing app.css keys on, so the dark palette
// lives in exactly one place (no prefers-color-scheme query is needed in the CSS).
(function () {
    var KEY = 'shelfaware.theme';
    var mq = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

    function pref() {
        try { return localStorage.getItem(KEY) || 'auto'; } catch (e) { return 'auto'; }
    }

    function resolve(p) {
        if (p === 'dark') return 'dark';
        if (p === 'light') return 'light';
        return mq && mq.matches ? 'dark' : 'light'; // auto → the OS
    }

    function apply() {
        var eff = resolve(pref());
        var root = document.documentElement;
        root.setAttribute('data-theme', eff);
        // Keep the browser toolbar / status-bar colour in step with the page background by reading the
        // resolved --bg token, so it can't drift from the palette. Fall back to the literal if the CSSOM
        // isn't ready (it is: the head stylesheet precedes this script, which blocks on it).
        var meta = document.getElementById('theme-color-meta');
        if (meta) {
            var bg = getComputedStyle(root).getPropertyValue('--bg').trim();
            meta.setAttribute('content', bg || (eff === 'dark' ? '#131619' : '#f6f7f9'));
        }
    }

    apply(); // pre-paint

    // Under "auto", a live OS switch should re-theme without a reload. An explicit choice ignores it.
    if (mq) {
        var onChange = function () { if (pref() === 'auto') apply(); };
        if (mq.addEventListener) mq.addEventListener('change', onChange);
        else if (mq.addListener) mq.addListener(onChange); // older Safari
    }

    // Blazor enhanced navigation morphs <html> to match the server response, which never carries
    // data-theme (the pref is client-only) — so it STRIPS our attribute and the page would revert to
    // light (confirmed on the static account pages: login → register). Re-stamp after each enhanced
    // load. theme.js runs before blazor.web.js, so defer the hook; the handler runs in the same task
    // as the morph, before the browser repaints, so there's no flash.
    function hookEnhancedNav() {
        if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', apply);
    }
    if (window.Blazor && window.Blazor.addEventListener) hookEnhancedNav();
    else window.addEventListener('load', hookEnhancedNav);

    // The in-app switcher (ThemeSwitcher.razor) drives this.
    window.shelfawareTheme = {
        get: function () { return pref(); },
        set: function (p) {
            try {
                if (p === 'light' || p === 'dark') localStorage.setItem(KEY, p);
                else localStorage.removeItem(KEY); // auto
            } catch (e) { /* private mode / storage disabled — apply for this session anyway */ }
            apply();
        }
    };
})();
