#!/usr/bin/env bash
# ShelfAware droplet-side install. deploy.ps1 uploads /tmp/shelfaware.tar.gz plus this
# script and runs it as root. It stages the new build OUTSIDE the live directory so the
# stopped window is seconds, then swaps, keeping the previous build at
# /opt/shelfaware.prev. If the new build does not come up, it ROLLS BACK on its own: the
# previous build goes back to /opt/shelfaware and is started, the failed one is kept at
# /opt/shelfaware.failed for inspection, and the script still exits non-zero so the deploy
# that ran it goes red either way.
# First-time box setup (user, env file, unit, Caddy) is docs/deploy-droplet.md.
set -euo pipefail

APP_DIR=/opt/shelfaware
STAGING=$APP_DIR.staging
PREV=$APP_DIR.prev
FAILED=$APP_DIR.failed
TARBALL=/tmp/shelfaware.tar.gz
SERVICE=shelfaware

# Start, then two spaced checks: the first catches an instant crash (bad env file), the second a
# slow one (first-boot schema work on a small box). A pathological crash loop could still thread
# the needle between them -- the journalctl line is the real confirmation. Returns the service's
# state as its own exit code, so it reads cleanly under `if` and `set -e` cannot short-circuit it.
start_and_wait() {
    systemctl start "$SERVICE"
    sleep 3
    if systemctl is-active --quiet "$SERVICE"; then
        sleep 7
    fi
    systemctl is-active --quiet "$SERVICE"
}

if [ ! -f "$TARBALL" ]; then
    echo "error: $TARBALL not found -- run deploy.ps1, which uploads it first" >&2
    exit 1
fi

if ! id -u shelfaware >/dev/null 2>&1; then
    echo "error: no 'shelfaware' user -- finish first-time setup (docs/deploy-droplet.md) first" >&2
    exit 1
fi

rm -rf "$STAGING"
mkdir -p "$STAGING"
tar -xzf "$TARBALL" -C "$STAGING"
# Root-owned, world-readable, exec bit restored (a tar written on Windows carries neither
# useful ownership nor an exec bit; a+x rather than a bare +x because +x defers to the
# root umask, and a hardened umask would silently strip the service account's execute).
# The service account gets READ access only: it must not be able to rewrite its own
# binary -- Restart=always would happily relaunch a tampered one. The app writes only
# under its DataDir, never here.
chown -R root:root "$STAGING"
chmod -R u+rwX,go+rX "$STAGING"
chmod a+x "$STAGING/ShelfAware.Web"

if systemctl is-active --quiet "$SERVICE"; then
    systemctl stop "$SERVICE"
fi

rm -rf "$PREV"
if [ -d "$APP_DIR" ]; then
    mv "$APP_DIR" "$PREV"
fi
mv "$STAGING" "$APP_DIR"

rm -f "$TARBALL"

# On the very first deploy the unit isn't installed yet -- say so instead of failing.
if ! systemctl cat "$SERVICE" >/dev/null 2>&1; then
    echo "Files are in $APP_DIR, but no '$SERVICE' service is installed yet."
    echo "Finish first-time setup (docs/deploy-droplet.md), then: systemctl enable --now $SERVICE"
    rm -f /tmp/shelfaware-install.sh
    exit 0
fi

if start_and_wait; then
    echo "Deployed and running. Logs: journalctl -u $SERVICE -f"
else
    echo "error: $SERVICE failed to start on the new build. See: journalctl -u $SERVICE -n 50 --no-pager" >&2
    if [ ! -d "$PREV" ]; then
        echo "No previous build at $PREV to roll back to (first deploy?) -- the box is DOWN." >&2
        exit 1
    fi

    # Roll back, rather than print the command and leave the box down for however long it takes
    # someone to read this. The failed build is kept, renamed, so the thing that broke can still
    # be inspected; the previous build goes back under the name the unit execs.
    echo "Rolling back to the previous build at $PREV (the failed one is kept at $FAILED)..." >&2
    # `|| true`: the unit may be mid-crash-loop or already in "failed" state, and either way a stop
    # is a no-op we must not abort on -- the swap below is what matters.
    systemctl stop "$SERVICE" || true
    rm -rf "$FAILED"
    mv "$APP_DIR" "$FAILED"
    mv "$PREV" "$APP_DIR"
    # ⚠️ Required once the unit's StartLimitBurst has tripped (shelfaware.service): systemd then
    # refuses `systemctl start` until the counter is reset, so without this the rollback would
    # swap the files and never start anything.
    systemctl reset-failed "$SERVICE" || true
    if start_and_wait; then
        echo "ROLLED BACK: the previous build is running again from $APP_DIR." >&2
        echo "The failed build is at $FAILED -- inspect it, then rm -rf it (nothing else removes it until the next rollback)." >&2
    else
        echo "ROLLBACK FAILED: the previous build did not start either -- the box is DOWN." >&2
        echo "Failed new build: $FAILED. Previous build (now in place): $APP_DIR. See: journalctl -u $SERVICE -n 100 --no-pager" >&2
    fi
    # Non-zero either way: a deploy that did not land is a failed deploy, even when the box
    # recovered -- the workflow that ran this must go red.
    exit 1
fi

rm -f /tmp/shelfaware-install.sh
