# GRSP-owned headless SCC 0.5.31 compiler contract. Runner only; no game writes.
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
# One native invocation implementation for both the smoke and full-source gates.
# Logs stay on the private runner; never publish third-party source paths.
# The official scc.exe CLI hardcodes show_error_popup=true. Its error
# dialog hangs headless Windows Actions; use the source-owned .NET 10 CLI
# over scc_lib.dll exports to disable the popup and report real errors.
function Invoke-HeadlessScc {
  param([string]$Phase,[string]$IsolatedRoot,[string]$CompilerLib,[int]$TimeoutSeconds)
  $r6=Join-Path $IsolatedRoot 'r6'
  $cache=Join-Path $r6 'cache/final.redscripts'
  $compiled=$cache+'.modded'
  $report=Join-Path $work ($Phase+'-headless-result.json')
  $privateLog=Join-Path $work ($Phase+'-native-compiler-error.log')
  Set-Content -LiteralPath (Join-Path $IsolatedRoot '.grsp-r6-workspace') -Value 'GRSP_R6_ISOLATED_WORKSPACE' -NoNewline
  New-Item -ItemType Directory -Force -Path (Join-Path $r6 'logs') | Out-Null
  $args=@($dll,'scc-headless','--workspace',$IsolatedRoot,'--r6',$r6,'--cache',$cache,'--compiled',$compiled,'--scc-native',$CompilerLib,'--out',$report,'--log',$privateLog)
  $info=[System.Diagnostics.ProcessStartInfo]::new('dotnet')
  $info.WorkingDirectory=Split-Path $dll -Parent
  $info.UseShellExecute=$false
  $info.RedirectStandardOutput=$true
  $info.RedirectStandardError=$true
  foreach($arg in $args) { [void]$info.ArgumentList.Add([string]$arg) }
  $p=[System.Diagnostics.Process]::new()
  $p.StartInfo=$info
  $timer=[System.Diagnostics.Stopwatch]::StartNew()
  try {
    if (!$p.Start()) { throw ('R6_HEADLESS_SCC_LAUNCH_FAILED: phase='+$Phase) }
    $stdout=$p.StandardOutput.ReadToEndAsync()
    $stderr=$p.StandardError.ReadToEndAsync()
    $exited=$p.WaitForExit($TimeoutSeconds*1000)
    if (!$exited) { $p.Kill($true); $p.WaitForExit() }
    $out=$stdout.GetAwaiter().GetResult()
    $err=$stderr.GetAwaiter().GetResult()
    $cpu=[Math]::Round($p.TotalProcessorTime.TotalSeconds,1)
    $exitLabel=if($exited){[string]$p.ExitCode}else{'TIMEOUT'}
    Write-Host ('R6 SCC headless: phase='+$Phase+' seconds='+[Math]::Round($timer.Elapsed.TotalSeconds,1)+' cpuSeconds='+$cpu+' exit='+$exitLabel)
    if (!$exited) { throw ('R6_HEADLESS_SCC_TIMEOUT: phase='+$Phase+' limit='+$TimeoutSeconds+'s') }
    if (!(Test-Path -LiteralPath $report -PathType Leaf)) {
      throw ('R6_HEADLESS_SCC_REPORT_MISSING: phase='+$Phase+' exit='+$p.ExitCode+' stderrChars='+$err.Length)
    }
    $result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -Depth 40
    if ($result.schemaVersion -ne 'grsp.scc.headless.v1' -or
        $result.guiErrorPopupDisabled -ne $true -or
        $result.originalCacheUnchanged -ne $true) {
      throw ('R6_HEADLESS_SCC_EVIDENCE_INVALID: phase='+$Phase)
    }
    if ($p.ExitCode -ne 0 -or $result.success -ne $true) {
      # Native flexi_logger files are closed now, after the DLL-hosting
      # dotnet process exited. Extract codes only; never print script paths
      # or upload raw private compiler diagnostics.
      $codes=@($result.errorCodeCounts.PSObject.Properties | ForEach-Object { $_.Name+'='+$_.Value })
      $nativeCodes=@()
      foreach($file in @(Get-ChildItem -LiteralPath (Join-Path $r6 'logs') -Filter 'redscript*.log' -File)) {
        $content=Get-Content -LiteralPath $file.FullName -Raw
        foreach($match in [regex]::Matches($content,'\[[A-Z][A-Z0-9_]+\]')) {
          if ($match.Value -notin @('[INFO]','[WARN]','[ERROR]','[DEBUG]','[TRACE]')) {
            $nativeCodes+=@($match.Value)
          }
        }
      }
      if ($nativeCodes.Count -gt 0) {
        $codes=@($nativeCodes | Group-Object | Sort-Object Name | ForEach-Object { $_.Name+'='+$_.Count })
      }
      $safeCodes=if($codes.Count -gt 0){$codes -join ','}else{'NO_STRUCTURED_ERROR_CODES'}
      throw ('R6_HEADLESS_SCC_COMPILATION_FAILED: phase='+$Phase+' errorCodes='+$safeCodes+' diagnosticBytes='+$result.diagnosticBytes+' rawErrorsRunnerPrivate=true')
    }
    if ($result.generatedOutputPresent -ne $true -or
        [long]$result.generatedOutputBytes -lt 1048576) {
      throw ('R6_HEADLESS_SCC_OUTPUT_MISSING: phase='+$Phase)
    }
    return $result
  }
  finally { $p.Dispose(); $timer.Stop() }
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
  # Fast executable and vanilla-cache probe before thousands of mod sources.
  # Explicit -outputCacheFile is required: SCC otherwise writes back into the source cache.
  $smokeScripts=Join-Path $work 'smoke/r6/scripts'
  $smokeCache=Join-Path $work 'smoke/r6/cache/final.redscripts'
  New-Item -ItemType Directory -Force -Path @($smokeScripts,(Split-Path $smokeCache -Parent)) | Out-Null
  Copy-Item -LiteralPath $baseFile.FullName -Destination $smokeCache -Force
  $smokeStatus=Invoke-HeadlessScc -Phase 'empty-source-smoke' -IsolatedRoot (Join-Path $work 'smoke') -CompilerLib $libs[0].FullName -TimeoutSeconds 35
  $smokeModded=$smokeCache+'.modded'
  $smokeOriginal=(Get-FileHash -LiteralPath $smokeCache -Algorithm SHA256).Hash
  if ($smokeOriginal -ne $baseSha -or !(Test-Path -LiteralPath $smokeModded -PathType Leaf) -or (Get-Item -LiteralPath $smokeModded).Length -lt 1048576) {
    throw 'R6 SCC smoke must preserve vanilla cache and emit a .modded compiled bundle.'
  }
  Write-Host ('PASS GRSP SCC 0.5.31 empty-source smoke: baselineUnchanged=true moddedBytes='+(Get-Item -LiteralPath $smokeModded).Length)
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
    # Same direct native API proven by empty-source smoke, now complete
    # installed source tree. Explicit output path protects the vanilla cache.
    $privateLog=Join-Path $work ($scenario + '-scc.log')
    $fullStatus=Invoke-HeadlessScc -Phase $scenario -IsolatedRoot $root -CompilerLib $libs[0].FullName -TimeoutSeconds 240
    $compiled=$cache+'.modded'
    if (!(Test-Path -LiteralPath $compiled -PathType Leaf)) {
      throw ('R6_SCC_OUTPUT_MISSING: phase='+$scenario+' expected .modded output')
    }
    $result=Get-Item -LiteralPath $compiled
    $sha=(Get-FileHash -LiteralPath $compiled -Algorithm SHA256).Hash
    if ($result.Length -lt 1048576 -or $sha -eq $baseSha -or (Get-FileHash -LiteralPath $cache -Algorithm SHA256).Hash -ne $baseSha) {
      throw ('R6_SCC_OUTPUT_UNPROVEN: phase='+$scenario+' invalid compiled cache or mutated vanilla input')
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
