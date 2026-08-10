#!/usr/bin/env bash
# ShelfAware droplet-side install. deploy.ps1 uploads /tmp/shelfaware.tar.gz plus this
# script and runs it as root. It stages the new build OUTSIDE the live directory so the
# stopped window is seconds, then swaps, keeping the previous build at
# /opt/shelfaware.prev -- roll back by moving it back and starting the service.
# First-time box setup (user, env file, unit, Caddy) is docs/deploy-droplet.md.
set -euo pipefail

APP_DIR=/opt/shelfaware
STAGING=$APP_DIR.staging
PREV=$APP_DIR.prev
TARBALL=/tmp/shelfaware.tar.gz
SERVICE=shelfaware

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

systemctl start "$SERVICE"
# Two spaced checks: the first catches an instant crash (bad env file), the second a slow
# one (first-boot schema work on a small box). A pathological crash loop could still
# thread the needle between them -- the journalctl line is the real confirmation.
sleep 3
if systemctl is-active --quiet "$SERVICE"; then
    sleep 7
fi
if systemctl is-active --quiet "$SERVICE"; then
    echo "Deployed and running. Logs: journalctl -u $SERVICE -f"
else
    echo "error: $SERVICE failed to start. See: journalctl -u $SERVICE -n 50 --no-pager" >&2
    if [ -d "$PREV" ]; then
        echo "Previous build is at $PREV -- mv it back to $APP_DIR and 'systemctl start $SERVICE' to roll back." >&2
    fi
    exit 1
fi

rm -f /tmp/shelfaware-install.sh
