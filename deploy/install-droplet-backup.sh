#!/usr/bin/env bash
# Installs the nightly backup on the ShelfAware droplet: a systemd service + timer that runs
# deploy/backup-droplet.sh at 03:30 with the arguments given here. Re-run it any time to change
# them (it overwrites the unit files and reloads).
#
# ⚠️ It installs a COPY of the script to $INSTALL_DIR, and points the timer at that copy. The 03:30
# run must not depend on whether the repo is checked out, on which branch, or mid-deploy -- the
# same reason the family installer publishes its tool outside the repo.
#
# ⚠️ Offsite is a SEPARATE, one-time operator step. Without --rclone-remote the backup is same-disk
# only, which protects against a bad write and not at all against losing the droplet. To get
# offsite: install rclone, run `rclone config` (interactive, so a human does it once), then re-run
# this with --rclone-remote gdrive:shelfaware-backups (or whatever you configured).
set -euo pipefail

INSTALL_DIR=/usr/local/lib/shelfaware
DATA_DIR=/var/lib/shelfaware
DEST=/var/backups/shelfaware
KEEP_DAYS=14
RCLONE_REMOTE=""
AT="03:30"
UNIT=shelfaware-backup

usage() {
    cat >&2 <<USAGE
usage: sudo $0 [--data-dir DIR] [--dest DIR] [--keep-days N] [--rclone-remote REMOTE:PATH] [--at HH:MM]

  --data-dir       the app's DataDir (default: $DATA_DIR)
  --dest           local staging directory for backups (default: $DEST)
  --keep-days      days of db-* snapshots to keep (default: $KEEP_DAYS)
  --rclone-remote  a configured rclone remote to mirror --dest to ('' = same-disk only)
  --at             daily run time, systemd OnCalendar clock part (default: $AT)
USAGE
    exit 2
}

while [ $# -gt 0 ]; do
    case "$1" in
        --data-dir)      DATA_DIR="${2:?--data-dir needs a value}"; shift 2 ;;
        --dest)          DEST="${2:?--dest needs a value}"; shift 2 ;;
        --keep-days)     KEEP_DAYS="${2:?--keep-days needs a value}"; shift 2 ;;
        --rclone-remote) RCLONE_REMOTE="${2:?--rclone-remote needs a value}"; shift 2 ;;
        --at)            AT="${2:?--at needs a value}"; shift 2 ;;
        -h|--help)       usage ;;
        *)               echo "error: unknown argument '$1'" >&2; usage ;;
    esac
done

# Checked FIRST, before anything is copied or written: a run that cannot finish should fail in a
# second with a clear message rather than halfway through installing a timer.
[ "$(id -u)" -eq 0 ] || { echo "error: run this with sudo -- it writes systemd units" >&2; exit 1; }

case "$KEEP_DAYS" in
    ''|*[!0-9]*) echo "error: --keep-days must be a whole number, got '$KEEP_DAYS'" >&2; exit 2 ;;
esac
[ "$KEEP_DAYS" -ge 1 ] || { echo "error: --keep-days must be at least 1" >&2; exit 2; }

# [0-2][0-9] would admit 25:00, which systemd then rejects at enable time -- after the units are
# already written. Spell the real hour range out so a typo fails before anything is installed.
case "$AT" in
    [01][0-9]:[0-5][0-9]|2[0-3]:[0-5][0-9]) ;;
    *) echo "error: --at must be a 24-hour HH:MM between 00:00 and 23:59, got '$AT'" >&2; exit 2 ;;
esac

SOURCE="$(cd "$(dirname "$0")" && pwd)/backup-droplet.sh"
[ -f "$SOURCE" ] || { echo "error: $SOURCE not found -- run this from the deploy/ directory" >&2; exit 1; }

if [ -n "$RCLONE_REMOTE" ] && ! command -v rclone >/dev/null 2>&1; then
    # Refuse rather than install a timer that will fail every night at 03:30 where nobody is
    # looking. The backup script says the same thing at run time; this says it while a human is here.
    echo "error: --rclone-remote given but rclone is not installed." >&2
    echo "       Install it and run 'rclone config' first, or drop --rclone-remote." >&2
    exit 1
fi

install -d -m 755 "$INSTALL_DIR"
install -m 755 "$SOURCE" "$INSTALL_DIR/backup-droplet.sh"
install -d -m 700 "$DEST"

ARGS="--data-dir $DATA_DIR --dest $DEST --keep-days $KEEP_DAYS"
if [ -n "$RCLONE_REMOTE" ]; then ARGS="$ARGS --rclone-remote $RCLONE_REMOTE"; fi

cat >"/etc/systemd/system/$UNIT.service" <<UNITFILE
[Unit]
Description=ShelfAware nightly backup
# Wants, not Requires: a backup is worth taking even if the app is down -- especially then.
Wants=network-online.target
After=network-online.target

[Service]
Type=oneshot
# Runs as root: it reads the service account's data directory and writes under /var/backups.
ExecStart=$INSTALL_DIR/backup-droplet.sh $ARGS
UNITFILE

cat >"/etc/systemd/system/$UNIT.timer" <<UNITFILE
[Unit]
Description=ShelfAware nightly backup at $AT

[Timer]
OnCalendar=*-*-* $AT:00
# Persistent: a droplet that was off or rebooting at $AT takes the backup when it comes back,
# rather than silently skipping a night.
Persistent=true
# A small spread so the backup never lands exactly on top of another timer.
RandomizedDelaySec=300

[Install]
WantedBy=timers.target
UNITFILE

systemctl daemon-reload
systemctl enable --now "$UNIT.timer"

echo "Installed $UNIT.timer"
echo "  script:  $INSTALL_DIR/backup-droplet.sh"
echo "  command: $INSTALL_DIR/backup-droplet.sh $ARGS"
echo "  next:    $(systemctl show "$UNIT.timer" -p NextElapseUSecRealtime --value)"
if [ -z "$RCLONE_REMOTE" ]; then
    echo
    echo "  ⚠️ Same-disk only -- nothing leaves this droplet. Install rclone, run 'rclone config',"
    echo "     then re-run this with --rclone-remote to get backups off the box."
fi
echo
echo "Rehearse it now without writing anything:"
echo "  $INSTALL_DIR/backup-droplet.sh $ARGS --dry-run"
echo "Run it for real:  systemctl start $UNIT.service && journalctl -u $UNIT -n 30"
