# Publishes ShelfAware to the family box - THIS machine's ShelfAware-server folder, the
# one the "ShelfAware Server" scheduled task runs at boot and the tailnet + the
# family.shelfaware.net tunnel both front. Companion to deploy.ps1 (the droplet); no ssh
# here, just local folders, and the same stage-and-swap shape as install.sh:
#
#   publish (self-contained win-x64) -> disable + stop the task -> back up the DBs +
#   keys -> sweep loose keepsakes to the attic -> rename the live folder aside as
#   ShelfAware-server-prev -> MOVE app-data into the fresh folder FIRST (instant,
#   same volume - the household's data is never in limbo while the slow binary copy
#   runs) -> lay the new build down -> carry the site's appsettings*.json across ->
#   re-enable + start the task -> probe until it answers.
#
# Nothing is ever deleted from a live folder (an early dry run of a /MIR-based version
# of this script would have eaten an old ad-hoc backup sitting at the server root -
# that is why this is a swap, not a mirror). Loose keepsakes at the server root are
# swept into ShelfAware-server-attic: moved, never deleted. The one deletion in this
# script - the PREVIOUS run's -prev folder - first REFUSES if that folder still holds
# an app-data (the fingerprint of an earlier mid-swap failure), so a re-run after a
# botched publish can never destroy the real database. Both halves of that defense
# (data-first ordering + the refusal) came out of this script's independent review.
#
# Site config (managed keys, quotas, locked registration, the Email section) lives in
# the server's appsettings.json and is carried across every publish, per the runbook's
# rule. Rollback: stop the task, move app-data back into -prev, swap the folder names,
# re-enable, start.
[CmdletBinding()]
param(
    [string]$ServerDir = "$env:USERPROFILE\ShelfAware-server",
    [string]$TaskName = 'ShelfAware Server',
    # The family box's port (verified against the live task + tailscale serve target,
    # 2026-08-12). The post-publish poll false-fails a GOOD deploy if this is wrong.
    [int]$Port = 5179,
    # Skip the are-you-sure prompt.
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot 'src\ShelfAware.Web'
$publishDir = Join-Path $project 'bin\publish\win-x64'
$prevDir    = "$ServerDir-prev"
$atticDir   = "$ServerDir-attic"
$appData    = Join-Path $ServerDir 'app-data'
$serverExe  = Join-Path $ServerDir 'ShelfAware.Web.exe'

if (-not (Test-Path $ServerDir)) { throw "No server folder at $ServerDir." }
if (-not (Test-Path $appData)) { throw "No app-data under $ServerDir - wrong folder? Refusing to touch it." }
if ($null -eq (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
    throw "Scheduled task '$TaskName' not found."
}
# The fingerprint of an earlier publish that failed mid-swap: -prev still holding an
# app-data. Deleting it below would destroy the real database (the app auto-creates an
# EMPTY app-data on boot, which lets the Test-Path precondition above pass while the
# genuine data sits in -prev). Refuse until a human sorts out which folder is truth.
if ((Test-Path $prevDir) -and (Test-Path (Join-Path $prevDir 'app-data'))) {
    throw "$prevDir still holds an app-data - a previous publish failed mid-swap. Recover it (move that app-data back into $ServerDir, or rename $prevDir back over it) before re-running."
}

if (-not $Yes) {
    Write-Host "This will stop '$TaskName', swap the new build into $ServerDir (app-data and appsettings*.json carried across), and start it again."
    Write-Host "Anything else loose in $ServerDir is moved to $atticDir - never deleted."
    $answer = Read-Host 'Type yes to continue'
    if ($answer -ne 'yes') { Write-Host 'Aborted.'; exit 1 }
}

Write-Host 'Publishing (self-contained win-x64)...'
# dotnet publish -o overlays; orphans from removed packages/assets would ship forever.
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish $project -c Release -r win-x64 --self-contained -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE). If it's a file lock (MSB3027), stop the dev server first." }
if (-not (Test-Path (Join-Path $publishDir 'ShelfAware.Web.exe'))) {
    throw 'Publish output is missing ShelfAware.Web.exe - wrong runtime identifier?'
}

Write-Host "Disabling + stopping '$TaskName'..."
# Disable BEFORE stopping: a merely-stopped task can be relaunched by any trigger
# mid-swap (boot is not guaranteed to stay the only one), landing the app on a
# half-swapped folder. Disabled, nothing relaunches it until we say so.
Disable-ScheduledTask -TaskName $TaskName | Out-Null
Stop-ScheduledTask -TaskName $TaskName
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    # Match on Path, not name: the DEV server is also ShelfAware.Web (the documented gotcha).
    if ($null -eq (Get-Process ShelfAware.Web -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $serverExe })) { break }
    Start-Sleep -Seconds 1
}
if (Get-Process ShelfAware.Web -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $serverExe }) {
    Enable-ScheduledTask -TaskName $TaskName | Out-Null
    throw 'The server process did not exit within 30s - not proceeding while its files are in use. (Task re-enabled.)'
}

# Pre-publish DB backup, taken with the app STOPPED so the -wal files are consistent.
# The next boot runs schema migrations (AdditiveSchema); this is migration insurance,
# not the nightly backup - receipts and the speech cache are untouched by a publish.
# It lives inside app-data, so it moves to the new folder with everything else.
# Get-ChildItem|Copy-Item rather than a bare wildcard so an empty match is a no-op,
# not a throw (a first-ever publish has no DBs yet).
$stamp = Get-Date -Format 'yyyy-MM-dd-HHmmss'
$backup = Join-Path $appData "backup-$stamp-pre-publish"
New-Item -ItemType Directory -Path $backup -Force | Out-Null
Get-ChildItem $appData -Filter 'shelfaware.db*' | Copy-Item -Destination $backup
Get-ChildItem $appData -Filter 'auth.db*' | Copy-Item -Destination $backup
if (Test-Path (Join-Path $appData 'keys')) { Copy-Item (Join-Path $appData 'keys') $backup -Recurse }
Write-Host "DBs + keys backed up to $backup"

# Sweep keepsakes to the attic BEFORE the swap: any top-level item the new publish
# doesn't account for (and that isn't app-data or appsettings*.json) is moved - never
# deleted - to the attic. This is what keeps -prev pure publish output, which is what
# makes deleting it below safe. The rare misfile (a DLL the new build stopped
# shipping gets attic'd instead of riding into -prev) is recoverable from the attic
# by hand, which is the safe direction.
$publishNames = (Get-ChildItem $publishDir).Name
foreach ($item in Get-ChildItem $ServerDir) {
    if ($item.Name -eq 'app-data') { continue }
    if ($item.Name -like 'appsettings*.json') { continue }
    # 'runtimes' is publish output of the older portable style (the new self-contained
    # publish flattens it away). It belongs with the old build in -prev so a rollback
    # still has its native libraries - it is not a keepsake.
    if ($item.Name -eq 'runtimes') { continue }
    if ($publishNames -contains $item.Name) { continue }
    if (-not (Test-Path $atticDir)) { New-Item -ItemType Directory -Path $atticDir | Out-Null }
    $dest = Join-Path $atticDir $item.Name
    if (Test-Path $dest) { $dest = Join-Path $atticDir "$($item.Name)-$stamp" }
    Move-Item $item.FullName $dest
    Write-Host "attic: $($item.Name)"
}

# The swap. Data moves FIRST: every operation between the rename and the app-data move
# is an instant same-volume op, so there is no meaningful window in which the live
# folder exists without the household's data - the compound loss chain this script's
# review found (fail mid-copy -> reboot -> the app auto-creates an EMPTY app-data ->
# a re-run deletes -prev with the real data inside) needs that window to start.
if (Test-Path $prevDir) { Remove-Item -Recurse -Force $prevDir }
try {
    Rename-Item $ServerDir $prevDir
    New-Item -ItemType Directory -Path $ServerDir | Out-Null
    Move-Item (Join-Path $prevDir 'app-data') $appData
    robocopy $publishDir $ServerDir /E /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy (install) failed with code $LASTEXITCODE" }
    # Site config over the repo defaults FIRST, then strip dev config - this order so a
    # stray Development file in -prev can't be re-introduced after the strip.
    Copy-Item (Join-Path $prevDir 'appsettings*.json') $ServerDir -Force
    if (Test-Path (Join-Path $ServerDir 'appsettings.Development.json')) {
        Remove-Item (Join-Path $ServerDir 'appsettings.Development.json')
    }
}
catch {
    Write-Host 'error: the swap failed partway. CURRENT STATE:' -ForegroundColor Red
    if (Test-Path $appData) {
        Write-Host "  - app-data (the household's data) IS in $ServerDir - it is safe."
    } elseif (Test-Path (Join-Path $prevDir 'app-data')) {
        Write-Host "  - app-data (the household's data) is still in $prevDir - safe there; do NOT delete that folder. (A re-run will refuse until it's recovered - deliberate.)"
    }
    Write-Host "  - old binaries: $prevDir / new (possibly partial) binaries: $ServerDir"
    Write-Host "  - recover: pick the binaries to keep, make sure app-data is inside $ServerDir, then re-enable and start the task."
    Write-Host "  - the task is currently DISABLED (deliberate): nothing will boot onto a half-swapped folder, and the site stays down until you recover."
    throw
}

Write-Host "Starting '$TaskName'..."
Enable-ScheduledTask -TaskName $TaskName | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Host "Waiting for the app to answer on http://127.0.0.1:$Port ..."
$deadline = (Get-Date).AddSeconds(90)
$alive = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/Account/Login" -UseBasicParsing -TimeoutSec 5
        if ($r.StatusCode -eq 200) { $alive = $true; break }
    } catch {
        # Not up yet (connection refused while Kestrel boots / migrations run) - keep polling.
    }
}
if (-not $alive) {
    Write-Host "error: the server did not answer on port $Port within 90s. Check Task Scheduler history - and check the port param before assuming the deploy failed." -ForegroundColor Red
    Write-Host "Rollback: stop + disable the task, move app-data from $ServerDir back into $prevDir, swap the folder names, re-enable, start. DB backup: $backup"
    exit 1
}
Write-Host 'Family server is up. Give it a click-around (tailnet or family.shelfaware.net) before walking away.'
