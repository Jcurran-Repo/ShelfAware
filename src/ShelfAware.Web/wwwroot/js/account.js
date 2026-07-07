// Progressive enhancement for the static-SSR Register page: typing an invite code (joining an
// existing household) hides the new-household name field, and naming a new household hides the
// invite field — the two paths are mutually exclusive. Server-side validation in Register.razor
// stays authoritative; this only tidies the form. No-ops on pages without these elements.
(() => {
    const invite = document.getElementById('invite-code');
    const name = document.getElementById('household-name');
    if (!invite || !name) return;

    const inviteField = invite.closest('.auth-field');
    const nameField = name.closest('.auth-field');
    const sync = () => {
        const joining = invite.value.trim().length > 0;
        const naming = name.value.trim().length > 0;
        nameField.hidden = joining;
        inviteField.hidden = naming && !joining;
    };
    invite.addEventListener('input', sync);
    name.addEventListener('input', sync);
    sync();
})();
