<#
.SYNOPSIS
    Builds a mailnix release on this machine: tests, the standalone apps with the version, checksums and a git tag.

.DESCRIPTION
    1. Checks: the version is semantic (1.4.0, or 1.5.0-beta.1 for a pre-release), the working tree is clean, and
       neither the tag v<version> nor the release folder exists yet.
    2. Tests: backend (dotnet test) and frontend (type-check, lint, unit tests). The Playwright e2e tests need a
       running instance and are not part of this; run them before a release.
    3. Builds each runtime with MailnixVersion=<version>: build-windows.ps1 for win-*, build-unix.sh in Git Bash for
       linux-*.
    4. Windows installer (unless -NoInstaller): for each win-* runtime the desktop app (build-windows.ps1 -Desktop),
       packed with Velopack (vpk, a local dotnet tool in backend/) into the update feed <Feed> and a Setup.exe.
       The feed folder is kept across releases: Velopack lists all versions there and builds delta updates from the
       previous one. Pre-releases go to their own channel (<runtime>-beta), so installed stable versions never get them.
    5. Output: <Output>\mailnix-<version>-<runtime>.zip / .tar.gz / -setup.exe and SHA256SUMS, as they go on the
       download server.
    6. Tags the commit v<version>. Pushing the tag is up to you; publish-release.ps1 <version> uploads <Output> and
       the feed to the download server.

    macOS builds have to be signed on a Mac (see README): build them there with build-unix.sh --archive, add them to
    the folder and update SHA256SUMS.

.EXAMPLE
    .\scripts\release.ps1 1.4.0
.EXAMPLE
    .\scripts\release.ps1 1.5.0-beta.1 -Runtimes win-x64 -NoTag
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string] $Version,

    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'linux-musl-arm64')]
    [string[]] $Runtimes = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64'),

    # Defaults to artifacts\releases\<version> in the repository.
    [string] $Output,

    # A trial run: no tag, and uncommitted changes are allowed. Delete the folder afterwards.
    [switch] $NoTag,

    # Where installed desktop apps look for updates: the web address the content of the feed folder is uploaded to
    # (publish-release.ps1 uploads it to updates/ there). Built into mailnix-desktop.exe; Mailnix:Desktop:UpdateUrl in
    # the data folder's appsettings.json overrides it.
    [string] $UpdateUrl = 'https://cloudsteps.de/u/mailnix/releases/updates',

    # The Velopack feed folder, kept across releases. Defaults to artifacts\releases\updates in the repository.
    [string] $Feed,

    # Only the zip/tar.gz builds, no Windows installer.
    [switch] $NoInstaller,

    # Code signing for the Windows programs, installers and update packages: a command in which {{file}} stands for
    # the quoted path (see build-windows.ps1 -SignTemplate and vpk --signTemplate). Without it the release is
    # unsigned: Windows warns about the installer, and nothing proves that an update comes from you.
    [string] $SignTemplate = $env:MAILNIX_SIGN_TEMPLATE
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$tag = "v$Version"
if (-not $Output) { $Output = Join-Path $root "artifacts\releases\$Version" }
$Output = [System.IO.Path]::GetFullPath($Output)
if (-not $Feed) { $Feed = Join-Path $root 'artifacts\releases\updates' }
$Feed = [System.IO.Path]::GetFullPath($Feed)
$installers = -not $NoInstaller -and ($Runtimes -like 'win-*')
$staging = Join-Path $root "artifacts\release-build\$Version"

function Invoke-Step([string] $Title, [scriptblock] $Action) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Title failed (exit code $LASTEXITCODE)." }
}

# --- Checks ------------------------------------------------------------------------------------------
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$') {
    throw "'$Version' is not a version like 1.4.0 or 1.5.0-beta.1."
}
foreach ($tool in 'git', 'dotnet', 'npm') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "'$tool' was not found on PATH." }
}
if (Test-Path $Output) { throw "$Output exists already. A released version is never rebuilt; delete it if it was a trial run." }
git -C $root rev-parse -q --verify "refs/tags/$tag" | Out-Null
if ($LASTEXITCODE -eq 0) { throw "The tag $tag exists already." }
$changes = git -C $root status --porcelain
if ($changes -and -not $NoTag) { throw "There are uncommitted changes; a release is built from a commit:`n$($changes -join "`n")" }
$branch = git -C $root branch --show-current
if ($branch -ne 'main') { Write-Warning "Releasing from '$branch', not main." }

if ($installers -and -not $UpdateUrl) {
    Write-Warning 'No -UpdateUrl: installed apps of this release only look for updates once Mailnix:Desktop:UpdateUrl is set in their data folder.'
}
if ($UpdateUrl -and $UpdateUrl -notmatch '^https?://') { throw "-UpdateUrl must be an http(s) address, not '$UpdateUrl'." }
if (($Runtimes -like 'win-*') -and -not $SignTemplate) {
    Write-Warning 'No -SignTemplate (or MAILNIX_SIGN_TEMPLATE): the Windows programs, installers and updates are not code-signed.'
}
$sign = if ($SignTemplate) { @{ SignTemplate = $SignTemplate } } else { @{} }
$vpkSign = if ($SignTemplate) { @('--signTemplate', $SignTemplate) } else { @() }

$bash = $null
if ($Runtimes -like 'linux-*') {
    # Git Bash, not the WSL bash.exe that may come first on PATH.
    $bash = Join-Path (Split-Path (Split-Path (Get-Command git).Source)) 'bin\bash.exe'
    if (-not (Test-Path $bash)) { throw "Git Bash was not found at $bash; it builds the Linux runtimes." }
}

# --- Tests -------------------------------------------------------------------------------------------
Push-Location (Join-Path $root 'backend')
try {
    Invoke-Step 'Backend tests' { dotnet test --solution Mailnix.slnx }
}
finally {
    Pop-Location
}
Push-Location (Join-Path $root 'frontend')
try {
    Invoke-Step 'Installing frontend packages' { npm ci --no-audit --no-fund }
    Invoke-Step 'Frontend type-check' { npm run type-check }
    Invoke-Step 'Frontend lint' { npm run lint }
    Invoke-Step 'Frontend tests' { npm test }
}
finally {
    Pop-Location
}

# --- Builds ------------------------------------------------------------------------------------------
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
$previousVersion = $env:MailnixVersion
$previousUpdateUrl = $env:MailnixUpdateUrl
# Picked up by every dotnet build (Directory.Build.props, Mailnix.Desktop.csproj).
$env:MailnixVersion = $Version
$env:MailnixUpdateUrl = $UpdateUrl
if ($installers) {
    Push-Location (Join-Path $root 'backend')
    try {
        Invoke-Step 'Restoring vpk' { dotnet tool restore }
    }
    finally {
        Pop-Location
    }
    New-Item -ItemType Directory $Feed -Force | Out-Null
}
try {
    foreach ($runtime in $Runtimes) {
        $build = Join-Path $staging "mailnix-$Version-$runtime"
        if ($runtime -like 'win-*') {
            Invoke-Step "Building $runtime" { & (Join-Path $PSScriptRoot 'build-windows.ps1') -Runtime $runtime -Output $build -Zip @sign }
            $built = (Get-Item (Join-Path $build 'mailnix.exe')).VersionInfo.ProductVersion
            if ($built -ne $Version -and -not $built.StartsWith("$Version+")) { throw "mailnix.exe reports version '$built', not $Version." }

            if ($installers) {
                $desktop = Join-Path $staging "desktop-$runtime"
                Invoke-Step "Building the desktop app ($runtime)" { & (Join-Path $PSScriptRoot 'build-windows.ps1') -Runtime $runtime -Output $desktop -Desktop @sign }
                $channel = if ($Version -like '*-*') { "$runtime-beta" } else { $runtime }
                Push-Location (Join-Path $root 'backend')
                try {
                    Invoke-Step "Packing the installer ($runtime, channel $channel)" {
                        dotnet tool run vpk pack --packId mailnix --packVersion $Version --packDir $desktop `
                            --mainExe mailnix-desktop.exe --packTitle mailnix --packAuthors mailnix `
                            --icon (Join-Path $root 'backend\src\Mailnix.Api\mailnix.ico') `
                            --runtime $runtime --channel $channel --outputDir $Feed --noPortable `
                            --exclude '(^|[\\/])data[\\/]|\.pdb$' @vpkSign
                    }
                }
                finally {
                    Pop-Location
                }
                Copy-Item (Join-Path $Feed "mailnix-$channel-Setup.exe") (Join-Path $staging "mailnix-$Version-$runtime-setup.exe")
            }
        }
        else {
            $script = (Join-Path $PSScriptRoot 'build-unix.sh') -replace '\\', '/'
            Invoke-Step "Building $runtime" { & $bash $script --runtime $runtime --output ($build -replace '\\', '/') --archive }
        }
    }
}
finally {
    $env:MailnixVersion = $previousVersion
    $env:MailnixUpdateUrl = $previousUpdateUrl
}

New-Item -ItemType Directory $Output | Out-Null
Get-ChildItem $staging -File | Where-Object { $_.Name -like '*.zip' -or $_.Name -like '*.tar.gz' -or $_.Name -like '*-setup.exe' } | Move-Item -Destination $Output
# The format of sha256sum, so `sha256sum -c SHA256SUMS` checks a download.
$sums = Get-ChildItem $Output -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[System.IO.File]::WriteAllText((Join-Path $Output 'SHA256SUMS'), ($sums -join "`n") + "`n")
Remove-Item $staging -Recurse -Force

# --- Tag ---------------------------------------------------------------------------------------------
if ($NoTag) {
    Write-Host "Trial run of $Version (not tagged): $Output" -ForegroundColor Yellow
}
else {
    Invoke-Step "Tagging $tag" { git -C $root tag -a $tag -m "mailnix $Version" }
    Write-Host "Release $Version ($(git -C $root rev-parse --short HEAD)): $Output" -ForegroundColor Green
    Write-Host "Push the tag with: git push origin $tag"
    Write-Host "Upload it with:    .\scripts\publish-release.ps1 $Version"
}
Get-ChildItem $Output | Format-Table Name, @{ n = 'MB'; e = { '{0:N1}' -f ($_.Length / 1MB) } } -AutoSize
if ($installers) {
    Write-Host "Update feed: $Feed$(if ($UpdateUrl) { " (served at $UpdateUrl)" })"
}
