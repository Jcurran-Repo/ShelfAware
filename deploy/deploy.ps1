# Publishes ShelfAware for linux-x64 and ships it to the droplet in one command:
#
#   powershell -ExecutionPolicy Bypass -File deploy\deploy.ps1 -TargetHost root@<droplet-ip>
#
# Publish (self-contained, so the droplet needs no .NET install) -> tar -> scp -> run
# deploy/install.sh remotely, which swaps the build in and restarts the service.
# First-time box setup is docs/deploy-droplet.md; this script is every deploy after that.
[CmdletBinding()]
param(
    # "root@203.0.113.10", or an alias from ~/.ssh/config.
    [Parameter(Mandatory = $true)]
    [string]$TargetHost
)

$ErrorActionPreference = 'Stop'

foreach ($tool in 'dotnet', 'tar', 'scp', 'ssh') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "'$tool' is not on PATH. dotnet needs the .NET 10 SDK; tar/scp/ssh ship with Windows 10+."
    }
}

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot 'src\ShelfAware.Web'
$publishDir = Join-Path $project 'bin\publish\linux-x64'
$tarball    = Join-Path $env:TEMP 'shelfaware.tar.gz'

Write-Host 'Publishing (self-contained linux-x64)...'
dotnet publish $project -c Release -r linux-x64 --self-contained -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE). If it's a file lock (MSB3027), stop the dev server first." }
if (-not (Test-Path (Join-Path $publishDir 'ShelfAware.Web'))) {
    throw "Publish output is missing the ShelfAware.Web apphost -- wrong runtime identifier?"
}

Write-Host 'Packing...'
if (Test-Path $tarball) { Remove-Item $tarball }
tar -czf $tarball -C $publishDir .
if ($LASTEXITCODE -ne 0) { throw "tar failed ($LASTEXITCODE)" }
$mb = [math]::Round((Get-Item $tarball).Length / 1MB, 1)

# install.sh must reach the droplet with LF endings and no BOM whatever this checkout did
# to it; bash refuses CRLF scripts with errors that don't name the cause. The script is
# pure ASCII, so PowerShell 5.1's encoding guess can't mangle the round-trip.
$installLf = Join-Path $env:TEMP 'shelfaware-install.sh'
$body = (Get-Content -Raw (Join-Path $PSScriptRoot 'install.sh')) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($installLf, $body, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Uploading $mb MB to $TargetHost..."
scp $tarball "${TargetHost}:/tmp/shelfaware.tar.gz"
if ($LASTEXITCODE -ne 0) { throw "scp (tarball) failed ($LASTEXITCODE)" }
scp $installLf "${TargetHost}:/tmp/shelfaware-install.sh"
if ($LASTEXITCODE -ne 0) { throw "scp (install script) failed ($LASTEXITCODE)" }

Write-Host 'Installing...'
ssh $TargetHost 'bash /tmp/shelfaware-install.sh'
if ($LASTEXITCODE -ne 0) { throw "Remote install failed ($LASTEXITCODE) -- its output above says where it stopped." }

Write-Host 'Done.'
