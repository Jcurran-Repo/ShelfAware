# Installs (or updates) the family box's nightly backup: publishes the SqliteSnapshot tool,
# copies backup-family.ps1 beside it in a stable folder OUTSIDE the repo, and registers a daily
# scheduled task pointing at that COPY. The copy is the point: the 3am task must keep working
# whatever branch the repo's working tree is on (deploy\backup-family.ps1 does not exist on
# branches that predate it, and a checkout mid-feature must not silently kill backups).
#
# Safe to re-run: republishes the tool, recopies the script, replaces the task (-Force). Run it
# again after any change to backup-family.ps1 so the installed copy picks it up.
#
# The task runs as the current user with LogonType S4U - no stored password, and it runs whether
# or not anyone is signed in (the family box serves headless after a reboot; an interactive-only
# trigger would quietly skip backups exactly when the machine has been up unattended longest).
# Registering an S4U task usually needs an ELEVATED PowerShell; everything before that step works
# non-elevated, and a denied registration prints the one command to run elevated.
[CmdletBinding()]
param(
    [string]$ToolDir = "$env:USERPROFILE\ShelfAware-backup",
    [string]$TaskName = 'ShelfAware Nightly Backup',
    [string]$At = '03:30',
    # Passed through to backup-family.ps1 and BAKED INTO the task action, so what the task will do
    # is explicit in Task Scheduler rather than re-derived from the environment every night.
    [string]$ServerDir = "$env:USERPROFILE\ShelfAware-server",
    [string]$Dest = "$env:USERPROFILE\ShelfAware-backups",
    # rclone remote path for the offsite mirror (e.g. gdrive:ShelfAware-backups). Set it AFTER
    # installing rclone and running `rclone config` once; re-run this installer to bake it into
    # the task. '' = local-only backups (and backup-family.ps1's header says exactly what that
    # does and doesn't protect against).
    [string]$RcloneRemote = '',
    # Mirror backup-family.ps1's own [ValidateRange(1,3650)]: reject a bad value HERE, at install,
    # rather than bake it into the task and have the nightly run fail at param-binding - which is
    # BEFORE its try/catch, so it would write no backup-log.txt line and fail only where Task
    # Scheduler shows it. Validating at the front door keeps a fat-fingered value from becoming a
    # silently log-less failing backup.
    [ValidateRange(1, 3650)]
    [int]$KeepDays = 14
)

$ErrorActionPreference = 'Stop'

$csproj = Join-Path $PSScriptRoot 'sqlite-snapshot\SqliteSnapshot.csproj'
$script = Join-Path $PSScriptRoot 'backup-family.ps1'
if (-not (Test-Path $csproj)) { throw "Run this from the repo: $csproj not found." }
if (-not (Test-Path $script)) { throw "Run this from the repo: $script not found." }

Write-Host "Publishing SqliteSnapshot to $ToolDir ..."
dotnet publish $csproj -c Release -o $ToolDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
Copy-Item $script -Destination $ToolDir -Force
Write-Host "Copied backup-family.ps1 beside it."

$installedScript = Join-Path $ToolDir 'backup-family.ps1'
$argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$installedScript`" -ServerDir `"$ServerDir`" -Dest `"$Dest`" -KeepDays $KeepDays"
if ($RcloneRemote -ne '') { $argLine += " -RcloneRemote `"$RcloneRemote`"" }
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argLine
$trigger = New-ScheduledTaskTrigger -Daily -At $At
# StartWhenAvailable: if the box was off or asleep at the trigger time, run on wake instead of
# skipping the night entirely.
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2)
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType S4U

try {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Settings $settings -Principal $principal -Force | Out-Null
    Write-Host "Task '$TaskName' registered: daily at $At -> $Dest"
    Write-Host "First run happens tonight; run it now with:  powershell -File `"$installedScript`""
}
catch {
    Write-Warning "Couldn't register the task ($($_.Exception.Message))."
    Write-Warning "The tool and script ARE installed at $ToolDir. From an ELEVATED PowerShell, re-run:"
    Write-Warning "  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit 1
}
