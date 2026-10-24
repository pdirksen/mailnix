<#
.SYNOPSIS
    Builds mailnix as a standalone Windows app: one self-contained executable (no .NET installation needed)
    with the web UI next to it.

.DESCRIPTION
    1. Frontend: npm ci, type-check + build, license gate, third-party notices.
    2. Backend: self-contained single-file publish for the given runtime, with the SPA in wwwroot.
    3. Output: <Output>\mailnix.exe, wwwroot\, appsettings.json, LICENSE, README.txt (and a .zip with -Zip).
       A data\ folder already in <Output> is kept on rebuilds and never put into the .zip.

    The app stores its database and encryption keys in data\ next to mailnix.exe and listens on
    http://localhost:5000 unless started with --urls (see README.txt in the output).

.EXAMPLE
    .\scripts\build-windows.ps1
.EXAMPLE
    .\scripts\build-windows.ps1 -Runtime win-arm64 -Zip
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    # Defaults to artifacts\windows\<runtime> in the repository.
    [string] $Output,

    # Also create <Output>.zip.
    [switch] $Zip,

    # Skip the frontend license gate (e.g. offline experiments). Release builds should not use this.
    [switch] $SkipLicenseCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$frontend = Join-Path $root 'frontend'
$backend = Join-Path $root 'backend'
$api = Join-Path $backend 'src\Mailnix.Api'
if (-not $Output) { $Output = Join-Path $root "artifacts\windows\$Runtime" }
$Output = [System.IO.Path]::GetFullPath($Output)

function Invoke-Step([string] $Title, [scriptblock] $Action) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Title failed (exit code $LASTEXITCODE)." }
}

foreach ($tool in 'dotnet', 'npm') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "'$tool' was not found on PATH." }
}

# --- Frontend ----------------------------------------------------------------------------------------
Push-Location $frontend
try {
    Invoke-Step 'Installing frontend packages' { npm ci --no-audit --no-fund }
    Invoke-Step 'Building the frontend' { npm run build }
    if (-not $SkipLicenseCheck) {
        Invoke-Step 'Checking frontend licenses' { npm run license:check }
    }
    Invoke-Step 'Writing third-party notices' { npm run license:notices }
}
finally {
    Pop-Location
}

# The API serves the SPA from its wwwroot (git-ignored build output).
$wwwroot = Join-Path $api 'wwwroot'
if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
Copy-Item (Join-Path $frontend 'dist') $wwwroot -Recurse

# --- Backend -----------------------------------------------------------------------------------------
if (Test-Path $Output) {
    $running = Get-Process mailnix -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $Output 'mailnix.exe') }
    if ($running) { throw "mailnix.exe is running from $Output (process $($running.Id -join ', ')). Stop it first." }
    # Replace the previous build, but keep its data folder (database, keys): it may be a local install in use.
    Get-ChildItem $Output -Force | Where-Object Name -ne 'data' | Remove-Item -Recurse -Force
}
Invoke-Step "Publishing the backend ($Runtime, self-contained)" {
    dotnet publish (Join-Path $api 'Mailnix.Api.csproj') `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        --output $Output `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:OpenApiGenerateDocuments=false `
        -p:IsTransformWebConfigDisabled=true `
        -p:MailnixRuntimeLockFiles=true
}

Rename-Item (Join-Path $Output 'Mailnix.Api.exe') 'mailnix.exe'
# Development settings are not part of a release.
Get-ChildItem $Output -Filter 'appsettings.Development.json' | Remove-Item
Copy-Item (Join-Path $root 'LICENSE') $Output

@"
mailnix $Runtime
================

Start mailnix.exe and open http://localhost:5000 in your browser.
The first account you register becomes the administrator.

Data (SQLite database, encryption keys for stored mailbox passwords) is kept in the
'data' folder next to mailnix.exe. Back it up, and keep it when updating: replace
everything else with the new version. The keys are encrypted for the Windows account
that runs mailnix: a backup restored on another computer or account needs the mailbox
passwords entered again. To move installations, use a master key instead: run
    mailnix.exe --generate-master-key
keep the output safe (outside the data folder) and start mailnix with it, e.g.
    set Mailnix__DataProtection__MasterKey=<key>

Other address or port:
    mailnix.exe --urls http://localhost:8080
Reachable from other computers (put a TLS reverse proxy in front for anything public):
    mailnix.exe --urls http://0.0.0.0:8080

Settings can be changed in appsettings.json or with environment variables, e.g.
    set Mailnix__Auth__AllowRegistration=false
All settings are listed in the project README, section "Configuration".

License: MIT (LICENSE). Third-party notices: wwwroot\THIRD-PARTY-NOTICES.md.
"@ | Set-Content (Join-Path $Output 'README.txt') -Encoding utf8

if ($Zip) {
    $archive = "$Output.zip"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    # Never ship a local data folder: it holds the database and the keys for stored mailbox passwords.
    Compress-Archive -Path (Get-ChildItem $Output -Force | Where-Object Name -ne 'data').FullName -DestinationPath $archive
    Write-Host "Archive: $archive"
}

$size = (Get-ChildItem $Output -Recurse -File | Where-Object FullName -notlike (Join-Path $Output 'data\*') | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Done: {0} ({1:N0} MB)" -f $Output, $size) -ForegroundColor Green
