$ErrorActionPreference = 'Stop'

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw 'Rust/Cargo was not found. Install Rust and the Windows MSVC build toolchain, or use GitHub Actions.'
}

cargo build --release

$built = 'target\release\g_redscript_profiler.dll'
$public = 'target\release\G-REDscript-Profiler.dll'

if (-not (Test-Path $built)) {
    throw "Expected Rust build output was not found: $built"
}

Copy-Item $built $public -Force

Write-Host ''
Write-Host 'Built G-REDscript Profiler 1.0.0:' -ForegroundColor Green
Write-Host "  $public"
