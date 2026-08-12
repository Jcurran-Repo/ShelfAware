# Publishes ShelfAware to the family box - THIS machine's ShelfAware-server folder, the
# one the "ShelfAware Server" scheduled task runs at boot and the tailnet + the
# family.shelfaware.net tunnel both front. Companion to deploy.ps1 (the droplet); no ssh
# here, just local folders, and the same stage-and-swap shape as install.sh:
#
#   publish (self-contained win-x64) -> stop the task -> back up the DBs + keys ->
#   rename the live folder aside as ShelfAware-server-prev -> lay the new build down
#   fresh -> MOVE app-data across (instant, same volume) and copy the site's
#   appsettings*.json over the repo defaults -> start the task -> probe it answers.
#
#   powershell -ExecutionPolicy Bypass -File deploy\publish-family.ps1
#
# Nothing is ever deleted from a live folder (an early dry run of a /MIR-based version
# of this script would have eaten an old ad-hoc backup sitting at the server root -
# that is why this is a swap, not a mirror). The one deletion is the PREVIOUS run's
# -prev folder, so: anything loose in the server folder that isn't app-data or
# appsettings*.json survives exactly one more publish inside -prev, then dies with it.
# Keep keepsakes in app-data\ (always carried forward) or outside the server folder.
#
# Site config (managed keys, quotas, locked registration) lives in the server's
# appsettings.json and is carried across every publish, per the runbook's rule.
# Rollback: stop the task, move app-data from the server folder back into -prev, swap
# the two folder names, start the task.
[CmdletBinding()]
param(
    [string]$ServerDir = "$env:USERPROFILE\ShelfAware-server",
    [string]$TaskName = 'ShelfAware Server',
    # Skip the are-you-sure prompt.
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot 'src\ShelfAware.Web'
$publishDir = Join-Path $project 'bin\publish\win-x64'
$prevDir    = "$ServerDir-prev"
$appData    = Join-Path $ServerDir 'app-data'
$serverExe  = Join-Path $ServerDir 'ShelfAware.Web.exe'

if (-not (Test-Path $ServerDir)) { throw "No server folder at $ServerDir." }
if (-not (Test-Path $appData)) { throw "No app-data under $ServerDir - wrong folder? Refusing to touch it." }
if ($null -eq (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
    throw "Scheduled task '$TaskName' not found."
}

if (-not $Yes) {
    Write-Host "This will stop '$TaskName', swap the new build into $ServerDir (app-data and appsettings*.json carried across), and start it again."
    Write-Host "Anything else loose in $ServerDir rides into $prevDir and is deleted on the NEXT publish - move keepsakes into app-data\ first."
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

Write-Host "Stopping '$TaskName'..."
Stop-ScheduledTask -TaskName $TaskName
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    # Match on Path, not name: the DEV server is also ShelfAware.Web (the documented gotcha).
    if ($null -eq (Get-Process ShelfAware.Web -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $serverExe })) { break }
    Start-Sleep -Seconds 1
}
if (Get-Process ShelfAware.Web -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $serverExe }) {
    throw 'The server process did not exit within 30s - not proceeding while its files are in use.'
}

# Pre-publish DB backup, taken with the app STOPPED so the -wal files are consistent.
# The next boot runs schema migrations (AdditiveSchema); this is migration insurance,
# not the nightly backup - receipts and the speech cache are untouched by a publish.
# It lives inside app-data, so it moves to the new folder with everything else.
$stamp = Get-Date -Format 'yyyy-MM-dd-HHmm'
$backup = Join-Path $appData "backup-$stamp-pre-publish"
New-Item -ItemType Directory -Path $backup | Out-Null
Copy-Item (Join-Path $appData 'shelfaware.db*') $backup
Copy-Item (Join-Path $appData 'auth.db*') $backup
if (Test-Path (Join-Path $appData 'keys')) { Copy-Item (Join-Path $appData 'keys') $backup -Recurse }
Write-Host "DBs + keys backed up to $backup"

# The swap. Renames and the app-data move are instant same-volume operations, so the
# stopped window is dominated by the file copy of the new build, not the data.
if (Test-Path $prevDir) { Remove-Item -Recurse -Force $prevDir }
Rename-Item $ServerDir $prevDir
New-Item -ItemType Directory -Path $ServerDir | Out-Null
robocopy $publishDir $ServerDir /E /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy (install) failed with code $LASTEXITCODE" }
# The repo's dev-environment config has no business on the family box.
if (Test-Path (Join-Path $ServerDir 'appsettings.Development.json')) {
    Remove-Item (Join-Path $ServerDir 'appsettings.Development.json')
}
Move-Item (Join-Path $prevDir 'app-data') $appData
# Site config over the repo defaults the publish just laid down.
Copy-Item (Join-Path $prevDir 'appsettings*.json') $ServerDir -Force

Write-Host "Starting '$TaskName'..."
Start-ScheduledTask -TaskName $TaskName
Write-Host 'Waiting for the app to answer on http://127.0.0.1:5179 ...'
$deadline = (Get-Date).AddSeconds(90)
$alive = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    try {
        $r = Invoke-WebRequest -Uri 'http://127.0.0.1:5179/Account/Login' -UseBasicParsing -TimeoutSec 5
        if ($r.StatusCode -eq 200) { $alive = $true; break }
    } catch {
        # Not up yet (connection refused while Kestrel boots / migrations run) - keep polling.
    }
}
if (-not $alive) {
    Write-Host 'error: the server did not answer within 90s. Check Task Scheduler history.' -ForegroundColor Red
    Write-Host "Rollback: stop the task, move app-data from $ServerDir back into $prevDir, swap the folder names, start the task. DB backup: $backup"
    exit 1
}
Write-Host 'Family server is up. Give it a click-around (tailnet or family.shelfaware.net) before walking away.'
