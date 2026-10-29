<#
.SYNOPSIS
    Uploads a release built by release.ps1 to the download server over FTPS and checks it on the web server.

.DESCRIPTION
    Remote layout below -FtpUrl (served at -WebUrl):
        <version>/    the release folder: zip/tar.gz builds, Windows installers and SHA256SUMS
        updates/      the Velopack update feed (release.ps1 -UpdateUrl <WebUrl>/updates)

    1. Checks the release folder against its SHA256SUMS.
    2. Signs in once and lists what is on the server. A failed sign-in stops at once; it is never retried.
    3. Uploads the release folder; SHA256SUMS goes last, so a folder without it is an unfinished upload. A version
       whose folder on the server has SHA256SUMS is published already and is not uploaded again (unless -Force).
    4. Uploads the feed: packages that are not on the server yet, then the installers, and the index files
       (releases.<channel>.json, RELEASES-<channel>, assets.<channel>.json) last, so installed apps never see a version
       whose packages are still missing.
    5. Checks every uploaded file at -WebUrl (size) and that the feed's index lists the version.

    The connection requires TLS (explicit FTPS). The password comes from the environment variable
    MAILNIX_FTP_PASSWORD or is asked for; it goes to curl through standard input, never on a command line.

.EXAMPLE
    .\scripts\publish-release.ps1 1.4.0
.EXAMPLE
    .\scripts\publish-release.ps1 1.4.0 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory, Position = 0)]
    [string] $Version,

    # The FTP folder of the releases, with the user name. rainbow.cloudsteps.de is the server of cloudsteps.de; its
    # TLS certificate is issued for that name.
    [string] $FtpUrl = 'ftp://deploy@rainbow.cloudsteps.de/mailnix/releases',

    # The web address of the same folder.
    [string] $WebUrl = 'https://cloudsteps.de/u/mailnix/releases',

    # The release folder. Defaults to artifacts\releases\<version> in the repository.
    [string] $Output,

    # The Velopack feed folder. Defaults to artifacts\releases\updates in the repository.
    [string] $Feed,

    # Only the release folder, not the update feed (e.g. a release built with -NoInstaller).
    [switch] $NoFeed,

    # Upload the release folder although the server has it already.
    [switch] $Force,

    # Accept any TLS certificate. Only for testing against a local server.
    [switch] $SkipCertificateCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root "artifacts\releases\$Version" }
$Output = [System.IO.Path]::GetFullPath($Output)
if (-not $Feed) { $Feed = Join-Path $root 'artifacts\releases\updates' }
$Feed = [System.IO.Path]::GetFullPath($Feed)

# --- Checks ------------------------------------------------------------------------------------------
if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw 'curl.exe was not found on PATH (it comes with Windows 10 and later).' }
$ftp = [Uri] $FtpUrl
if ($ftp.Scheme -ne 'ftp' -or -not $ftp.UserInfo) { throw "-FtpUrl must look like ftp://user@host/folder, not '$FtpUrl'." }
$user = [Uri]::UnescapeDataString($ftp.UserInfo)
$ftpBase = "ftp://$($ftp.Authority)$($ftp.AbsolutePath.TrimEnd('/'))"
$webBase = $WebUrl.TrimEnd('/')

if (-not (Test-Path (Join-Path $Output 'SHA256SUMS'))) { throw "$Output has no SHA256SUMS; build the release with release.ps1 first." }
$sums = @{}
foreach ($line in Get-Content (Join-Path $Output 'SHA256SUMS')) {
    if ($line -match '^([0-9a-f]{64})  (.+)$') { $sums[$Matches[2]] = $Matches[1] }
}
$files = @(Get-ChildItem $Output -File | Where-Object Name -ne 'SHA256SUMS' | Sort-Object Name)
foreach ($file in $files) {
    if (-not $sums.ContainsKey($file.Name)) { throw "$($file.Name) is not listed in SHA256SUMS." }
    if ((Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sums[$file.Name]) { throw "$($file.Name) does not match its checksum in SHA256SUMS." }
}
foreach ($name in $sums.Keys) {
    if (-not (Test-Path (Join-Path $Output $name))) { throw "$name is listed in SHA256SUMS but missing in $Output." }
}

# Index files are rewritten by every release; packages carry their version in the name and never change.
$isIndex = { param($name) $name -like 'releases.*.json' -or $name -like 'RELEASES-*' -or $name -like 'assets.*.json' }
$feedFiles = @()
if (-not $NoFeed) {
    if (-not (Test-Path $Feed)) { throw "The feed folder $Feed does not exist (-NoFeed for a release without installers)." }
    $feedFiles = @(Get-ChildItem $Feed -File | Sort-Object Name)
    if (-not ($feedFiles | Where-Object Name -like "mailnix-$Version-*-full.nupkg")) { throw "The feed $Feed has no package of $Version." }
}

if ($WhatIfPreference) {
    Write-Host "Would upload to $ftpBase (web: $webBase):"
    $files + @(Get-Item (Join-Path $Output 'SHA256SUMS')) | ForEach-Object { Write-Host "  $Version/$($_.Name)" }
    $feedFiles | ForEach-Object { Write-Host "  updates/$($_.Name)$(if (-not (& $isIndex $_.Name) -and $_.Name -like '*.nupkg') { ' (if not on the server yet)' })" }
    return
}

$password = $env:MAILNIX_FTP_PASSWORD
if (-not $password) {
    $secure = Read-Host "FTP password for $user@$($ftp.Host)" -AsSecureString
    $password = [System.Net.NetworkCredential]::new('', $secure).Password
}
if (-not $password) { throw 'No password.' }

# --- curl --------------------------------------------------------------------------------------------
# A curl config file (on standard input): keeps the password off the command line.
function ConfigString([string] $value) { '"' + ($value -replace '\\', '\\' -replace '"', '\"') + '"' }

function Invoke-Curl([string[]] $Lines, [string[]] $Arguments) {
    $config = @("user = $(ConfigString "${user}:$password")") + $Lines
    $common = @('--config', '-', '--ssl-reqd', '--silent', '--show-error', '--connect-timeout', '30')
    if ($SkipCertificateCheck) { $common += '--insecure' }
    $result = $config | & curl.exe @common @Arguments 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($result | ForEach-Object { "$_" }) }
}

function FtpUrlOf([string] $folder, [string] $name = '') {
    "$ftpBase/$folder/$(if ($name) { [Uri]::EscapeDataString($name) })"
}

# Names in a folder on the server; an empty list if the folder does not exist yet.
function Get-RemoteNames([string] $folder) {
    $result = Invoke-Curl @() @('--list-only', (FtpUrlOf $folder))
    switch ($result.ExitCode) {
        0 { return @($result.Output | Where-Object { $_ -and $_ -notin '.', '..' } | ForEach-Object { Split-Path $_ -Leaf }) }
        { $_ -in 9, 78 } { return @() } # the folder does not exist (yet)
        67 { throw "Signing in as $user at $($ftp.Host) failed. Not trying again (the server blocks repeated failures)." }
        default { throw "Listing $folder on the server failed (curl exit code $($result.ExitCode)): $($result.Output -join ' ')" }
    }
}

function Send-Files([string] $folder, [System.IO.FileInfo[]] $items) {
    if (-not $items) { return }
    $lines = foreach ($item in $items) {
        "url = $(ConfigString (FtpUrlOf $folder $item.Name))"
        "upload-file = $(ConfigString $item.FullName)"
    }
    $result = Invoke-Curl $lines @('--ftp-create-dirs', '--retry', '2', '--write-out', '  uploaded %{url_effective} (%{size_upload} bytes)\n')
    $result.Output | ForEach-Object { Write-Host ([Uri]::UnescapeDataString($_.Replace("$ftpBase/", ''))) }
    if ($result.ExitCode -ne 0) { throw "Uploading to $folder failed (curl exit code $($result.ExitCode))." }
}

# --- Upload ------------------------------------------------------------------------------------------
Write-Host "==> Signing in to $($ftp.Host) as $user" -ForegroundColor Cyan
$remoteRelease = Get-RemoteNames $Version
if ($remoteRelease -contains 'SHA256SUMS' -and -not $Force) {
    throw "$Version is published already ($webBase/$Version/SHA256SUMS exists). A released version is never uploaded again; -Force overrides this."
}

Write-Host "==> Release folder $Version/" -ForegroundColor Cyan
Send-Files $Version $files
Send-Files $Version @(Get-Item (Join-Path $Output 'SHA256SUMS'))
$uploaded = @($files + @(Get-Item (Join-Path $Output 'SHA256SUMS')) | ForEach-Object { [pscustomobject]@{ Path = "$Version/$($_.Name)"; Length = $_.Length } })

if ($feedFiles) {
    Write-Host '==> Update feed updates/' -ForegroundColor Cyan
    $remoteFeed = Get-RemoteNames 'updates'
    $packages = @($feedFiles | Where-Object { $_.Name -like '*.nupkg' -and $_.Name -notin $remoteFeed })
    $others = @($feedFiles | Where-Object { $_.Name -notlike '*.nupkg' -and -not (& $isIndex $_.Name) })
    $indexes = @($feedFiles | Where-Object { & $isIndex $_.Name })
    $skipped = $feedFiles.Count - $packages.Count - $others.Count - $indexes.Count
    if ($skipped) { Write-Host "  $skipped packages of earlier versions are on the server already" }
    Send-Files 'updates' $packages
    Send-Files 'updates' $others
    Send-Files 'updates' $indexes
    $uploaded += @($packages + $others + $indexes | ForEach-Object { [pscustomobject]@{ Path = "updates/$($_.Name)"; Length = $_.Length } })
}

# --- Check on the web server -------------------------------------------------------------------------
Write-Host "==> Checking $webBase" -ForegroundColor Cyan
$web = @{ Method = 'Head'; SkipHttpErrorCheck = $true; SkipCertificateCheck = [bool] $SkipCertificateCheck }
$problems = @()
foreach ($file in $uploaded) {
    $response = Invoke-WebRequest @web -Uri "$webBase/$($file.Path)"
    $length = $response.Headers['Content-Length'] | Select-Object -First 1
    if ($response.StatusCode -ne 200) { $problems += "$($file.Path): HTTP $($response.StatusCode)" }
    elseif ($length -and [long] $length -ne $file.Length) { $problems += "$($file.Path): $length bytes on the web server, $($file.Length) here" }
}
foreach ($index in $feedFiles | Where-Object Name -like 'releases.*.json') {
    $listed = (Invoke-RestMethod -Uri "$webBase/updates/$($index.Name)" -SkipCertificateCheck:$SkipCertificateCheck).Assets.Version
    $local = (Get-Content $index.FullName -Raw | ConvertFrom-Json).Assets.Version
    if ($local -contains $Version -and $listed -notcontains $Version) { $problems += "updates/$($index.Name) on the web server does not list $Version" }
}
if ($problems) { throw "Uploaded, but the web server does not serve everything correctly:`n$($problems -join "`n")" }

Write-Host "Published $Version ($($uploaded.Count) files):" -ForegroundColor Green
Write-Host "  Downloads:  $webBase/$Version/"
if ($feedFiles) { Write-Host "  Updates:    $webBase/updates (release.ps1 -UpdateUrl)" }
