<#
.SYNOPSIS
    Runs the standalone Windows build of mailnix (building it first if needed) and opens it in the browser.

.DESCRIPTION
    Starts <Output>\mailnix.exe in this console; press Ctrl+C to stop it. Data stays in <Output>\data.

.EXAMPLE
    .\scripts\run-windows.ps1
.EXAMPLE
    .\scripts\run-windows.ps1 -Build -Urls http://localhost:8080
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    # Same default as build-windows.ps1: artifacts\windows\<runtime> in the repository.
    [string] $Output,

    # Address(es) to listen on, separated by ';'.
    [string] $Urls = 'http://localhost:5000',

    # Rebuild even if a build exists.
    [switch] $Build,

    # Don't open the browser.
    [switch] $NoBrowser
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root "artifacts\windows\$Runtime" }
$Output = [System.IO.Path]::GetFullPath($Output)
$exe = Join-Path $Output 'mailnix.exe'

if ($Build -or -not (Test-Path $exe)) {
    & (Join-Path $PSScriptRoot 'build-windows.ps1') -Runtime $Runtime -Output $Output
}

$running = Get-Process mailnix -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) { throw "mailnix.exe is already running from $Output (process $($running.Id -join ', '))." }

# The browser needs a concrete host, not a wildcard binding.
$browserUrl = ($Urls -split ';')[0].Trim() -replace '://(0\.0\.0\.0|\*|\+|\[::\])', '://localhost'

Write-Host "Starting mailnix on $Urls (Ctrl+C to stop)" -ForegroundColor Cyan
$process = Start-Process $exe -ArgumentList '--urls', $Urls -WorkingDirectory $Output -NoNewWindow -PassThru
try {
    if (-not $NoBrowser) {
        $deadline = (Get-Date).AddSeconds(60)
        while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
            try {
                Invoke-WebRequest "$browserUrl/healthz" -UseBasicParsing -TimeoutSec 2 | Out-Null
                Start-Process $browserUrl
                break
            }
            catch {
                Start-Sleep -Milliseconds 500
            }
        }
    }
    $process.WaitForExit()
}
finally {
    # Ctrl+C reaches the app through the shared console; this only covers other ways of ending the script.
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
