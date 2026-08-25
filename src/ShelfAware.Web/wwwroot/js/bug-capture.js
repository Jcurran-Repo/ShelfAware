// Client-side diagnostic capture for bug reports. Loaded in <head> (after theme.js) so the error ring
// buffer is installed as EARLY as possible — it catches failures during startup and module loads, not
// just after the first interaction. The strict production CSP blocks inline scripts, so this is an
// external 'self' script like the rest.
//
// Nothing here persists anything or phones home: it keeps a bounded in-memory buffer of recent errors,
// and exposes shelfawareBugCapture.snapshot() which the "Report a bug" click reads on the page the
// reporter is still looking at. What that snapshot contains is then SHOWN to the reporter on the form,
// with each section removable, before any of it is stored — never captured silently.
(function () {
    'use strict';

    var MAX_ERRORS = 20;   // most recent client-side errors kept
    var MAX_MSG = 300;     // per error line
    var MAX_CONTENT = 8000; // page-content text
    var buffer = [];

    function push(kind, message) {
        if (!message) return;
        message = String(message);
        if (message.length > MAX_MSG) message = message.slice(0, MAX_MSG) + '…';
        var stamp;
        try { stamp = new Date().toLocaleTimeString(); } catch (e) { stamp = ''; }
        buffer.push((stamp ? stamp + ' ' : '') + kind + ': ' + message);
        if (buffer.length > MAX_ERRORS) buffer.shift();
    }

    // Uncaught errors — and, in the capture phase, resource load failures (img/script/link), which arrive
    // with no message but a target.
    window.addEventListener('error', function (e) {
        if (e && e.message) {
            push('error', e.message + (e.filename ? ' @ ' + e.filename + ':' + (e.lineno || 0) : ''));
        } else if (e && e.target && (e.target.src || e.target.href)) {
            push('resource', 'failed to load ' + (e.target.src || e.target.href));
        }
    }, true);

    window.addEventListener('unhandledrejection', function (e) {
        var r = e && e.reason;
        push('promise', r && r.message ? r.message : (r != null ? String(r) : 'unhandled rejection'));
    });

    // Capture app-logged errors (interop failures, CSP violations the app logs, Blazor circuit errors)
    // without changing what the console shows. Guarded so capturing can never itself throw.
    var nativeError = typeof console !== 'undefined' && console.error ? console.error.bind(console) : null;
    if (nativeError) {
        console.error = function () {
            try {
                var parts = Array.prototype.map.call(arguments, function (a) {
                    return a && a.message ? a.message : String(a);
                });
                push('console', parts.join(' '));
            } catch (_) { /* never let capturing an error throw */ }
            nativeError.apply(console, arguments);
        };
    }

    function theme() {
        // data-theme always holds the RESOLVED theme (theme.js stamps it even under Auto); the stored
        // preference tells us whether that was an explicit choice or "follow the OS".
        var eff = document.documentElement.getAttribute('data-theme') || 'light';
        var pref = 'auto';
        try { if (window.shelfawareTheme) pref = window.shelfawareTheme.get(); } catch (e) { /* ignore */ }
        return pref === 'auto' ? eff + ' (auto)' : eff;
    }

    function pageContent() {
        var el = document.getElementById('main-content') || document.body;
        if (!el) return null;
        var text = (el.innerText || '').replace(/\n{3,}/g, '\n\n').trim();
        if (text.length > MAX_CONTENT) text = text.slice(0, MAX_CONTENT) + '\n…(truncated)';
        return text.length ? text : null;
    }

    function match(q) {
        try { return !!(window.matchMedia && window.matchMedia(q).matches); } catch (e) { return false; }
    }

    window.shelfawareBugCapture = {
        // Read at the "Report a bug" click. Returns the FULL snapshot (both sections); the form shows it
        // and lets the reporter drop either section before it's stored. Deliberately never throws — a
        // capture failure must not block filing the report — so a bad field degrades to null.
        snapshot: function () {
            var tz = null;
            try { tz = Intl.DateTimeFormat().resolvedOptions().timeZone || null; } catch (e) { /* ignore */ }
            var localTime = null;
            try { localTime = new Date().toLocaleString(); } catch (e) { /* ignore */ }
            return {
                diagnostics: {
                    url: location.pathname + location.search,
                    viewport: window.innerWidth + 'x' + window.innerHeight +
                        ' @' + (window.devicePixelRatio || 1) + 'x',
                    userAgent: (navigator.userAgent || '').slice(0, 400),
                    theme: theme(),
                    reducedMotion: match('(prefers-reduced-motion: reduce)'),
                    localTime: localTime,
                    timeZone: tz,
                    jsErrors: buffer.slice()
                },
                pageContent: pageContent()
            };
        }
    };
})();
