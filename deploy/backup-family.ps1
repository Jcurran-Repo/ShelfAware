# Nightly backup for the family box - THIS machine's ShelfAware-server\app-data, the live
# household data behind family.shelfaware.net. Companion to publish-family.ps1 (which takes a
# one-off backup with the app STOPPED before a publish); this one runs with the app UP, on a
# schedule, so the newest backup is never older than a day. It is also the dry run for the
# future pay-to-play box's backup provision (there: cron + sqlite3 .backup, same shape).
#
# How a live database is snapshotted safely: deploy\sqlite-snapshot (a tiny console tool,
# published on first use) runs VACUUM INTO from a READ-ONLY connection. Under WAL, readers never
# block the app's writers, and the copy is taken inside one read transaction - a consistent,
# self-contained .db with no -wal/-shm sidecars. A plain file copy of a live database is exactly
# the torn-backup mistake this tool exists to avoid. The tool then runs PRAGMA integrity_check
# against the COPY, because a backup nobody has ever opened is a hope, not a backup.
#
# Layout under -Dest:
#   db-YYYY-MM-dd-HHmmss\shelfaware.db + auth.db + manifest.txt   point-in-time DB snapshots,
#                                                                 kept for -KeepDays
#   files\receipts, files\recipe-images, files\keys, files\tts-cache
#                                                                 a ROLLING robocopy mirror of the
#                                                                 blob trees (append-mostly; nightly
#                                                                 per-stamp copies would balloon)
#   backup-log.txt                                                one line per run, success or fail
#
# /MIR discipline (the publish-family.ps1 lesson: a /MIR dry run once nearly ate an unrelated
# backup): mirrors target ONLY dedicated subfolders this script creates under -Dest\files. Never
# point -Dest at a folder that holds anything else. -DryRun rehearses: robocopy runs with /L
# (list-only) and retention deletions are printed, not performed.
#
# Retention is keyed on the FOLDER NAME's date stamp, not filesystem dates - deterministic, and
# testable by creating a fake old-named folder. It only ever deletes db-* stamp folders under
# -Dest; the files\ mirror is never touched by retention.
#
# Offsite: -Dest is a LOCAL staging folder on purpose, and -RcloneRemote is how backups leave the
# machine. A per-user sync client (OneDrive, Google Drive for desktop) only syncs while its owner
# is signed in - and this box serves headless after a reboot, which is the exact window a backup
# matters most. So the offsite hop runs INSIDE this task: when -RcloneRemote names a configured
# rclone remote path (e.g. gdrive:ShelfAware-backups), the run ends with `rclone sync` mirroring
# -Dest to it - provider-agnostic (Google Drive, B2, S3, whatever rclone speaks), no sign-in
# needed, and retention applies once locally then mirrors out. The sync runs with --backup-dir, so
# a file it would delete or overwrite offsite is MOVED into a dated archive (<remote>-archive/<stamp>)
# rather than erased - without it, a bad local night (mass-delete / corruption the /MIR faithfully
# mirrored) would propagate offsite and leave the blob trees, which have no per-stamp local history,
# unrecoverable anywhere. The archive grows and is the operator's to prune. One-time setup: install
# rclone, run `rclone config` (interactive OAuth - the operator does this, once), then re-run
# install-family-backup.ps1 with -RcloneRemote so the task carries it. Without -RcloneRemote the
# backup is same-disk only, and that is stated here rather than implied to be more than it is.
#
# Restore: copy the newest db-* pair back into ShelfAware-server\app-data (app stopped), restore
# the files\ trees beside them (from -Dest, or `rclone copy` them down first). Caveat, stated
# because it will matter on the day: keys\ is DPAPI-protected, so on a REBUILT machine or
# different user those DataProtection keys will not decrypt - everyone signs in again.
# Same-machine restores decrypt fine.
#
# Needs no elevation and does not touch the running server (reads only). Exits nonzero on any
# failure so Task Scheduler's Last Run Result shows red. Scheduled by install-family-backup.ps1,
# which copies this script + the tool to a stable folder OUTSIDE the repo - the 3am task must not
# depend on whichever branch the working tree happens to be on.
[CmdletBinding()]
param(
    [string]$ServerDir = "$env:USERPROFILE\ShelfAware-server",
    [string]$Dest = "$env:USERPROFILE\ShelfAware-backups",
    # An rclone remote path to mirror -Dest to after a successful run ('' = no offsite step).
    [string]$RcloneRemote = '',
    [ValidateRange(1, 3650)]
    [int]$KeepDays = 14,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$appData = Join-Path $ServerDir 'app-data'
$pantryDb = Join-Path $appData 'shelfaware.db'
$authDb = Join-Path $appData 'auth.db'
if (-not (Test-Path $pantryDb)) { throw "No pantry database at $pantryDb - is -ServerDir right?" }
if (-not (Test-Path $authDb)) { throw "No auth database at $authDb - is -ServerDir right?" }

# ---- Locate (or build) the snapshot tool ------------------------------------------------------
# Beside this script first: that is the installed layout (install-family-backup.ps1 publishes the
# tool and copies this script into one stable folder). Fall back to a local cache, building it
# from the repo source when running straight from a checkout.
$exe = Join-Path $PSScriptRoot 'SqliteSnapshot.exe'
if (-not (Test-Path $exe)) {
    $cacheDir = Join-Path $env:LOCALAPPDATA 'ShelfAware\backup-tool'
    $exe = Join-Path $cacheDir 'SqliteSnapshot.exe'
    if (-not (Test-Path $exe)) {
        # (The folder is named sqlite-snapshot, not backup: .gitignore's VS-template Backup*/
        # pattern is case-insensitive here and would swallow a deploy\backup\ wholesale.)
        $csproj = Join-Path $PSScriptRoot 'sqlite-snapshot\SqliteSnapshot.csproj'
        if (-not (Test-Path $csproj)) {
            throw "SqliteSnapshot.exe not found beside this script or in $cacheDir, and no source at $csproj. Run deploy\install-family-backup.ps1 from the repo first."
        }
        Write-Host "Publishing the snapshot tool to $cacheDir ..."
        dotnet publish $csproj -c Release -o $cacheDir --nologo -v quiet
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { throw "Publishing SqliteSnapshot failed (exit $LASTEXITCODE)." }
    }
}

# ---- One run, logged win or lose ---------------------------------------------------------------
$stamp = Get-Date -Format 'yyyy-MM-dd-HHmmss'
$logFile = Join-Path $Dest 'backup-log.txt'
$failure = $null

try {
    New-Item -ItemType Directory -Force -Path $Dest | Out-Null

    # -- Database snapshots into a fresh stamp folder --
    $snapDir = Join-Path $Dest "db-$stamp"
    if ($DryRun) {
        Write-Host "[dry run] would snapshot shelfaware.db + auth.db into $snapDir"
    }
    else {
        New-Item -ItemType Directory -Path $snapDir | Out-Null
        $manifest = @("ShelfAware family-box backup $stamp", "source: $appData")
        foreach ($db in @($pantryDb, $authDb)) {
            $name = Split-Path $db -Leaf
            $target = Join-Path $snapDir $name
            & $exe $db $target
            if ($LASTEXITCODE -ne 0) { throw "Snapshot of $name failed (SqliteSnapshot exit $LASTEXITCODE)." }
            $mb = [math]::Round((Get-Item $target).Length / 1MB, 2)
            $manifest += "$name : $mb MB, integrity_check ok"
            Write-Host "  $name -> $target ($mb MB, integrity ok)"
        }
        Set-Content -Path (Join-Path $snapDir 'manifest.txt') -Value $manifest -Encoding utf8
    }

    # -- Rolling mirror of the blob trees --
    # recipe-images may not exist yet (created on the first photo); mirror whatever is present.
    $filesRoot = Join-Path $Dest 'files'
    foreach ($tree in @('receipts', 'recipe-images', 'keys', 'tts-cache')) {
        $src = Join-Path $appData $tree
        if (-not (Test-Path $src)) { continue }
        $dst = Join-Path $filesRoot $tree
        $switches = @('/MIR', '/R:2', '/W:5', '/NP', '/NFL', '/NDL', '/NJH', '/NJS')
        if ($DryRun) { $switches += '/L' }
        robocopy $src $dst @switches | Out-Null
        # Robocopy: 0-7 are success flavors (copied / extra / mismatched combinations); 8+ is failure.
        if ($LASTEXITCODE -ge 8) { throw "Mirroring $tree failed (robocopy exit $LASTEXITCODE)." }
        if ($DryRun) { Write-Host "[dry run] would mirror $tree (robocopy /L exit $LASTEXITCODE)" }
        else { Write-Host "  mirrored $tree (robocopy exit $LASTEXITCODE)" }
    }

    # -- Retention: prune old db-* stamp folders by the date IN THEIR NAME --
    # TryParseExact, not a [datetime] cast: a stray folder whose name fits the shape but names an
    # impossible day (db-2026-13-40-000000) would make the cast THROW, and under -ErrorActionPreference
    # Stop that aborts the whole run and skips retention entirely - a single planted name jams pruning
    # forever. TryParseExact fails that folder to $false (left alone, never mis-deleted) and the run
    # carries on. Invariant culture so the yyyy-MM-dd parse is locale-independent.
    $cutoff = (Get-Date).Date.AddDays(-$KeepDays)
    $old = Get-ChildItem $Dest -Directory -Filter 'db-*' | Where-Object {
        $d = [datetime]::MinValue
        ($_.Name -match '^db-(\d{4}-\d{2}-\d{2})-\d{6}$') -and
        [datetime]::TryParseExact($Matches[1], 'yyyy-MM-dd', [cultureinfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::None, [ref]$d) -and
        ($d -lt $cutoff)
    }
    foreach ($dir in $old) {
        if ($DryRun) { Write-Host "[dry run] would remove old snapshot $($dir.Name)" }
        else {
            Remove-Item $dir.FullName -Recurse -Force
            Write-Host "  removed old snapshot $($dir.Name)"
        }
    }

    # -- Offsite: mirror the staging folder to the rclone remote, with versioned protection --
    # After retention, so the remote mirrors the pruned local state. The danger a plain `rclone
    # sync` would carry: the blob trees (receipts, recipe-images, keys, tts-cache) have ONLY this
    # rolling mirror - no per-stamp local history like the DBs get - so a bad LOCAL night (a
    # mass-delete, an app bug, or ransomware that the /MIR above faithfully mirrored into $Dest)
    # would propagate the deletion offsite and leave NO recoverable copy of those blobs anywhere.
    # --backup-dir closes that: instead of DELETING a remote file that has vanished locally, rclone
    # MOVES it into a dated archive folder, so one bad night can never erase good offsite blobs -
    # recovery is from the archive. This is the durability model of the pay-for box too (the family
    # box is its dry run), which is why it's fixed here rather than accepted.
    #   The archive is a SIBLING of the remote (<remote>-archive/<stamp>), never a child - rclone
    # refuses a backup-dir nested inside the sync destination, and "<remote>-archive" shares no
    # path component with "<remote>" so the two provably never overlap.
    #   Cost, stated so it's a conscious choice: the archive grows over time (every superseded or
    # deleted file, dated) and has no auto-pruning - it is the operator's to trim, deliberately
    # manual on this dry run. A failure here still leaves the LOCAL backup intact and complete; the
    # thrown message says so, so the log line names which half failed.
    if ($RcloneRemote -ne '') {
        $rclone = Get-Command rclone -ErrorAction SilentlyContinue
        if (-not $rclone) {
            throw "Offsite sync skipped: rclone is not installed (the LOCAL backup in $Dest is intact). Install rclone and run 'rclone config', or drop -RcloneRemote."
        }
        $archive = "$RcloneRemote-archive/$stamp"
        $rcArgs = @('sync', $Dest, $RcloneRemote, '--backup-dir', $archive, '-q')
        if ($DryRun) { $rcArgs += '--dry-run' }
        & $rclone.Source @rcArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Offsite sync to $RcloneRemote failed (rclone exit $LASTEXITCODE) - the LOCAL backup in $Dest is intact."
        }
        if ($DryRun) { Write-Host "[dry run] would sync $Dest -> $RcloneRemote (superseded files -> $archive)" }
        else { Write-Host "  synced offsite -> $RcloneRemote (superseded -> $archive)" }
    }
}
catch {
    $failure = $_.Exception.Message
}

# The log line is written on BOTH outcomes - a backup that fails silently for a month is the
# scenario this file exists to make visible. Plain ASCII, one line per run.
$status = 'ok'
if ($failure) { $status = "FAILED: $failure" }
if ($DryRun) { $status = "dry-run $status" }
try { Add-Content -Path $logFile -Value "$stamp $status" -Encoding utf8 }
catch [System.IO.IOException] { Write-Warning "Couldn't write the backup log line ($stamp $status): $($_.Exception.Message)" }
catch [System.UnauthorizedAccessException] { Write-Warning "Couldn't write the backup log line ($stamp $status): $($_.Exception.Message)" }

if ($failure) {
    Write-Error "Backup failed: $failure"
    exit 1
}
Write-Host "Backup complete: $Dest (snapshots kept $KeepDays days, log: backup-log.txt)"
