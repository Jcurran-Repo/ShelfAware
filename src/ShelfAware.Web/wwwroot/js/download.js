// Small helper for client-side file downloads + a page keyboard shortcut. Used by the Grocery List so
// you can grab the list as a text file (button, or press "D" when you're not typing in a field).

export function downloadText(filename, text) {
    const blob = new Blob([text], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
}

// One shortcut key at a time (this module is a singleton per page). Ignored while typing in an input or
// when a modifier is held, so it never fights browser/OS shortcuts or form entry.
let handler = null;

export function registerShortcut(dotnetRef, key) {
    unregister();
    handler = (e) => {
        const t = e.target;
        const typing = t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable);
        if (typing || e.ctrlKey || e.metaKey || e.altKey) return;
        if ((e.key || '').toLowerCase() === key) {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnDownloadShortcut');
        }
    };
    document.addEventListener('keydown', handler);
}

export function unregister() {
    if (handler) {
        document.removeEventListener('keydown', handler);
        handler = null;
    }
}
