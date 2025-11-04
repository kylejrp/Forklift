# -------------------------
# eval.ps1  (no-cache, verbose variable names)
# -------------------------
[CmdletBinding()]
param(
  [string]$MainRef = "origin/main",
  [string]$Configuration = "Release",

  # Forward-only flags for the benchmark; null means "don't pass it"
  [ValidateSet("quick", "ci", "deep")]
  [string]$Preset,
  [string]$Suite,
  [int]$Depth,
  [int]$Repeat,
  [int]$Warmup,
  [int]$Threads,

  [switch]$HighPriority,
  [switch]$KeepHistory,
  [double]$FailIfNodesPerSecondDropsPercentage = 0.0,
  [switch]$KeepWorktree,
  [string]$CandidateRef,
  [string]$BenchmarkReference
)

# --- Bootstrap: always run under PowerShell 7+ (pwsh) ---
if ($PSVersionTable.PSEdition -ne 'Core') {
  $pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
  if (-not $pwsh) {
    Write-Error "This script requires PowerShell 7+. Install with: winget install Microsoft.PowerShell"
    exit 1
  }
  & $pwsh -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @args
  exit $LASTEXITCODE
}

$ErrorActionPreference = "Stop"
$VerbosePreference = if ($PSBoundParameters.ContainsKey('Verbose')) { 'Continue' } else { 'SilentlyContinue' }
$script:ExitCode = 0

function Show-StageProgress([string]$Activity, [string]$Status, [int]$Percent) {
  Write-Progress -Activity $Activity -Status $Status -PercentComplete $Percent
}

function Invoke-Quiet {
  param(
    [scriptblock]$Command,
    [string]$LogPath,
    [object[]]$ArgumentList
  )
  $output = & $Command @ArgumentList 2>&1
  $output | Out-File $LogPath -Encoding utf8
  if ($VerbosePreference -eq 'Continue') { $output }
}

function Colorize([double]$Percent) {
  if ($null -eq $Percent) { return @{ Color = 'Yellow'; Tag = ' ? ' } }
  if ($Percent -ge 0.5) { return @{ Color = 'Green'; Tag = '↑ ' } }
  if ($Percent -le -0.5) { return @{ Color = 'Red'; Tag = '↓ ' } }
  return @{ Color = 'Yellow'; Tag = '→ ' }
}

function Bar([double]$Percent) {
  $span = 15.0
  $clamped = [math]::Max(-$span, [math]::Min($span, $Percent))
  $columns = 31
  $mid = [int][math]::Floor($columns / 2)
  $pos = $mid + [int][math]::Round(($clamped / $span) * $mid)

  $chars = New-Object char[] $columns
  for ($i = 0; $i -lt $columns; $i++) { $chars[$i] = ' ' }
  $chars[$mid] = '|'
  if ($pos -gt $mid) {
    for ($i = $mid + 1; $i -le $pos; $i++) { $chars[$i] = '█' }
  }
  elseif ($pos -lt $mid) {
    for ($i = $pos; $i -lt $mid; $i++) { $chars[$i] = '█' }
  }
  -join $chars
}

function PrintDeltaLine {
  param(
    [string]$Name,
    [string]$BaselineString,
    [string]$CandidateString,
    [double]$Percent,
    [switch]$LowerIsBetter
  )
  $percentForColor = if ($LowerIsBetter) { -1 * $Percent } else { $Percent }
  $colorInfo = Colorize $percentForColor
  $bar = Bar $Percent
  $percentString = "{0,7:N3}%" -f $Percent
  $line = "{0,-14} {1,14} → {2,14}   {3}  {4}" -f $Name, $BaselineString, $CandidateString, $percentString, $bar
  Write-Host $line -ForegroundColor $colorInfo.Color
}

function Resolve-GitRoot {
  $root = git rev-parse --show-toplevel 2>$null
  if (-not $root) { throw "Not a git repository. Run this from within the repo root." }
  $root
}

function New-Directory {
  [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
  param([string]$Path)
  if (-not (Test-Path $Path)) {
    if ($PSCmdlet.ShouldProcess($Path, "New-Item -ItemType Directory")) {
      New-Item -ItemType Directory -Path $Path | Out-Null
    }
  }
}

function ShortHash([string]$Hash) { if ($Hash.Length -ge 7) { $Hash.Substring(0, 7) } else { $Hash } }
function Get-PercentageChange([double]$Original, [double]$New) {
  if ($Original -eq 0) { return $null }
  [math]::Round((($New - $Original) / $Original) * 100.0, 3)
}

function Remove-WorktreeArtifact {
  [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
  param([switch]$Force)

  if ($KeepWorktree) { return }

  $targets = @()
  if (Test-Path $baselineWorktree) { $targets += @{ Path = $baselineWorktree; Log = "worktree-remove-baseline.log" } }
  if ($candidateMode -eq "REF" -and (Test-Path $candidateWorktree)) {
    $targets += @{ Path = $candidateWorktree; Log = "worktree-remove-candidate.log" }
  }
  if ($BenchmarkReference -and (Test-Path $benchmarkWorktree)) {
    $targets += @{ Path = $benchmarkWorktree; Log = "worktree-remove-benchmark.log" }
  }

  $forceArg = if ($Force) { '--force' } else { '' }

  foreach ($t in $targets) {
    if ($PSCmdlet.ShouldProcess($t.Path, "git worktree remove $forceArg --quiet")) {
      Invoke-Quiet -Command {
        param($pathParam, $forceParam)
        git worktree remove $forceParam --quiet $pathParam
      } -LogPath (Join-Path $logsRoot $t.Log) -ArgumentList $t.Path, $forceArg
    }
  }
}

# --- Layout and repo info
$repoRoot = Resolve-GitRoot
Set-Location $repoRoot

$evaluationRoot = Join-Path $repoRoot ".eval"
$outRoot = Join-Path $evaluationRoot "out"
$logsRoot = Join-Path $evaluationRoot "logs"
$baselineWorktree = Join-Path $evaluationRoot "baseline"
$candidateWorktree = Join-Path $evaluationRoot "candidate"
$benchmarkWorktree = $null  # may be created if BenchmarkReference differs from baseline
$runBaselineDirectory = Join-Path $outRoot "run-baseline"
$runCandidateDirectory = Join-Path $outRoot "run-candidate"

New-Directory $evaluationRoot
New-Directory $outRoot
New-Directory $logsRoot

# --- Identify commits
try { $baselineCommit = (git rev-parse $MainRef).Trim() } catch { $baselineCommit = (git rev-parse HEAD).Trim() }
$baselineShortHash = ShortHash $baselineCommit
$baselineSubject = (git show -s --format=%s $baselineCommit).Trim()

if ([string]::IsNullOrWhiteSpace($CandidateRef)) {
  $candidateMode = "WORKTREE"
  $candidateCommit = $null
  $candidateLabel = "WORKTREE"
  $candidateSubject = "working tree"
}
else {
  $candidateMode = "REF"
  $candidateCommit = (git rev-parse $CandidateRef).Trim()
  $candidateLabel = ShortHash $candidateCommit
  $candidateSubject = (git show -s --format=%s $candidateCommit).Trim()
}

Write-Host ("Baseline:  {0} — {1}" -f $baselineCommit, $baselineSubject)
if ($candidateMode -eq "REF") {
  Write-Host ("Candidate: {0} — {1}" -f $candidateCommit, $candidateSubject)
}
else {
  Write-Host "Candidate: WORKTREE (uncommitted)"
}

# --- Prepare worktrees (always rebuild; worktrees are for reproducible sources)
Show-StageProgress -Activity "Setup" -Status "Preparing baseline worktree" -Percent 10
if (Test-Path $baselineWorktree) {
  $existingBaselineHead = (git -C $baselineWorktree rev-parse HEAD).Trim()
  if ($existingBaselineHead -ne $baselineCommit) {
    Invoke-Quiet -Command { param($p) git worktree remove --force --quiet $p } `
      -LogPath (Join-Path $logsRoot "worktree-remove-baseline.log") -ArgumentList $baselineWorktree
    Invoke-Quiet -Command { param($p, $c) git worktree add --detach --quiet $p $c } `
      -LogPath (Join-Path $logsRoot "worktree-add-baseline.log") -ArgumentList $baselineWorktree, $baselineCommit
  }
}
else {
  Invoke-Quiet -Command { param($p, $c) git worktree add --detach --quiet $p $c } `
    -LogPath (Join-Path $logsRoot "worktree-add-baseline.log") -ArgumentList $baselineWorktree, $baselineCommit
}

if ($candidateMode -eq "REF") {
  Show-StageProgress -Activity "Setup" -Status "Preparing candidate worktree" -Percent 15
  if (Test-Path $candidateWorktree) {
    $existingCandidateHead = (git -C $candidateWorktree rev-parse HEAD).Trim()
    if ($existingCandidateHead -ne $candidateCommit) {
      Invoke-Quiet -Command { param($p) git worktree remove --force --quiet $p } `
        -LogPath (Join-Path $logsRoot "worktree-remove-candidate.log") -ArgumentList $candidateWorktree
      Invoke-Quiet -Command { param($p, $c) git worktree add --detach --quiet $p $c } `
        -LogPath (Join-Path $logsRoot "worktree-add-candidate.log") -ArgumentList $candidateWorktree, $candidateCommit
    }
  }
  else {
    Invoke-Quiet -Command { param($p, $c) git worktree add --detach --quiet $p $c } `
      -LogPath (Join-Path $logsRoot "worktree-add-candidate.log") -ArgumentList $candidateWorktree, $candidateCommit
  }
}

# --- Build flags (always rebuild; no caches)
$msbuildFlags = @(
  "-c", $Configuration,
  "/nologo", "/clp:Summary",
  "/t:Rebuild",
  "/p:Deterministic=true",
  "/p:ContinuousIntegrationBuild=true",
  "/m"
)

# --- Output directories for fresh builds
$baselineCoreOutputDirectory = Join-Path $outRoot "baseline-core"
$candidateCoreOutputDirectory = Join-Path $outRoot "candidate-core"
$benchmarkBitsDirectory = Join-Path $outRoot "bench"

# Clean and recreate output directories
if (Test-Path $baselineCoreOutputDirectory) { Remove-Item $baselineCoreOutputDirectory -Recurse -Force }
if (Test-Path $candidateCoreOutputDirectory) { Remove-Item $candidateCoreOutputDirectory -Recurse -Force }
if (Test-Path $benchmarkBitsDirectory) { Remove-Item $benchmarkBitsDirectory -Recurse -Force }
if (Test-Path $runBaselineDirectory) { Remove-Item $runBaselineDirectory -Recurse -Force }
if (Test-Path $runCandidateDirectory) { Remove-Item $runCandidateDirectory -Recurse -Force }
New-Directory $baselineCoreOutputDirectory
New-Directory $candidateCoreOutputDirectory
New-Directory $benchmarkBitsDirectory
New-Directory $runBaselineDirectory
New-Directory $runCandidateDirectory

# --- Build Forklift.Core (baseline)
Show-StageProgress -Activity "Build" -Status "Baseline Forklift.Core (rebuild)" -Percent 25
Invoke-Quiet -Command {
  param($worktreePath, $flags, $outDir)
  dotnet build (Join-Path $worktreePath "Forklift.Core/Forklift.Core.csproj") @flags "/p:OutputPath=$outDir"
} -LogPath (Join-Path $logsRoot "build-baseline-core.log") -ArgumentList $baselineWorktree, $msbuildFlags, $baselineCoreOutputDirectory

$baselineCoreDllPath = Join-Path $baselineCoreOutputDirectory "Forklift.Core.dll"
if (-not (Test-Path $baselineCoreDllPath)) { throw "Baseline Core build produced no Forklift.Core.dll at $baselineCoreDllPath" }

# --- Build Forklift.Core (candidate)
Show-StageProgress -Activity "Build" -Status "Candidate Forklift.Core (rebuild)" -Percent 35
if ($candidateMode -eq "REF") {
  Invoke-Quiet -Command {
    param($worktreePath, $flags, $outDir)
    dotnet build (Join-Path $worktreePath "Forklift.Core/Forklift.Core.csproj") @flags "/p:OutputPath=$outDir"
  } -LogPath (Join-Path $logsRoot "build-candidate-core.log") -ArgumentList $candidateWorktree, $msbuildFlags, $candidateCoreOutputDirectory
}
else {
  Invoke-Quiet -Command {
    param($repoPath, $flags, $outDir)
    dotnet build (Join-Path $repoPath "Forklift.Core/Forklift.Core.csproj") @flags "/p:OutputPath=$outDir"
  } -LogPath (Join-Path $logsRoot "build-candidate-core.log") -ArgumentList $repoRoot, $msbuildFlags, $candidateCoreOutputDirectory
}

$candidateCoreDllPath = Join-Path $candidateCoreOutputDirectory "Forklift.Core.dll"
if (-not (Test-Path $candidateCoreDllPath)) { throw "Candidate Core build produced no Forklift.Core.dll at $candidateCoreDllPath" }

# --- Build Forklift.Benchmark bits (single rebuild)
Show-StageProgress -Activity "Build" -Status "Benchmark app (rebuild)" -Percent 45
$benchmarkBuildSourcePath = $repoRoot
if ($BenchmarkReference) {
  $benchmarkReferenceCommit = (git rev-parse $BenchmarkReference).Trim()
  if ($benchmarkReferenceCommit -ne $baselineCommit) {
    $benchmarkWorktree = Join-Path $evaluationRoot "benchref"
    if (Test-Path $benchmarkWorktree) {
      $existingBenchHead = (git -C $benchmarkWorktree rev-parse HEAD).Trim()
      if ($existingBenchHead -ne $benchmarkReferenceCommit) {
        Invoke-Quiet -Command { param($p) git worktree remove --force --quiet $p } `
          -LogPath (Join-Path $logsRoot "worktree-remove-benchmark.log") -ArgumentList $benchmarkWorktree
        Invoke-Quiet -Command { param($p, $c) git worktree add --detach --quiet $p $c } `
          -LogPath (Join-Path $logsRoot "worktree-add-benchmark.log") -ArgumentList $benchmarkWorktree, $benchmarkReferenceCommit
      }
    }
    else {
      Invoke-Quiet -Command { param($p, $c) git worktree add --detach --quiet $p $c } `
        -LogPath (Join-Path $logsRoot "worktree-add-benchmark.log") -ArgumentList $benchmarkWorktree, $benchmarkReferenceCommit
    }
    $benchmarkBuildSourcePath = $benchmarkWorktree
  }
}

Invoke-Quiet -Command {
  param($sourcePath, $flags, $outDir)
  dotnet build (Join-Path $sourcePath "Forklift.Benchmark/Forklift.Benchmark.csproj") @flags "/p:OutputPath=$outDir"
} -LogPath (Join-Path $logsRoot "build-bench.log") -ArgumentList $benchmarkBuildSourcePath, $msbuildFlags, $benchmarkBitsDirectory

$benchmarkDllPath = Join-Path $benchmarkBitsDirectory "Forklift.Benchmark.dll"
if (-not (Test-Path $benchmarkDllPath)) { throw "Benchmark build produced no Forklift.Benchmark.dll at $benchmarkDllPath" }

# --- Prepare run directories and swap Core DLLs
Copy-Item -Path (Join-Path $benchmarkBitsDirectory '*') -Destination $runBaselineDirectory -Recurse -Force
Copy-Item -Path (Join-Path $benchmarkBitsDirectory '*') -Destination $runCandidateDirectory -Recurse -Force

Copy-Item $baselineCoreDllPath  (Join-Path $runBaselineDirectory  "Forklift.Core.dll") -Force
Copy-Item $candidateCoreDllPath (Join-Path $runCandidateDirectory "Forklift.Core.dll") -Force

# Also keep hash-stamped copies for provenance
Copy-Item $baselineCoreDllPath  (Join-Path $runBaselineDirectory  ("Forklift.Core.{0}.dll" -f $baselineShortHash)) -Force
Copy-Item $candidateCoreDllPath (Join-Path $runCandidateDirectory ("Forklift.Core.{0}.dll" -f ($candidateMode -eq "REF" ? $candidateLabel : "WORKTREE"))) -Force

function New-BenchArguments {
  # Always tell the benchmark a preset; default to 'quick' so Program.cs owns defaults.
  $effectivePreset = if ($PSBoundParameters.ContainsKey('Preset') -and $Preset) { $Preset } else { 'quick' }
  $arguments = @("--json", "--preset", $effectivePreset)
  if ($PSBoundParameters.ContainsKey('Suite')) { $arguments += @("--suite", $Suite) }
  if ($PSBoundParameters.ContainsKey('Depth')) { $arguments += @("--depth", $Depth) }
  if ($PSBoundParameters.ContainsKey('Repeat')) { $arguments += @("--repeat", $Repeat) }
  if ($PSBoundParameters.ContainsKey('Warmup')) { $arguments += @("--warmup", $Warmup) }
  if ($PSBoundParameters.ContainsKey('Threads') -and $Threads -gt 0) { $arguments += @("--threads", $Threads) }
  if ($HighPriority) { $arguments += "--highPriority" }
  if ($KeepHistory) { $arguments += "--keepHistory" }
  return $arguments
}

function Invoke-BenchJson([string]$WorkingDirectory, [string]$Label) {
  Push-Location $WorkingDirectory
  try {
    $benchDll = Get-ChildItem -Filter "Forklift.Benchmark.dll" | Select-Object -First 1
    if (-not $benchDll) { throw "Forklift.Benchmark.dll not found in $WorkingDirectory" }

    $jsonOutputPath = Join-Path $logsRoot "bench-$Label.json"
    $arguments = New-BenchArguments
    $arguments += @("--out", $jsonOutputPath)

    $runLogPath = Join-Path $logsRoot "run-$Label.log"
    $commandLine = @("dotnet", $benchDll.FullName, "--") + $arguments
    ($commandLine -join " ") | Out-File $runLogPath -Encoding utf8

    & dotnet $benchDll.FullName -- @arguments 2>&1 | Tee-Object -FilePath $runLogPath -Encoding utf8 | Out-Null

    if (-not (Test-Path $jsonOutputPath)) { throw "Expected JSON output not found: $jsonOutputPath" }
    $parsed = Get-Content $jsonOutputPath -Raw | ConvertFrom-Json
    return [pscustomobject]@{
      Label          = $Label
      Raw            = $parsed
      TotalNodes     = [int64]$parsed.TotalNodes
      TotalElapsedMs = [double]$parsed.TotalElapsedMs
      AggregateNps   = [double]$parsed.AggregateNps
    }
  }
  finally { Pop-Location }
}

# --- Randomize run order to reduce thermal bias
$swapOrder = (Get-Random -Maximum 2) -eq 1
if ($swapOrder) {
  Show-StageProgress -Activity "Run" -Status "Candidate benchmark" -Percent 70
  $candidateRun = Invoke-BenchJson -WorkingDirectory $runCandidateDirectory -Label "candidate"

  Show-StageProgress -Activity "Run" -Status "Baseline benchmark" -Percent 85
  $baselineRun = Invoke-BenchJson -WorkingDirectory $runBaselineDirectory -Label "baseline"
}
else {
  Show-StageProgress -Activity "Run" -Status "Baseline benchmark" -Percent 70
  $baselineRun = Invoke-BenchJson -WorkingDirectory $runBaselineDirectory -Label "baseline"

  Show-StageProgress -Activity "Run" -Status "Candidate benchmark" -Percent 85
  $candidateRun = Invoke-BenchJson -WorkingDirectory $runCandidateDirectory -Label "candidate"
}

Write-Host ""
Write-Host ("Suite={0} Depth={1} Repeat={2} Warmup={3} Threads={4}" -f $baselineRun.Raw.Suite, $baselineRun.Raw.Depth, $baselineRun.Raw.Repeat, $baselineRun.Raw.Warmup, ($baselineRun.Raw.Threads ?? "default"))

# --- Summarize
Show-StageProgress -Activity "Summarize" -Status "Computing deltas" -Percent 95

function Get-GeometricMean([double[]]$Values) {
  $sumLog = 0.0
  $n = 0
  foreach ($v in $Values) {
    if ($v -le 0) { continue }
    $sumLog += [math]::Log($v)
    $n++
  }
  if ($n -eq 0) { return $null }
  return [math]::Exp($sumLog / $n)
}

$nodesDeltaPercent = Get-PercentageChange -Original $baselineRun.TotalNodes     -New $candidateRun.TotalNodes
$timeDeltaPercent = Get-PercentageChange -Original $baselineRun.TotalElapsedMs -New $candidateRun.TotalElapsedMs

$ratioArray = @()
foreach ($baselinePosition in $baselineRun.Raw.Results) {
  $candidatePosition = $candidateRun.Raw.Results | Where-Object Name -eq $baselinePosition.Name
  if ($null -ne $candidatePosition -and $baselinePosition.NpsMedian -gt 0) {
    $ratioArray += ($candidatePosition.NpsMedian / $baselinePosition.NpsMedian)
  }
}
$geometricMeanRatio = Get-GeometricMean $ratioArray
$npsDeltaPercent = if ($null -ne $geometricMeanRatio) { [math]::Round((($geometricMeanRatio - 1.0) * 100.0), 3) } else { $null }

# --- Pretty console output
Write-Host ""
PrintDeltaLine -Name "Nodes" `
  -BaselineString ($baselineRun.TotalNodes.ToString("N0")) `
  -CandidateString ($candidateRun.TotalNodes.ToString("N0")) `
  -Percent $nodesDeltaPercent

PrintDeltaLine -Name "Elapsed (ms)" `
  -BaselineString ($baselineRun.TotalElapsedMs.ToString("N2")) `
  -CandidateString ($candidateRun.TotalElapsedMs.ToString("N2")) `
  -Percent $timeDeltaPercent `
  -LowerIsBetter

PrintDeltaLine -Name "NPS" `
  -BaselineString ([math]::Round($baselineRun.AggregateNps, 0).ToString("N0")) `
  -CandidateString ([math]::Round($candidateRun.AggregateNps, 0).ToString("N0")) `
  -Percent $npsDeltaPercent

Write-Progress -Activity "Done" -Completed

# --- Persist artifacts (JSON + Markdown)
$timestampStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$jsonResultPath = Join-Path $evaluationRoot "result-$timestampStamp.json"
$markdownResultPath = Join-Path $evaluationRoot "result-$timestampStamp.md"

$jsonEnvelope = @{
  BaselineCommit   = $baselineCommit
  CandidateCommit  = ($candidateMode -eq 'REF' ? $candidateLabel : 'WORKTREE')
  Suite            = $Suite
  Depth            = $Depth
  Repeat           = $Repeat
  Warmup           = $Warmup
  Threads          = if ($Threads -gt 0) { $Threads } else { $null }
  KeepHistory      = [bool]$KeepHistory
  HighPriority     = [bool]$HighPriority
  BaselineResults  = $baselineRun.Raw
  CandidateResults = $candidateRun.Raw
  Deltas           = @{
    NodesPercent   = $nodesDeltaPercent
    ElapsedPercent = $timeDeltaPercent
    NpsPercent     = $npsDeltaPercent
  }
  TimestampUtc     = (Get-Date).ToUniversalTime().ToString("o")
}
$jsonEnvelope | ConvertTo-Json -Depth 6 | Out-File $jsonResultPath -Encoding utf8

$markdown = @"
# Forklift Perf A/B — $timestampStamp

**Baseline**:  `$baselineCommit`
**Candidate**: `$(if ($candidateMode -eq 'REF') { $candidateLabel } else { 'WORKTREE' })`

**Suite**: `$( $baselineRun.Raw.Suite )`  **Depth**: `$( $baselineRun.Raw.Depth )`  **Repeat**: `$( $baselineRun.Raw.Repeat )`  **Warmup**: `$( $baselineRun.Raw.Warmup )`  **Threads**: $(if ($baselineRun.Raw.Threads) { "$($baselineRun.Raw.Threads)" } else { "default" })

| Metric       | Baseline | Candidate | Δ % |
|-------------:|---------:|----------:|----:|
| Nodes        | $($baselineRun.TotalNodes) | $($candidateRun.TotalNodes) | $nodesDeltaPercent |
| Elapsed (ms) | $([math]::Round($baselineRun.TotalElapsedMs,2)) | $([math]::Round($candidateRun.TotalElapsedMs,2)) | $timeDeltaPercent |
| NPS          | $([math]::Round($baselineRun.AggregateNps,0)) | $([math]::Round($candidateRun.AggregateNps,0)) | $npsDeltaPercent |

Per-position medians:
| Position | Nodes (B → C)  | ms (B → C) | NPS (B → C) |
|---------:|---------------:|-----------:|------------:|
"@

foreach ($baselinePosition in $baselineRun.Raw.Results) {
  $candidatePosition = $candidateRun.Raw.Results | Where-Object Name -eq $baselinePosition.Name
  $n2 = if ($candidatePosition) { $candidatePosition.NodesMedian } else { "n/a" }
  $ms2 = if ($candidatePosition) { [math]::Round($candidatePosition.ElapsedMsMedian, 2) } else { "n/a" }
  $nps2 = if ($candidatePosition) { [math]::Round($candidatePosition.NpsMedian, 0) } else { "n/a" }
  $markdown += ("| {0} | {1} → {2} | {3} → {4} | {5} → {6} |`n" -f
    $baselinePosition.Name,
    $baselinePosition.NodesMedian, $n2,
    ([math]::Round($baselinePosition.ElapsedMsMedian, 2)), $ms2,
    ([math]::Round($baselinePosition.NpsMedian, 0)), $nps2)
}

$markdown += @"

Logs & artifacts:
- JSON: `$jsonResultPath`
- Build logs: `.eval/logs/build-baseline-core.log`, `.eval/logs/build-candidate-core.log`, `.eval/logs/build-bench.log`
- Run logs: `.eval/logs/run-baseline.log`, `.eval/logs/run-candidate.log`
"@
$markdown | Out-File $markdownResultPath -Encoding utf8

Write-Host ""
Write-Host "Saved:" -ForegroundColor DarkGray
Write-Host "  $jsonResultPath"
Write-Host "  $markdownResultPath"
Write-Host ""

# --- Perf gate
$threshold = $FailIfNodesPerSecondDropsPercentage
if (-not $PSBoundParameters.ContainsKey('FailIfNodesPerSecondDropsPercentage')) {
  $threshold = 0.0
}

if ($npsDeltaPercent -lt $threshold) {
  Write-Host ("FAIL: Aggregate NPS delta {0:N3}% < threshold {1:N3}%." -f $npsDeltaPercent, $threshold) -ForegroundColor Red
  $script:ExitCode = 1
}
else {
  Write-Host ("OK: Aggregate NPS delta {0:N3}% ≥ threshold {1:N3}%." -f $npsDeltaPercent, $threshold) -ForegroundColor Green
}

# --- Cleanup worktrees unless requested
Remove-WorktreeArtifact -Force:$true
exit $script:ExitCode