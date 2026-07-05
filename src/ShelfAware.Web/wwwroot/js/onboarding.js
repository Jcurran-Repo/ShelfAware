// Tiny persistence for the dashboard "getting started" banner — remembers a dismissal across visits.
window.shelfawareOnboarding = {
    isDismissed: function () {
        try { return localStorage.getItem('shelfaware.onboardingDismissed') === '1'; }
        catch { return false; }
    },
    dismiss: function () {
        try { localStorage.setItem('shelfaware.onboardingDismissed', '1'); }
        catch { /* private mode / storage disabled — the banner just won't remember, which is fine */ }
    },
};
