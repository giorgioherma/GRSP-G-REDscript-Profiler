param(
    [Parameter(Mandatory=$true)]
    [string]$GameRoot
)

$ErrorActionPreference = "Stop"
$dll = Join-Path $PSScriptRoot "target\release\redscript_profiler_alpha.dll"

if (-not (Test-Path $dll)) {
    throw "DLL not built yet: $dll"
}

$dst = Join-Path $GameRoot "red4ext\plugins\redscript_profiler_alpha.dll"
New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
Copy-Item $dll $dst -Force

Write-Host "Installed:"
Write-Host $dst
Write-Host ""
Write-Host "Results will appear under:"
Write-Host (Join-Path $GameRoot "red4ext\plugins\redscript_profiler_alpha\RESULTS")
