$ErrorActionPreference = 'Stop'

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw 'Rust/Cargo was not found. Install Rust and the Windows MSVC build toolchain, or use GitHub Actions.'
}

cargo build --release

Write-Host ''
Write-Host 'Built GRSP 0.5.0 Public Preview:' -ForegroundColor Green
Write-Host '  target\release\redscript_profiler_alpha.dll'
