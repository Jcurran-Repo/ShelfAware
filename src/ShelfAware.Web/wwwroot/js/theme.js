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
        document.documentElement.setAttribute('data-theme', eff);
        // Keep the browser toolbar / status bar colour in step with the page background.
        var meta = document.getElementById('theme-color-meta');
        if (meta) meta.setAttribute('content', eff === 'dark' ? '#131619' : '#f6f7f9');
    }

    apply(); // pre-paint

    // Under "auto", a live OS switch should re-theme without a reload. An explicit choice ignores it.
    if (mq) {
        var onChange = function () { if (pref() === 'auto') apply(); };
        if (mq.addEventListener) mq.addEventListener('change', onChange);
        else if (mq.addListener) mq.addListener(onChange); // older Safari
    }

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
