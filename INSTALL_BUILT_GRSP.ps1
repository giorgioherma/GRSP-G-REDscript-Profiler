param(
    [Parameter(Mandatory=$true)]
    [string]$GameRoot
)

$ErrorActionPreference = 'Stop'
$Source = Join-Path $PSScriptRoot 'target\release\redscript_profiler_alpha.dll'
$PluginDir = Join-Path $GameRoot 'red4ext\plugins'
$Target = Join-Path $PluginDir 'redscript_profiler_alpha.dll'
$DataDir = Join-Path $PluginDir 'redscript_profiler_alpha'
$ScenarioSource = Join-Path $PSScriptRoot 'RSP_Scenario.txt'
$ScenarioTarget = Join-Path $DataDir 'RSP_Scenario.txt'

if (-not (Test-Path $Source)) {
    throw "Built DLL not found: $Source"
}

New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
Copy-Item -Force $Source $Target

if ((Test-Path $ScenarioSource) -and -not (Test-Path $ScenarioTarget)) {
    Copy-Item $ScenarioSource $ScenarioTarget
}

Write-Host "Installed GRSP DLL: $Target" -ForegroundColor Green
Write-Host "Scenario file: $ScenarioTarget" -ForegroundColor Green
