# GRSP-owned headless SCC 0.5.31 compiler contract. Runner only; no game writes.
param(
  [Parameter(Mandatory=$true)][string]$GameRoot,
  [Parameter(Mandatory=$true)][string]$PreviewRoot,
  [Parameter(Mandatory=$true)][string]$CompilerArchive,
  [Parameter(Mandatory=$true)][string]$CleanBaselineArchive,
  [Parameter(Mandatory=$true)][string]$EffectiveCacheArchive,
  [Parameter(Mandatory=$true)][string]$CodewareArchive,
  [Parameter(Mandatory=$true)][string]$ResolverDll
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$game=(Resolve-Path -LiteralPath $GameRoot).Path
$preview=(Resolve-Path -LiteralPath $PreviewRoot).Path
$compilerZip=(Resolve-Path -LiteralPath $CompilerArchive).Path
$baselineZip=(Resolve-Path -LiteralPath $CleanBaselineArchive).Path
$effectiveZip=(Resolve-Path -LiteralPath $EffectiveCacheArchive -ErrorAction Stop).Path
$codewareZip=(Resolve-Path -LiteralPath $CodewareArchive -ErrorAction Stop).Path
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
  $externalScripts=Join-Path $IsolatedRoot 'red4ext/plugins/Codeware/Scripts'
  if (Test-Path -LiteralPath $externalScripts -PathType Container) {
    $args+=@('--external-scripts',$externalScripts)
  }
  $expectedExternalPaths=if (Test-Path -LiteralPath $externalScripts -PathType Container) { 1 } else { 0 }
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
      $missingExternalAbi=[bool]($err -match 'scc_settings_add_script_path')
      $missingNativeSymbol=[bool]($err -match '(?i)entry point|export|symbol')
      Write-Host ('R6 headless launch diagnostics: externalScriptAbiMentioned='+$missingExternalAbi+' missingNativeSymbolHint='+$missingNativeSymbol)
      throw ('R6_HEADLESS_SCC_REPORT_MISSING: phase='+$Phase+' exit='+$p.ExitCode+' stderrChars='+$err.Length)
    }
    $result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -Depth 40
    if ($result.schemaVersion -ne 'grsp.scc.headless.v1' -or
        $result.guiErrorPopupDisabled -ne $true -or
        $result.originalCacheUnchanged -ne $true -or
        [int]$result.externalScriptPaths -ne $expectedExternalPaths) {
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
      # Never print the underlying private source or path. The native error
      # is retained only as an isolated-runner diagnostic.
      $safeUnresolved='NOT_IDENTIFIED'
      $locationParsed=$false
      $nativeLogFiles=@(Get-ChildItem -LiteralPath (Join-Path $r6 'logs') -Filter 'redscript*.log' -File)
      $diagnosticFiles=@($privateLog) + @($nativeLogFiles | ForEach-Object FullName)
      foreach($diagnosticFile in $diagnosticFiles) {
        if (!(Test-Path -LiteralPath $diagnosticFile -PathType Leaf)) { continue }
        $diagnostic=Get-Content -LiteralPath $diagnosticFile -Raw
        $position=[regex]::Match($diagnostic,'(?i)\bAt\s+(.+?\.reds):(\d+):(\d+)')
        if (!$position.Success) { continue }
        $locationParsed=$true
        $filePath=$position.Groups[1].Value
        $lineNo=[int]$position.Groups[2].Value
        $columnNo=[int]$position.Groups[3].Value
        if (!(Test-Path -LiteralPath $filePath -PathType Leaf) -or $lineNo -lt 1 -or $columnNo -lt 1) { continue }
        $sourceLine=@(Get-Content -LiteralPath $filePath)[$lineNo-1]
        if ($null -eq $sourceLine) { continue }
        $columnIndex=$columnNo-1
        foreach($match in [regex]::Matches([string]$sourceLine,'[A-Za-z_][A-Za-z0-9_]*')) {
          if ($match.Index -le $columnIndex -and $columnIndex -lt ($match.Index+$match.Length)) {
            if ($match.Value.Length -le 100) { $safeUnresolved=$match.Value }
            break
          }
        }
        if ($safeUnresolved -ne 'NOT_IDENTIFIED') { break }
      }
      $originExternal=0
      $originInstalled=0
      $symbolNames=[System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
      foreach($logItem in $nativeLogFiles) {
        $logLines=@(Get-Content -LiteralPath $logItem.FullName)
        foreach($oneLine in $logLines) {
          if ($oneLine -notmatch 'UNRESOLVED_') { continue }
          if ($oneLine -match 'Codeware[\\/]Scripts') { $originExternal++ }
          elseif ($oneLine -match '[\\/]r6[\\/]scripts[\\/]') { $originInstalled++ }
        }
        $contents=Get-Content -LiteralPath $logItem.FullName -Raw
        foreach($match in [regex]::Matches($contents, 'unresolved [a-z ]+''([A-Za-z_][A-Za-z0-9_.]{0,90})''')) {
          if ($symbolNames.Count -lt 12) { [void]$symbolNames.Add($match.Groups[1].Value) }
        }
      }
      Write-Host ('R6 unresolved origin: codeware='+$originExternal+' installed='+$originInstalled+' symbols='+(@($symbolNames) -join ','))
      $nativeLogCount=$nativeLogFiles.Count
      $nativeLogHasLocation=[bool](@($nativeLogFiles | Where-Object {
        [regex]::IsMatch((Get-Content -LiteralPath $_.FullName -Raw),'(?i)\.reds:\d+:\d+')
      }).Count -gt 0)
      throw ('R6_HEADLESS_SCC_COMPILATION_FAILED: phase='+$Phase+' errorCodes='+$safeCodes+' unresolvedIdentifier='+$safeUnresolved+' nativeLogCount='+$nativeLogCount+' nativeLogHasLocation='+$nativeLogHasLocation+' locationParsed='+$locationParsed+' diagnosticBytes='+$result.diagnosticBytes+' rawErrorsRunnerPrivate=true')
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
  $effectiveRoot=Join-Path $work 'supplied-effective-cache'
  $codewareRoot=Join-Path $work 'supplied-codeware'
  New-Item -ItemType Directory -Path @($compilerRoot,$baselineRoot,$effectiveRoot,$codewareRoot) -Force | Out-Null
  Expand-Archive -LiteralPath $compilerZip -DestinationPath $compilerRoot -ErrorAction Stop
  Expand-Archive -LiteralPath $baselineZip -DestinationPath $baselineRoot -ErrorAction Stop
  Expand-Archive -LiteralPath $effectiveZip -DestinationPath $effectiveRoot -ErrorAction Stop
  Expand-Archive -LiteralPath $codewareZip -DestinationPath $codewareRoot -ErrorAction Stop
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
  # The user's REDmod configuration compiles with a separate effective cache.
  # Treat the private upload as a *candidate*, not an authenticated vanilla
  # cache. A previously compiled .modded bundle is never silently reused as
  # source: SCC's official REDSCRIPT_SIGNATURE guard decides admissibility.
  $effectiveEntries=@(Get-ChildItem -LiteralPath $effectiveRoot -Recurse -File)
  $effectiveFiles=@($effectiveEntries | Where-Object Name -CEQ 'final.redscripts')
  if ($effectiveFiles.Count -ne 1 -or $effectiveEntries.Count -ne 1) {
    throw ('R6_EFFECTIVE_CACHE_ARCHIVE_INVALID: expected exactly one final.redscripts file, found='+$effectiveEntries.Count+' correctNamed='+$effectiveFiles.Count)
  }
  $effectiveFile=$effectiveFiles[0]
  if ($effectiveFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
    throw 'R6_EFFECTIVE_CACHE_REPARSE_POINT_REFUSED'
  }
  if ($effectiveFile.Length -lt 1048576) {
    throw 'R6_EFFECTIVE_CACHE_TOO_SMALL: cannot use as game cache input'
  }
  $effectiveSha=(Get-FileHash -Algorithm SHA256 -LiteralPath $effectiveFile.FullName).Hash
  Write-Host ('R6 effective-cache candidate admitted for independent compilation experiment: bytes='+$effectiveFile.Length+' differsFromVanilla='+($effectiveSha -ne $baseSha)+' originalVanillaSmokePassed=true')
  # RED4ext supplies Codeware's extra REDscript declarations at runtime.
  # Headless SCC does not invoke RED4ext; stage the ORIGINAL read-only
  # external scripts in both isolated full-stack scenarios, never in the
  # captured game source, generic preview, or deployable output.
  $cwCandidates=@(Get-ChildItem -LiteralPath $codewareRoot -Directory -Recurse |
    Where-Object { $_.Name -eq 'Scripts' -and $_.Parent.Name -eq 'Codeware' })
  if ($cwCandidates.Count -ne 1) {
    throw ('R6_CODEWARE_SCRIPTS_ROOT_INVALID: expected one Codeware/Scripts tree, found='+$cwCandidates.Count)
  }
  $cwScripts=$cwCandidates[0].FullName
  $cwFiles=@(Get-ChildItem -LiteralPath $cwScripts -Recurse -File -Filter '*.reds')
  if ($cwFiles.Count -eq 0) { throw 'R6_CODEWARE_SCRIPTS_MISSING: no actual REDscript declarations' }
  foreach($file in $cwFiles) {
    if ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
      throw 'R6_CODEWARE_REPARSE_POINT_REFUSED'
    }
  }
  $cwRelative=@($cwFiles | ForEach-Object { [System.IO.Path]::GetRelativePath($cwScripts,$_.FullName) })
  $cwOriginalHashes=@($cwFiles | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
  Write-Host ('R6 Codeware external declarations admitted: files='+$cwFiles.Count+' provenance=PRIVATE_USER_ARCHIVE identicalInBothStacks=true viaOfficialSccAdditionalScriptPath=true')
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
    $cwStage=Join-Path $root 'red4ext/plugins/Codeware/Scripts'
    if (Test-Path -LiteralPath $cwStage) { throw 'R6_CODEWARE_STAGE_PATH_COLLISION' }
    New-Item -ItemType Directory -Path $cwStage -Force | Out-Null
    Copy-Item -Path (Join-Path $cwScripts '*') -Destination $cwStage -Recurse -Force
    $cwStaged=@(Get-ChildItem -LiteralPath $cwStage -Recurse -File -Filter '*.reds')
    if ($cwStaged.Count -ne $cwFiles.Count) { throw 'R6_CODEWARE_STAGE_COUNT_MISMATCH' }
    for($i=0; $i -lt $cwFiles.Count; $i++) {
      $staged=Join-Path $cwStage $cwRelative[$i]
      if (!(Test-Path -LiteralPath $staged -PathType Leaf) -or
          (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $cwOriginalHashes[$i]) {
        throw 'R6_CODEWARE_STAGE_INTEGRITY_MISMATCH'
      }
    }
    Copy-Item -LiteralPath $effectiveFile.FullName -Destination $cache -Force
    if ((Get-FileHash -LiteralPath $cache -Algorithm SHA256).Hash -ne $effectiveSha) {
      throw 'R6 source compilation input cache differs from user supplied effective cache.'
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
      throw 'R6 isolated base script source count changed.'
    }
    if (@(Get-ChildItem -LiteralPath $cwStage -Recurse -File -Filter '*.reds').Count -ne $cwFiles.Count) {
      throw 'R6 external Codeware script path is incomplete.'
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
    if ($result.Length -lt 1048576 -or $sha -eq $effectiveSha -or (Get-FileHash -LiteralPath $cache -Algorithm SHA256).Hash -ne $effectiveSha) {
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
      'inputCache=USER_EFFECTIVE_CANDIDATE_NOT_VANILLA ' +
      'gameplayValidated=false deployable=false')
}
finally {
  if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop
  }
}
$global:LASTEXITCODE=0
