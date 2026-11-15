# GRSP-owned R6 real compiler contract. Runner only; not installable output.
param(
  [Parameter(Mandatory=$true)][string]$GameRoot,
  [Parameter(Mandatory=$true)][string]$PreviewRoot,
  [Parameter(Mandatory=$true)][string]$CompilerArchive,
  [Parameter(Mandatory=$true)][string]$CleanBaselineArchive,
  [Parameter(Mandatory=$true)][string]$ResolverDll
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$game=(Resolve-Path -LiteralPath $GameRoot).Path
$preview=(Resolve-Path -LiteralPath $PreviewRoot).Path
$compilerZip=(Resolve-Path -LiteralPath $CompilerArchive).Path
$baselineZip=(Resolve-Path -LiteralPath $CleanBaselineArchive).Path
$dll=(Resolve-Path -LiteralPath $ResolverDll).Path
$plan=Get-Content -LiteralPath (Join-Path $preview 'preview.json') -Raw | ConvertFrom-Json -Depth 80
if ($plan.schemaVersion -ne 'grsp.generic.preview.v1' -or
    $plan.rule -ne 'CNAME_ARRAY_MEMBERSHIP_V1' -or
    $plan.generationAuthorized -ne $false -or
    $plan.compilerValidated -ne $false -or
    $plan.deployable -ne $false -or
    [int]$plan.stagedFiles -lt 1) {
  throw 'R6 requires real structurally proven, nondeployable GRSP source preview.'
}
$work=Join-Path $env:RUNNER_TEMP ('grsp-r6-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
  $compilerRoot=Join-Path $work 'compiler'
  $baselineRoot=Join-Path $work 'supplied-base'
  New-Item -ItemType Directory -Path @($compilerRoot,$baselineRoot) -Force | Out-Null
  Expand-Archive -LiteralPath $compilerZip -DestinationPath $compilerRoot -ErrorAction Stop
  Expand-Archive -LiteralPath $baselineZip -DestinationPath $baselineRoot -ErrorAction Stop
  $tools=@(Get-ChildItem -LiteralPath $compilerRoot -Recurse -File -Filter 'scc.exe')
  $libs=@(Get-ChildItem -LiteralPath $compilerRoot -Recurse -File -Filter 'scc_lib.dll')
  $bases=@(Get-ChildItem -LiteralPath $baselineRoot -Recurse -File | Where-Object Name -eq 'final.redscripts')
  if ($tools.Count -ne 1 -or $libs.Count -ne 1 -or $bases.Count -ne 1 -or
      $tools[0].DirectoryName -ne $libs[0].DirectoryName) {
    throw 'R6 requires one SCC executable + DLL together and one original final.redscripts candidate.'
  }
  $upstream=@{
    'scc.exe'='DA2C0DE580173010C907A05FDD2F3CD2430D6E62BF748339D65060118AE32EDD'
    'scc_lib.dll'='49EB60A3613C990CD13B6136E827A74F9F64AD68E2F911D8C93E4E3D3F53C100'
  }
  foreach($binary in @($tools[0],$libs[0])) {
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $binary.FullName).Hash -ne $upstream[$binary.Name]) {
      throw ('R6 supplied compiler differs from verified redscript 0.5.31: '+$binary.Name)
    }
  }
  $baseFile=$bases[0]
  if ($baseFile.Length -lt 1048576) {
    throw 'R6 baseline candidate too small to contain a valid game script bundle.'
  }
  $baseSha=(Get-FileHash -Algorithm SHA256 -LiteralPath $baseFile.FullName).Hash
  $liveSources=Join-Path $game 'r6/scripts'
  $liveCount=@(Get-ChildItem -LiteralPath $liveSources -Filter '*.reds' -Recurse -File).Count
  if ($liveCount -lt 100 -or $liveCount -ne [int]$plan.sourceCount) {
    throw 'R6 source inventory differs from the admitted original capture.'
  }
  $overlayRoot=Join-Path $preview 'overlay/r6/scripts'
  $overlay=@(Get-ChildItem -LiteralPath $overlayRoot -Recurse -File)
  if ($overlay.Count -ne [int]$plan.stagedFiles) {
    throw 'R6 staged overlay has unverified file count.'
  }
  foreach($file in $overlay) {
    if ($file.Extension -ne '.reds' -or
        ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
      throw 'R6 staged overlay contains an unsafe non-REDscript source.'
    }
  }
  $outputs=@()
  foreach($scenario in @('untouched','optimized-preview')) {
    $root=Join-Path $work $scenario
    $scripts=Join-Path $root 'r6/scripts'
    $cache=Join-Path $root 'r6/cache/final.redscripts'
    New-Item -ItemType Directory -Path $scripts -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path $cache -Parent) -Force | Out-Null
    foreach($item in @(Get-ChildItem -LiteralPath $liveSources -Force)) {
      Copy-Item -LiteralPath $item.FullName -Destination $scripts -Recurse -Force
    }
    Copy-Item -LiteralPath $baseFile.FullName -Destination $cache -Force
    if ((Get-FileHash -LiteralPath $cache -Algorithm SHA256).Hash -ne $baseSha) {
      throw 'R6 source compilation input cache differs from user supplied baseline.'
    }
    if ($scenario -eq 'optimized-preview') {
      foreach($file in $overlay) {
        $relative=[System.IO.Path]::GetRelativePath($overlayRoot,$file.FullName)
        if ([System.IO.Path]::IsPathRooted($relative) -or
            $relative.StartsWith('..') -or $relative.Contains(':')) {
          throw 'R6 staged source escapes overlay ownership.'
        }
        $target=Join-Path $scripts $relative
        if (!(Test-Path -LiteralPath $target -PathType Leaf)) {
          throw 'R6 staged source cannot replace a missing original file.'
        }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
      }
    }
    $actualCount=@(Get-ChildItem -LiteralPath $scripts -Recurse -File -Filter '*.reds').Count
    if ($actualCount -ne $liveCount) {
      throw 'R6 isolated compilation source count changed.'
    }
    # SCC 0.5.31 actual CLI: -compile <source path> <cache file>.
    # Both invocations start from identical, unmodified user-supplied base.
    $privateLog=Join-Path $work ($scenario + '-scc.log')
    # Match RED4ext's headless compile policy: never execute VM code or
    # generate debugger instrumentation inside an isolated CI workspace.
    $args=@('-compile',$scripts,$cache,'-Wnone','-threads','4',
            '-no-testonly','-no-breakpoint','-no-exec','-no-debug',
            '-profile=off')
    # WaitForExit is bounded; a malformed input or hung native compiler
    # must never consume an unlimited Actions runner. Read both redirected
    # streams asynchronously to avoid pipe backpressure deadlocks.
    $processInfo=[System.Diagnostics.ProcessStartInfo]::new($tools[0].FullName)
    $processInfo.UseShellExecute=$false
    $processInfo.WorkingDirectory=$tools[0].DirectoryName
    $processInfo.RedirectStandardOutput=$true
    $processInfo.RedirectStandardError=$true
    foreach($arg in $args) { [void]$processInfo.ArgumentList.Add([string]$arg) }
    $process=[System.Diagnostics.Process]::new()
    $process.StartInfo=$processInfo
    try {
      if (!$process.Start()) { throw 'R6_SCC_NATIVE_LAUNCH_FAILED' }
      $stdoutTask=$process.StandardOutput.ReadToEndAsync()
      $stderrTask=$process.StandardError.ReadToEndAsync()
      if (!$process.WaitForExit(240000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw ('R6_' + $scenario.ToUpperInvariant() +
               '_SCC_TIMEOUT: native compiler exceeded 240 seconds')
      }
      $output=$stdoutTask.GetAwaiter().GetResult()
      $errors=$stderrTask.GetAwaiter().GetResult()
      [System.IO.File]::WriteAllText($privateLog, $output + [Environment]::NewLine + $errors)
      $code=$process.ExitCode
      if ($code -ne 0) {
        throw ('R6_' + $scenario.ToUpperInvariant() +
               '_SCC_FAILED: exit=' + $code + '; source diagnostics runner-private')
      }
    }
    finally { $process.Dispose() }
    $result=Get-Item -LiteralPath $cache
    $sha=(Get-FileHash -LiteralPath $cache -Algorithm SHA256).Hash
    if ($result.Length -lt 1048576 -or $sha -eq $baseSha) {
      throw ('R6_' + $scenario.ToUpperInvariant() +
             '_SCC_OUTPUT_UNPROVEN: missing or unchanged compiled bundle')
    }
    $outputs+=@([pscustomobject]@{ Scenario=$scenario; Bytes=$result.Length; Sha256=$sha })
  }
  if ($outputs.Count -ne 2 -or $outputs[0].Sha256 -eq $outputs[1].Sha256) {
    throw 'R6 compiled baseline and optimized-preview caches are indistinguishable.'
  }
  $audit=Join-Path $work 'verify-source.json'
  $auditLog=Join-Path $work 'verify-source.log'
  $expectedSha=[string]$plan.sourceStackSha256
  & dotnet $dll source-audit --game $game --out $audit --expected-source-sha256 $expectedSha *> $auditLog
  if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $audit -PathType Leaf)) {
    throw 'R6 original source changed during isolated compiler validation.'
  }
  Write-Host ('PASS GRSP full actual SCC 0.5.31 compiles: sourceFiles=' +
      $liveCount + ' optimizedFiles=' + $overlay.Count +
      ' untouchedCacheBytes=' + $outputs[0].Bytes +
      ' optimizedCacheBytes=' + $outputs[1].Bytes +
      ' compiledOutputsDistinct=true originalSourcesUntouched=true ' +
      'gameplayValidated=false deployable=false')
}
finally {
  if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop
  }
}
$global:LASTEXITCODE=0
