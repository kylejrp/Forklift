# -------------------------
# eval.ps1 (header)
# -------------------------
[CmdletBinding()]
param(
  [string]$MainRef = "origin/main",
  [string]$Configuration = "Release",
  [ValidateSet("quick", "ci", "deep")]
  [string]$Preset = "quick",
  [string]$Suite,
  [int]$Depth,
  [int]$Repeat,
  [int]$Warmup,
  [int]$Threads = 0,
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
  # Re-run this script under pwsh, passing through all args
  & $pwsh -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @args
  exit $LASTEXITCODE
}

$ErrorActionPreference = "Stop"
$VerbosePreference = if ($PSBoundParameters.ContainsKey('Verbose')) { 'Continue' } else { 'SilentlyContinue' }
$script:ExitCode = 0

function Show-StageProgress([string]$activity, [string]$status, [int]$percent) {
  # Write-Progress collapses automatically in PS7; keep it lightweight
  Write-Progress -Activity $activity -Status $status -PercentComplete $percent
}

function Invoke-Quiet {
  param(
    [scriptblock]$Command,
    [string]$LogPath
  )
  $out = & $Command 2>&1
  $out | Out-File $LogPath -Encoding utf8
  if ($VerbosePreference -eq 'Continue') { $out }
}

function Colorize([double]$percent) {
  if ($null -eq $percent) { return @{Color = 'Yellow'; Tag = ' ? ' } }
  if ($percent -ge 0.5) { return @{Color = 'Green'; Tag = '↑ ' } }
  if ($percent -le -0.5) { return @{Color = 'Red'; Tag = '↓ ' } }
  return @{Color = 'Yellow'; Tag = '→ ' }
}

function Bar([double]$percent) {
  # ASCII bar from -15%..+15%, 31 columns, center at zero
  $span = 15.0
  $p = [math]::Max(-$span, [math]::Min($span, $percent))
  $cols = 31
  $mid = [int][math]::Floor($cols / 2)
  $pos = $mid + [int][math]::Round(($p / $span) * $mid)

  $chars = New-Object char[] $cols
  for ($i = 0; $i -lt $cols; $i++) { $chars[$i] = ' ' }
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
    [string]$BaseString,
    [string]$CandidateString,
    [double]$percent
  )
  $colorInfo = Colorize $percent
  $bar = Bar $percent
  $percentString = "{0,7:N3}%" -f $percent
  $line = "{0,-14} {1,14} → {2,14}   {3}  {4}" -f $Name, $BaseString, $CandidateString, $percentString, $bar
  Write-Host $line -ForegroundColor $colorInfo.Color
}

# --- Preset → default mappings (you can tweak later)
switch ($Preset) {
  "quick" {
    if (-not $Suite) { $Suite = "minimal" }
    if (-not $Depth) { $Depth = 3 }
    if (-not $Repeat) { $Repeat = 1 }
    if (-not $Warmup) { $Warmup = 0 }
  }
  "ci" {
    if (-not $Suite) { $Suite = "minimal" }
    if (-not $Depth) { $Depth = 4 }
    if (-not $Repeat) { $Repeat = 3 }
    if (-not $Warmup) { $Warmup = 1 }
  }
  "deep" {
    if (-not $Suite) { $Suite = "fast" }
    if (-not $Depth) { $Depth = 5 }
    if (-not $Repeat) { $Repeat = 5 }
    if (-not $Warmup) { $Warmup = 1 }
  }
}

function Remove-WorktreeArtifact {
  [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
  param([switch]$Force)

  if ($KeepWorktree) { return }

  $targets = @()
  if (Test-Path $baseWorktree) { $targets += @{ Path = $baseWorktree; Log = "worktree-remove.log" } }
  if ($candidateMode -eq "REF" -and (Test-Path $candidateWorktree)) {
    $targets += @{ Path = $candidateWorktree; Log = "worktree-remove-candidate.log" }
  }
  if ($BenchmarkReference) {
    $benchCommit = (git rev-parse $BenchmarkReference).Trim()
    if ($benchCommit -ne $baseCommit) {
      $benchmarkWorktree = Join-Path $evaluationRoot "benchref"
      if (Test-Path $benchmarkWorktree) {
        $targets += @{ Path = $benchmarkWorktree; Log = "worktree-remove-benchref.log" }
      }
    }
  }

  $forceArg = if ($Force) { '--force' } else { '' }  # <-- now used

  foreach ($t in $targets) {
    if ($PSCmdlet.ShouldProcess($t.Path, "git worktree remove $forceArg --quiet")) {
      Invoke-Quiet -Command { git worktree remove $forceArg --quiet $($t.Path) } `
        -LogPath (Join-Path $logsRoot $t.Log)
    }
  }
}

Write-Host "Preset:    $Preset  Suite=$Suite Depth=$Depth Repeat=$Repeat Warmup=$Warmup Threads=$Threads"

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

function ShortHash([string]$hash) { if ($hash.Length -ge 7) { $hash.Substring(0, 7) } else { $hash } }
function Get-PercentageChange([double]$originalValue, [double]$newValue) {
  if ($originalValue -eq 0) { return $null }
  [math]::Round((($newValue - $originalValue) / $originalValue) * 100.0, 3)
}

# --- Layout
$repo = Resolve-GitRoot
Set-Location $repo

$evaluationRoot = Join-Path $repo ".eval"
$outRoot = Join-Path $evaluationRoot "out"
$logsRoot = Join-Path $evaluationRoot "logs"
$baseWorktree = Join-Path $evaluationRoot "baseline"
$benchmarkOut = Join-Path $outRoot "baseline-bench"
$baseCoreOut = Join-Path $outRoot "baseline-core"
$candidateWorktree = Join-Path $evaluationRoot "candidate"
$candidateCoreOut = Join-Path $outRoot "candidate-core"
$runA = Join-Path $outRoot "run-baseline"
$runB = Join-Path $outRoot "run-candidate"
New-Directory $evaluationRoot; New-Directory $outRoot; New-Directory $logsRoot

# --- Identify commits
try { $baseCommit = (git rev-parse $MainRef).Trim() } catch { $baseCommit = (git rev-parse HEAD).Trim() }
$baseShort = ShortHash $baseCommit
$baseSubject = (git show -s --format=%s $baseCommit).Trim()

# Candidate
if ([string]::IsNullOrWhiteSpace($CandidateRef)) {
  $candidateMode = "WORKTREE"
  $candidateLabel = "WORKTREE"
  $candidateCommit = $null
  $candidateSubject = "working tree"
}
else {
  $candidateMode = "REF"
  $candidateCommit = (git rev-parse $CandidateRef).Trim()
  $candidateSubject = (git show -s --format=%s $candidateCommit).Trim()
  $candidateLabel = ShortHash $candidateCommit
}

Write-Host ("Baseline:  {0} — {1}" -f $baseCommit, $baseSubject)
if ($candidateMode -eq "REF") {
  Write-Host ("Candidate: {0} — {1}" -f $candidateCommit, $candidateSubject)
}
else {
  Write-Host "Candidate: WORKTREE (uncommitted)"
}


# --- Baseline worktree
Show-StageProgress -Activity "Setup" -Status "Preparing baseline worktree" -Percent 10
if (Test-Path $baseWorktree) {
  $existing = (git -C $baseWorktree rev-parse HEAD).Trim()
  if ($existing -ne $baseCommit) {
    Invoke-Quiet -Command { git worktree remove --force --quiet $baseWorktree } -LogPath (Join-Path $logsRoot "worktree-remove.log")
    Invoke-Quiet -Command { git worktree add --detach --quiet $baseWorktree $baseCommit } -LogPath (Join-Path $logsRoot "worktree-add.log")
  }
}
else {
  Invoke-Quiet -Command { git worktree add --detach --quiet $baseWorktree $baseCommit } -LogPath (Join-Path $logsRoot "worktree-add.log")
}

# --- Candidate worktree if comparing a ref
if ($candidateMode -eq "REF") {
  Show-StageProgress -Activity "Setup" -Status "Preparing candidate worktree" -Percent 15
  if (Test-Path $candidateWorktree) {
    $existingCandidate = (git -C $candidateWorktree rev-parse HEAD).Trim()
    if ($existingCandidate -ne $candidateCommit) {
      Invoke-Quiet -Command { git worktree remove --force --quiet $candidateWorktree } -LogPath (Join-Path $logsRoot "worktree-remove-candidate.log")
      Invoke-Quiet -Command { git worktree add --detach --quiet $candidateWorktree $candidateCommit } -LogPath (Join-Path $logsRoot "worktree-add-candidate.log")
    }
  }
  else {
    Invoke-Quiet -Command { git worktree add --detach --quiet $candidateWorktree $candidateCommit } -LogPath (Join-Path $logsRoot "worktree-add-candidate.log")
  }
}



# --- Build flags
$msbuildFlags = @(
  "-c", $Configuration,
  "/nologo", "/clp:Summary",
  "/p:Deterministic=true",
  "/p:ContinuousIntegrationBuild=true",
  "/m"
)

# --- Build baseline (Core + Benchmark) from clean worktree
New-Directory $baseCoreOut; New-Directory $benchmarkOut

Show-StageProgress -Activity "Build" -Status "Baseline Forklift.Core" -Percent 25
Invoke-Quiet -Command { dotnet build (Join-Path $baseWorktree "Forklift.Core/Forklift.Core.csproj") @msbuildFlags "/p:OutputPath=$baseCoreOut" } -LogPath (Join-Path $logsRoot "build-baseline-core.log")

# --- Build Forklift.Benchmark (same bits for both runs)
Show-StageProgress -Activity "Build" -Status "Benchmark app" -Percent 40
if ([string]::IsNullOrWhiteSpace($BenchmarkReference)) {
  # default: build from the working tree (your current behaviour)
  Invoke-Quiet -Command { dotnet build (Join-Path $repo "Forklift.Benchmark/Forklift.Benchmark.csproj") @msbuildFlags "/p:OutputPath=$benchmarkOut" } -LogPath (Join-Path $logsRoot "build-bench.log")
}
else {
  # build from a specific ref (use baseline Worktree if it matches; otherwise make a temp Worktree)
  $benchCommit = (git rev-parse $BenchmarkReference).Trim()
  $benchmarkWorktree = $baseWorktree
  if ($benchCommit -ne $baseCommit) {
    $benchmarkWorktree = Join-Path $evaluationRoot "benchref"
    if (Test-Path $benchmarkWorktree) {
      $existingBench = (git -C $benchmarkWorktree rev-parse HEAD).Trim()
      if ($existingBench -ne $benchCommit) {
        Invoke-Quiet -Command { git worktree remove --force --quiet $benchmarkWorktree } -LogPath (Join-Path $logsRoot "worktree-remove-benchref.log")
        Invoke-Quiet -Command { git worktree add --detach --quiet $benchmarkWorktree $benchCommit } -LogPath (Join-Path $logsRoot "worktree-add-benchref.log")
      }
    }
    else {
      Invoke-Quiet -Command { git worktree add --detach --quiet $benchmarkWorktree $benchCommit } -LogPath (Join-Path $logsRoot "worktree-add-benchref.log")
    }
  }
  Invoke-Quiet -Command { dotnet build (Join-Path $benchmarkWorktree "Forklift.Benchmark/Forklift.Benchmark.csproj") @msbuildFlags "/p:OutputPath=$benchmarkOut" } -LogPath (Join-Path $logsRoot "build-bench.log")
}


# --- Build candidate Forklift.Core (working tree OR candidate worktree)
New-Directory $candidateCoreOut
Show-StageProgress -Activity "Build" -Status "Candidate Forklift.Core" -Percent 55
if ($candidateMode -eq "REF") {
  Invoke-Quiet -Command { dotnet build (Join-Path $candidateWorktree "Forklift.Core/Forklift.Core.csproj") @msbuildFlags "/p:OutputPath=$candidateCoreOut" } -LogPath (Join-Path $logsRoot "build-candidate-core.log")
}
else {
  Invoke-Quiet -Command { dotnet build (Join-Path $repo "Forklift.Core/Forklift.Core.csproj") @msbuildFlags "/p:OutputPath=$candidateCoreOut" } -LogPath (Join-Path $logsRoot "build-candidate-core.log")
}

# --- Prepare run folders using the SAME benchmark bits; swap Core dll
if (Test-Path $runA) { Remove-Item $runA -Recurse -Force }
if (Test-Path $runB) { Remove-Item $runB -Recurse -Force }
New-Directory $runA; New-Directory $runB
Copy-Item -Path (Join-Path $benchmarkOut '*') -Destination $runA -Recurse -Force
Copy-Item -Path (Join-Path $benchmarkOut '*') -Destination $runB -Recurse -Force

$baseCoreDll = Get-ChildItem -Path $baseCoreOut -Recurse -Filter "Forklift.Core.dll" | Select-Object -First 1
$candidateCoreDll = Get-ChildItem -Path $candidateCoreOut -Recurse -Filter "Forklift.Core.dll" | Select-Object -First 1
if (-not $baseCoreDll) { throw "Baseline Forklift.Core.dll not found." }
if (-not $candidateCoreDll) { throw "Candidate Forklift.Core.dll not found." }

Copy-Item $baseCoreDll.FullName (Join-Path $runA "Forklift.Core.dll") -Force
Copy-Item $candidateCoreDll.FullName (Join-Path $runB "Forklift.Core.dll") -Force

# Keep hash-stamped copies for provenance
Copy-Item $baseCoreDll.FullName (Join-Path $runA "Forklift.Core.$baseShort.dll") -Force
Copy-Item $candidateCoreDll.FullName (Join-Path $runB "Forklift.Core.$candidateLabel.dll") -Force

# --- Helper: run benchmark with JSON and return parsed object
function Invoke-BenchJson([string]$directory, [string]$label) {
  Push-Location $directory
  try {
    $benchDll = Get-ChildItem -Filter "Forklift.Benchmark.dll" | Select-Object -First 1
    if (-not $benchDll) { throw "Forklift.Benchmark.dll not found in $directory" }

    $jsonPath = Join-Path $logsRoot "bench-$label.json"
    $argsList = @("--preset", $Preset, "--suite", $Suite, "--depth", $Depth, "--repeat", $Repeat, "--warmup", $Warmup, "--json", "--out", $jsonPath)
    if ($Preset -eq "quick") { $argsList += "--skipCorrectness" }
    if ($HighPriority) { $argsList += "--highPriority" }
    if ($KeepHistory) { $argsList += "--keepHistory" }
    if ($Threads -gt 0) { $argsList += @("--threads", $Threads) }

    $logPath = Join-Path $logsRoot "run-$label.log"
    $Command = @("dotnet", $benchDll.FullName, "--") + $argsList
    ($Command -join " ") | Out-File $logPath

    # Execute
    & dotnet $benchDll.FullName -- @argsList 2>&1 | Tee-Object -FilePath $logPath -Encoding utf8 | Out-Null

    if (-not (Test-Path $jsonPath)) { throw "Expected JSON output not found: $jsonPath" }
    $obj = Get-Content $jsonPath -Raw | ConvertFrom-Json
    return [pscustomobject]@{
      Label          = $label
      Raw            = $obj
      TotalNodes     = [int64]$obj.TotalNodes
      TotalElapsedMs = [double]$obj.TotalElapsedMs
      AggregateNps   = [double]$obj.AggregateNps
    }
  }
  finally { Pop-Location }
}

Show-StageProgress -Activity "Run" -Status "Baseline benchmark" -Percent 70
$base = Invoke-BenchJson -directory $runA -label "baseline"

Show-StageProgress -Activity "Run" -Status "Candidate benchmark" -Percent 85
$candidate = Invoke-BenchJson -directory $runB -label "candidate"

# --- Compute deltas (Aggregate over the suite)
Show-StageProgress -Activity "Summarize" -Status "Computing deltas" -Percent 95
# keep a single set of these
$nodesDeltaPercent = Get-PercentageChange -originalValue $base.TotalNodes -newValue $candidate.TotalNodes
$timeDeltaPercent = Get-PercentageChange -originalValue $base.TotalElapsedMs -newValue $candidate.TotalElapsedMs
$npsDeltaPercent = Get-PercentageChange -originalValue $base.AggregateNps -newValue $candidate.AggregateNps

# --- Persist results
Write-Host ""

# Pretty console deltas (quiet, aligned, rounded)
PrintDeltaLine -Name "Nodes"       -BaseString ($base.TotalNodes.ToString("N0")) `
  -CandidateString ($candidate.TotalNodes.ToString("N0")) -percent $nodesDeltaPercent

PrintDeltaLine -Name "Elapsed (ms)" -BaseString ($base.TotalElapsedMs.ToString("N2")) `
  -CandidateString ($candidate.TotalElapsedMs.ToString("N2")) -percent $timeDeltaPercent

PrintDeltaLine -Name "NPS" -BaseString ([math]::Round($base.AggregateNps, 0).ToString("N0")) `
  -CandidateString ([math]::Round($candidate.AggregateNps, 0).ToString("N0")) -percent $npsDeltaPercent

Write-Progress -Activity "Done" -Completed

# Also keep the Markdown + JSON artifacts
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$jsonPath = Join-Path $evaluationRoot "result-$stamp.json"
$markdownPath = Join-Path $evaluationRoot "result-$stamp.md"

$jsonOut = @{
  BaselineCommit   = $baseCommit
  CandidateCommit  = $candidateLabel
  Suite            = $Suite
  Depth            = $Depth
  Repeat           = $Repeat
  Warmup           = $Warmup
  Threads          = if ($Threads -gt 0) { $Threads } else { $null }
  KeepHistory      = [bool]$KeepHistory
  HighPriority     = [bool]$HighPriority
  BaselineResults  = $base.Raw
  CandidateResults = $candidate.Raw
  Deltas           = @{
    NodesPercent   = $nodesDeltaPercent
    ElapsedPercent = $timeDeltaPercent
    NpsPercent     = $npsDeltaPercent
  }
  TimestampUtc     = (Get-Date).ToUniversalTime().ToString("o")
}
$jsonOut | ConvertTo-Json -Depth 6 | Out-File $jsonPath -Encoding utf8

$markdown = @"
# Forklift Perf A/B — $stamp

**Baseline**:  `$baseCommit`
**Candidate**: `$(if ($candidateMode -eq 'REF') { $candidateLabel } else { 'WORKTREE' })`

**Suite**: `$Suite`  **Depth**: `$Depth`  **Repeat**: `$Repeat`  **Warmup**: `$Warmup`  **Threads**: $(if ($Threads -gt 0) { "$Threads" } else { "default" })

| Metric       | Baseline | Candidate | Δ % |
|-------------:|---------:|----------:|----:|
| Nodes        | $($base.TotalNodes) | $($candidate.TotalNodes) | $nodesDeltaPercent |
| Elapsed (ms) | $([math]::Round($base.TotalElapsedMs,2)) | $([math]::Round($candidate.TotalElapsedMs,2)) | $timeDeltaPercent |
| NPS          | $([math]::Round($base.AggregateNps,0)) | $([math]::Round($candidate.AggregateNps,0)) | $npsDeltaPercent |

Per-position medians:
| Position | Nodes (B → C)  | ms (B → C) | NPS (B → C) |
|---------:|---------------:|-----------:|------------:|
"@

# rows
foreach ($baseResults in $base.Raw.Results) {
  $name = $baseResults.Name
  $candidateResults = $candidate.Raw.Results | Where-Object Name -eq $name
  $markdown += ("| {0} | {1} → {2} | {3} → {4} | {5} → {6} |`n" -f
    $name,
    $baseResults.NodesMedian, $candidateResults.NodesMedian,
    ([math]::Round($baseResults.ElapsedMsMedian, 2)), ([math]::Round($candidateResults.ElapsedMsMedian, 2)),
    ([math]::Round($baseResults.NpsMedian, 0)), ([math]::Round($candidateResults.NpsMedian, 0)))
}


$markdown += @"

Logs & artifacts:
- JSON: `$jsonPath`
- Baseline build logs: `.eval/logs/build-baseline-core.log`
- Benchmark build logs: `.eval/logs/build-bench.log`
- Candidate build logs: `.eval/logs/build-candidate-core.log`
- Runs: `.eval/logs/run-baseline.log`, `.eval/logs/run-candidate.log`
"@
$markdown | Out-File $markdownPath -Encoding utf8

Write-Host ""
Write-Host "Saved:" -ForegroundColor DarkGray
Write-Host "  $jsonPath"
Write-Host "  $markdownPath"
Write-Host ""

# --- Perf gate (optional)
if ($FailIfNodesPerSecondDropsPercentage -ne 0.0 -and $npsDeltaPercent -lt $FailIfNodesPerSecondDropsPercentage) {
  Write-Host ("FAIL: Aggregate NPS delta {0:N3}% < threshold {1:N3}%." -f $npsDeltaPercent, $FailIfNodesPerSecondDropsPercentage) -ForegroundColor Red
  $script:ExitCode = 1
}
else {
  Write-Host ("OK: Aggregate NPS delta {0:N3}% ≥ threshold {1:N3}%." -f $npsDeltaPercent, $FailIfNodesPerSecondDropsPercentage) -ForegroundColor Green
}

# --- Cleanup
Remove-WorktreeArtifact -Force:$true
exit $script:ExitCode