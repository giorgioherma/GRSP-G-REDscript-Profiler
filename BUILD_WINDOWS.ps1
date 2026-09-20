$ErrorActionPreference = "Stop"

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    Write-Host "Rust/Cargo is not installed."
    Write-Host "Use the included GitHub Actions workflow, or install Rust + MSVC build tools first."
    exit 1
}

cargo build --release

$dll = Join-Path $PSScriptRoot "target\release\redscript_profiler_alpha.dll"
if (-not (Test-Path $dll)) {
    throw "Build finished but DLL was not found: $dll"
}

Write-Host ""
Write-Host "Built:"
Write-Host $dll
