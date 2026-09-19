#!/usr/bin/env bash
# Nightly backup for the ShelfAware droplet -- the box that takes paying customers, so this is the
# one whose absence is expensive. Same design as deploy/backup-family.ps1, which has already been
# through a review that caught a data-loss chain and four fail-safe defects; reuse the reviewed
# shape rather than invent a second one. Differences are platform only: sqlite3 for VACUUM INTO,
# rsync for the mirrors, a systemd timer for the schedule.
#
# How a LIVE database is snapshotted safely: `VACUUM INTO` over a READ-ONLY connection
# (file:...?mode=ro). Under WAL a reader never blocks the app's writers, and the copy is taken
# inside one read transaction -- a consistent, self-contained .db with no -wal/-shm sidecars. A
# plain `cp` of a live database is exactly the torn-backup mistake this avoids. Then
# `PRAGMA integrity_check` runs against the COPY, because a backup nobody has ever opened is a
# hope, not a backup.
#
# Layout under $DEST:
#   db-YYYY-MM-DD-HHMMSS/shelfaware.db + auth.db + manifest.txt   point-in-time snapshots, kept
#                                                                 for $KEEP_DAYS
#   files/{receipts,recipe-images,keys,tts-cache}                 a ROLLING rsync mirror of the
#                                                                 blob trees (append-mostly;
#                                                                 per-stamp copies would balloon)
#   backup-log.txt                                                one line per run, success or fail
#
# --delete discipline (the publish-family.ps1 lesson -- a mirror dry run once nearly ate an
# unrelated backup): mirrors target ONLY the dedicated subfolders this script creates under
# $DEST/files. Never point --dest at a directory that holds anything else. --dry-run rehearses:
# rsync runs with -n and retention deletions are printed, not performed.
#
# Retention is keyed on the FOLDER NAME's date stamp, never filesystem times -- deterministic, and
# testable by creating a fake old-named folder. A directory whose name does not parse as a date is
# LEFT ALONE rather than guessed at, so a stray folder can never be mis-deleted (nor keep the run
# from pruning the real ones). Only db-* stamp folders under $DEST are ever removed; the files/
# mirror is never touched by retention.
#
# Offsite: $DEST is a LOCAL staging directory on purpose, and --rclone-remote is how backups leave
# the machine -- a backup on the same droplet is not a backup against losing the droplet. The sync
# runs with --backup-dir, so a file it would delete or overwrite offsite is MOVED into a dated
# archive (<remote>-archive/<stamp>) rather than erased; without it a bad local night (corruption
# the mirror faithfully copied) would propagate offsite and leave the blob trees -- which have no
# per-stamp local history -- unrecoverable anywhere. The archive grows and is the operator's to
# prune. One-time setup: install rclone, run `rclone config` (interactive, the operator does this
# once), then re-run install-droplet-backup.sh with --rclone-remote so the timer carries it.
# Without it the backup is same-disk only, and that is stated here rather than implied to be more.
#
# To RESTORE: stop the service, copy the chosen db-* snapshot's two .db files into $DATA_DIR
# (deleting any -wal/-shm beside them, which belong to the database you are replacing), restore the
# files/ trees, chown to the service account, start. The snapshot files are ordinary SQLite
# databases -- no tooling needed to read one.
set -euo pipefail

DATA_DIR=/var/lib/shelfaware
DEST=/var/backups/shelfaware
KEEP_DAYS=14
RCLONE_REMOTE=""
DRY_RUN=0

usage() {
    cat >&2 <<USAGE
usage: $0 [--data-dir DIR] [--dest DIR] [--keep-days N] [--rclone-remote REMOTE:PATH] [--dry-run]

  --data-dir       the app's DataDir (default: $DATA_DIR)
  --dest           local staging directory for backups (default: $DEST)
                   ⚠️ must hold nothing but this script's output -- the mirrors delete
  --keep-days      how many days of db-* snapshots to keep (default: $KEEP_DAYS)
  --rclone-remote  a configured rclone remote to mirror --dest to ('' = same-disk only)
  --dry-run        rehearse everything: no writes, no deletions, no offsite sync
USAGE
    exit 2
}

while [ $# -gt 0 ]; do
    case "$1" in
        --data-dir)      DATA_DIR="${2:?--data-dir needs a value}"; shift 2 ;;
        --dest)          DEST="${2:?--dest needs a value}"; shift 2 ;;
        --keep-days)     KEEP_DAYS="${2:?--keep-days needs a value}"; shift 2 ;;
        --rclone-remote) RCLONE_REMOTE="${2:?--rclone-remote needs a value}"; shift 2 ;;
        --dry-run)       DRY_RUN=1; shift ;;
        -h|--help)       usage ;;
        *)               echo "error: unknown argument '$1'" >&2; usage ;;
    esac
done

case "$KEEP_DAYS" in
    ''|*[!0-9]*) echo "error: --keep-days must be a whole number, got '$KEEP_DAYS'" >&2; exit 2 ;;
esac
[ "$KEEP_DAYS" -ge 1 ] || { echo "error: --keep-days must be at least 1" >&2; exit 2; }

# ⚠️ Held in a variable, never inlined as a command substitution: `echo -n` prints NOTHING (it is
# the suppress-newline flag), so the obvious-looking inline form silently drops rsync's -n and a
# "rehearsal" mirrors for real. A dry run that isn't one is worse than having no dry run at all.
RSYNC_DRY=""
if [ "$DRY_RUN" -eq 1 ]; then RSYNC_DRY="-n"; fi

STAMP=$(date -u +%Y-%m-%d-%H%M%S)
LOG="$DEST/backup-log.txt"
DATABASES="shelfaware.db auth.db"
TREES="receipts recipe-images keys tts-cache"

# One line per run, success or failure -- an operator reading only this file must be able to tell
# the difference. Best-effort: a log that cannot be written must not fail a backup that worked.
log_line() {
    if [ "$DRY_RUN" -eq 1 ]; then return 0; fi
    mkdir -p "$DEST" 2>/dev/null || return 0
    printf '%s  %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$1" >>"$LOG" 2>/dev/null || true
}

fail() {
    echo "error: $1" >&2
    # ⚠️ Take the half-written snapshot with us. A run that dies after the first database has been
    # copied would otherwise leave a correctly-named db-<stamp>/ holding ONE of the two -- which is
    # indistinguishable from a good snapshot until somebody restores from it and finds no accounts.
    # (A run killed outright can still leave a .incomplete directory; that one is named so nobody
    # mistakes it, and retention sweeps it on age like any other.)
    if [ -n "${SNAPSHOT:-}" ] && [ -d "$SNAPSHOT" ]; then rm -rf "$SNAPSHOT"; fi
    log_line "FAILED ($STAMP): $1"
    exit 1
}

command -v sqlite3 >/dev/null 2>&1 || fail "sqlite3 is not installed (apt-get install -y sqlite3)"
command -v rsync   >/dev/null 2>&1 || fail "rsync is not installed (apt-get install -y rsync)"
[ -d "$DATA_DIR" ] || fail "data directory $DATA_DIR does not exist"

[ "$DRY_RUN" -eq 1 ] && echo "ShelfAware backup $STAMP (DRY RUN)" || echo "ShelfAware backup $STAMP"
echo "  from $DATA_DIR -> $DEST"

# ---- 1. Point-in-time database snapshots -------------------------------------------------------
# Built under .incomplete and renamed only once BOTH databases have been copied and verified, so a
# directory named db-<stamp> is always a snapshot you can restore from.
SNAPSHOT="$DEST/db-$STAMP.incomplete"
FINAL="$DEST/db-$STAMP"
if [ "$DRY_RUN" -eq 0 ]; then mkdir -p "$SNAPSHOT"; fi
MANIFEST="$SNAPSHOT/manifest.txt"

for db in $DATABASES; do
    src="$DATA_DIR/$db"
    if [ ! -f "$src" ]; then
        # Both files exist on any box that has booted once. A missing one means the wrong
        # --data-dir or a half-provisioned box; silently backing up one of two databases is the
        # kind of success that is only discovered during a restore.
        fail "$src not found -- is --data-dir right?"
    fi

    target="$SNAPSHOT/$db"
    if [ "$DRY_RUN" -eq 1 ]; then
        echo "  would snapshot $src -> $target and integrity-check the copy"
        continue
    fi

    # mode=ro: readers never block writers under WAL, and this can never modify the live file.
    sqlite3 "file:$src?mode=ro" "VACUUM INTO '$target'" \
        || fail "VACUUM INTO failed for $db (the live database is untouched)"

    check=$(sqlite3 "$target" 'PRAGMA integrity_check;' 2>&1) \
        || fail "integrity_check could not run against the $db copy"
    [ "$check" = "ok" ] || fail "the $db copy failed integrity_check: $check"

    size=$(du -h "$target" | cut -f1)
    echo "  $db -> $target ($size, integrity ok)"
    printf '%s : %s, integrity_check ok\n' "$db" "$size" >>"$MANIFEST"
done

if [ "$DRY_RUN" -eq 0 ]; then
    mv "$SNAPSHOT" "$FINAL"
    SNAPSHOT=""   # nothing half-written to clean up from here on
    echo "  snapshot complete: $FINAL"
fi

# ---- 2. Rolling mirrors of the blob trees ------------------------------------------------------
# Append-mostly and large (receipt images, recipe photos, synthesized audio), so one rolling mirror
# rather than a copy per night. keys/ is in here because losing the DataProtection keys logs every
# household out and invalidates outstanding reset links -- it is small and it matters.
for tree in $TREES; do
    src="$DATA_DIR/$tree"
    [ -d "$src" ] || { echo "  (no $tree/ yet -- skipping)"; continue; }

    dst="$DEST/files/$tree"
    if [ "$DRY_RUN" -eq 0 ]; then mkdir -p "$dst"; fi
    # ⚠️ --delete, so the destination is a dedicated per-tree directory THIS script creates and
    # nothing else. Trailing slashes on both sides: rsync's "contents of", not "the directory into".
    rsync -a --delete $RSYNC_DRY "$src/" "$dst/" \
        || fail "mirroring $tree failed"
    if [ "$DRY_RUN" -eq 1 ]; then echo "  would mirror $tree"; else echo "  mirrored $tree"; fi
done

# ---- 3. Retention ------------------------------------------------------------------------------
# Parse the stamp out of the folder NAME, strictly. `date -d` on a malformed stamp fails, and a
# folder that fails to parse is left alone -- the family script's TryParseExact rule: a stray
# directory must never be mis-deleted, and must never wedge the run so the real ones pile up.
cutoff=$(date -u -d "$KEEP_DAYS days ago" +%s)
for dir in "$DEST"/db-*; do
    [ -d "$dir" ] || continue
    name=$(basename "$dir")
    day=${name#db-}; day=${day%.incomplete}; day=${day%%-[0-9][0-9][0-9][0-9][0-9][0-9]}
    if ! when=$(date -u -d "$day" +%s 2>/dev/null); then
        echo "  (leaving $name alone -- its name is not a date stamp)"
        continue
    fi
    [ "$when" -lt "$cutoff" ] || continue
    if [ "$DRY_RUN" -eq 1 ]; then
        echo "  would remove $name (older than $KEEP_DAYS days)"
    else
        rm -rf "$dir"
        echo "  removed $name (older than $KEEP_DAYS days)"
    fi
done

# ---- 4. Offsite --------------------------------------------------------------------------------
# After retention, so the remote mirrors the pruned local state. --backup-dir is what makes a
# `sync` survivable: a remote file this run would delete or overwrite is MOVED into a dated archive
# instead. The archive is a SIBLING of the remote, never a child -- rclone refuses a backup-dir
# nested inside the sync destination.
if [ -n "$RCLONE_REMOTE" ]; then
    if [ "$DRY_RUN" -eq 1 ]; then
        echo "  would sync $DEST -> $RCLONE_REMOTE (archiving replaced files under ${RCLONE_REMOTE}-archive/$STAMP)"
    elif ! command -v rclone >/dev/null 2>&1; then
        # The LOCAL backup is intact, so this is not a failed backup -- but it is not offsite
        # either, and saying so is the whole point.
        fail "offsite sync skipped: rclone is not installed (the local backup in $DEST is intact)"
    else
        rclone sync "$DEST" "$RCLONE_REMOTE" \
            --backup-dir "${RCLONE_REMOTE}-archive/$STAMP" \
            || fail "offsite sync to $RCLONE_REMOTE failed (the local backup in $DEST is intact)"
        echo "  synced to $RCLONE_REMOTE"
    fi
fi

if [ "$DRY_RUN" -eq 1 ]; then
    echo "DRY RUN complete -- nothing was written, deleted or uploaded."
else
    offsite=$([ -n "$RCLONE_REMOTE" ] && echo "offsite $RCLONE_REMOTE" || echo "same-disk only")
    log_line "ok ($STAMP): databases snapshotted + verified, trees mirrored, kept ${KEEP_DAYS}d, $offsite"
    echo "Backup complete."
fi
