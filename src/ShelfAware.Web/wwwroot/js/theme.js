// Theme resolver. Loaded SYNCHRONOUSLY in <head> (before the stylesheet paints) so the effective
// theme is stamped on <html> before first paint — no flash. The strict production CSP blocks inline
// scripts, so this must be an external 'self' script rather than an inline one.
//
// TWO independent axes, both stamped on <html>:
//   • data-theme     — "light" | "dark", resolved from "shelfaware.theme" (light|dark|absent=auto→OS).
//   • data-apptheme  — which palette, from "shelfaware.apptheme" (absent = the DEFAULT palette).
//                      app.css keys each alternative palette on [data-apptheme=NAME] × the light/dark
//                      blocks; the default palette also lives in the bare :root, so a JS-off / pre-stamp
//                      render still gets it. A stored palette that isn't known falls back to the default.
(function () {
    var THEME_KEY = 'shelfaware.theme';
    var APP_KEY = 'shelfaware.apptheme';
    var DEFAULT_APP = 'classic';        // the palette a visitor gets until they pick another
    var APP_THEMES = ['classic'];       // every known palette (incl. the default); guards a stale value
    var mq = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

    function themePref() {
        try { return localStorage.getItem(THEME_KEY) || 'auto'; } catch (e) { return 'auto'; }
    }
    function appPref() {
        try {
            var v = localStorage.getItem(APP_KEY);
            return v && APP_THEMES.indexOf(v) !== -1 ? v : DEFAULT_APP;
        } catch (e) { return DEFAULT_APP; }
    }

    function resolve(p) {
        if (p === 'dark') return 'dark';
        if (p === 'light') return 'light';
        return mq && mq.matches ? 'dark' : 'light'; // auto → the OS
    }

    function apply() {
        var root = document.documentElement;
        var eff = resolve(themePref());
        root.setAttribute('data-theme', eff);
        root.setAttribute('data-apptheme', appPref());
        // Keep the browser toolbar / status-bar colour in step with the resolved --bg token so it can't
        // drift from the palette (either axis can move it). Fall back to the literal only if the CSSOM
        // isn't ready — it is: the head stylesheet precedes this script, which blocks on it.
        var meta = document.getElementById('theme-color-meta');
        if (meta) {
            var bg = getComputedStyle(root).getPropertyValue('--bg').trim();
            meta.setAttribute('content', bg || (eff === 'dark' ? '#131619' : '#f6f7f9'));
        }
    }

    apply(); // pre-paint

    // Under "auto", a live OS switch should re-theme without a reload. An explicit choice ignores it.
    if (mq) {
        var onChange = function () { if (themePref() === 'auto') apply(); };
        if (mq.addEventListener) mq.addEventListener('change', onChange);
        else if (mq.addListener) mq.addListener(onChange); // older Safari
    }

    // Blazor enhanced navigation morphs <html> to match the server response, which never carries
    // data-theme / data-apptheme (both are client-only) — so it STRIPS our attributes and the page
    // would revert to the default theme (confirmed on the static account pages: login → register).
    // Re-stamp after each enhanced load. theme.js runs before blazor.web.js, so defer the hook; the
    // handler runs in the same task as the morph, before the browser repaints, so there's no flash.
    function hookEnhancedNav() {
        if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', apply);
    }
    if (window.Blazor && window.Blazor.addEventListener) hookEnhancedNav();
    else window.addEventListener('load', hookEnhancedNav);

    // The in-app switcher (ThemeSwitcher.razor) drives both axes.
    window.shelfawareTheme = {
        // Axis 1 — light / dark / auto.
        get: function () { return themePref(); },
        set: function (p) {
            try {
                if (p === 'light' || p === 'dark') localStorage.setItem(THEME_KEY, p);
                else localStorage.removeItem(THEME_KEY); // auto → no stored value
            } catch (e) { /* private mode / storage disabled — apply for this session anyway */ }
            apply();
        },
        // Axis 2 — which palette. The default palette stores NOTHING (bare :root), so a visitor who
        // never chose one, and one who chose the default explicitly, are the same clean state.
        getTheme: function () { return appPref(); },
        setTheme: function (name) {
            try {
                if (APP_THEMES.indexOf(name) !== -1 && name !== DEFAULT_APP) localStorage.setItem(APP_KEY, name);
                else localStorage.removeItem(APP_KEY); // default (or unknown) → no stored value
            } catch (e) { /* storage disabled — apply for this session anyway */ }
            apply();
        }
    };
})();
