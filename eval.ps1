param(
  [string]$MainRef = "origin/main",
  [string]$Configuration = "Release",

  # Quick/CI/Deep preset mapping
  [ValidateSet("quick","ci","deep")]
  [string]$Preset = "quick",

  # Advanced overrides (optional, mostly set by presets)
  [string]$Suite,
  [int]$Depth,
  [int]$Repeat,
  [int]$Warmup,
  [int]$Threads = 0,
  [switch]$HighPriority,
  [switch]$KeepHistory,

  [double]$FailIfNpsDropPct = 0.0,
  [switch]$KeepWorktree
)

$ErrorActionPreference = "Stop"

# --- Preset → default mappings (you can tweak later)
switch ($Preset) {
  "quick" {
    if (-not $Suite)  { $Suite  = "minimal" }
    if (-not $Depth)  { $Depth  = 3 }
    if (-not $Repeat) { $Repeat = 1 }
    if (-not $Warmup) { $Warmup = 0 }
    $SkipCorrectness = $true
  }
  "ci" {
    if (-not $Suite)  { $Suite  = "minimal" }
    if (-not $Depth)  { $Depth  = 4 }
    if (-not $Repeat) { $Repeat = 3 }
    if (-not $Warmup) { $Warmup = 1 }
  }
  "deep" {
    if (-not $Suite)  { $Suite  = "fast" }
    if (-not $Depth)  { $Depth  = 5 }
    if (-not $Repeat) { $Repeat = 5 }
    if (-not $Warmup) { $Warmup = 1 }
  }
}

Write-Host "Preset: $Preset  Suite=$Suite Depth=$Depth Repeat=$Repeat Warmup=$Warmup Threads=$Threads"

function Resolve-GitRoot { git rev-parse --show-toplevel }
function Ensure-Dir([string]$p){ if(-not(Test-Path $p)){ New-Item -ItemType Directory -Path $p | Out-Null } }
function ShortHash([string]$h){ if($h.Length -ge 7){ $h.Substring(0,7) } else { $h } }
function Pct([double]$a, [double]$b){ if ($a -eq 0) { return $null } [math]::Round((($b - $a)/$a)*100.0,3) }

# --- Layout
$repo = Resolve-GitRoot
Set-Location $repo

$evalRoot   = Join-Path $repo ".eval"
$outRoot    = Join-Path $evalRoot "out"
$logsRoot   = Join-Path $evalRoot "logs"
$baseWT     = Join-Path $evalRoot "baseline"
$benchOut   = Join-Path $outRoot "baseline-bench"
$baseCoreOut= Join-Path $outRoot "baseline-core"
$candCoreOut= Join-Path $outRoot "candidate-core"
$runA       = Join-Path $outRoot "run-baseline"
$runB       = Join-Path $outRoot "run-candidate"
Ensure-Dir $evalRoot; Ensure-Dir $outRoot; Ensure-Dir $logsRoot

# --- Identify commits
try { $baseCommit = (git rev-parse $MainRef).Trim() } catch { $baseCommit = (git rev-parse HEAD).Trim() }
$baseShort = ShortHash $baseCommit
$candidateLabel = "WORKTREE"

Write-Host "Baseline: $baseCommit"
Write-Host "Candidate: $candidateLabel (uncommitted)"

# --- Baseline worktree
if (Test-Path $baseWT) {
  $existing = (git -C $baseWT rev-parse HEAD).Trim()
  if ($existing -ne $baseCommit) {
    git worktree remove --force $baseWT
    git worktree add --detach $baseWT $baseCommit
  }
} else {
  git worktree add --detach $baseWT $baseCommit
}

# --- Build flags
$msbuildFlags = @(
  "-c", $Configuration,
  "/nologo", "/clp:Summary",
  "/p:Deterministic=true",
  "/p:ContinuousIntegrationBuild=true"
)

# --- Build baseline (Core + Benchmark) from clean worktree
Ensure-Dir $baseCoreOut; Ensure-Dir $benchOut

Write-Host "`n=== Build baseline Forklift.Core @$baseShort ==="
dotnet build (Join-Path $baseWT "Forklift.Core/Forklift.Core.csproj") @msbuildFlags "/p:OutputPath=$baseCoreOut" `
  | Tee-Object -FilePath (Join-Path $logsRoot "build-baseline-core.log")

Write-Host "`n=== Build Forklift.Benchmark (current working tree) ==="
dotnet build (Join-Path $repo "Forklift.Benchmark/Forklift.Benchmark.csproj") @msbuildFlags "/p:OutputPath=$benchOut" `
  | Tee-Object -FilePath (Join-Path $logsRoot "build-bench.log")


# --- Build candidate Forklift.Core from working tree
Ensure-Dir $candCoreOut
Write-Host "`n=== Build candidate Forklift.Core @WORKTREE ==="
dotnet build (Join-Path $repo "Forklift.Core/Forklift.Core.csproj") @msbuildFlags "/p:OutputPath=$candCoreOut" `
  | Tee-Object -FilePath (Join-Path $logsRoot "build-candidate-core.log")

# --- Prepare run folders using the SAME benchmark bits; swap Core dll
if (Test-Path $runA) { Remove-Item $runA -Recurse -Force }
if (Test-Path $runB) { Remove-Item $runB -Recurse -Force }
Copy-Item $benchOut $runA -Recurse
Copy-Item $benchOut $runB -Recurse

$baseCoreDll = Get-ChildItem -Path $baseCoreOut -Recurse -Filter "Forklift.Core.dll" | Select-Object -First 1
$candCoreDll = Get-ChildItem -Path $candCoreOut -Recurse -Filter "Forklift.Core.dll" | Select-Object -First 1
if (-not $baseCoreDll) { throw "Baseline Forklift.Core.dll not found." }
if (-not $candCoreDll) { throw "Candidate Forklift.Core.dll not found." }

Copy-Item $baseCoreDll.FullName (Join-Path $runA "Forklift.Core.dll") -Force
Copy-Item $candCoreDll.FullName (Join-Path $runB "Forklift.Core.dll") -Force

# Keep hash-stamped copies for provenance
Copy-Item $baseCoreDll.FullName (Join-Path $runA "Forklift.Core.$baseShort.dll") -Force
Copy-Item $candCoreDll.FullName (Join-Path $runB "Forklift.Core.$candidateLabel.dll") -Force

# --- Helper: run benchmark with JSON and return parsed object
function Invoke-BenchJson([string]$dir, [string]$label){
  Push-Location $dir
  try {
    $benchDll = Get-ChildItem -Filter "Forklift.Benchmark.dll" | Select-Object -First 1
    if (-not $benchDll) { throw "Forklift.Benchmark.dll not found in $dir" }

    $jsonPath = Join-Path $logsRoot "bench-$label.json"
    $argsList = @("--preset",$Preset,"--suite",$Suite,"--depth",$Depth,"--repeat",$Repeat,"--warmup",$Warmup,"--json","--out",$jsonPath)
    if ($Preset -eq "quick") { $argsList += "--skipCorrectness" }
    if ($HighPriority) { $argsList += "--highPriority" }
    if ($KeepHistory) { $argsList += "--keepHistory" }
    if ($Threads -gt 0) { $argsList += @("--threads",$Threads) }

    $logPath = Join-Path $logsRoot "run-$label.log"
    $cmd = @("dotnet", $benchDll.FullName, "--") + $argsList
    ($cmd -join " ") | Out-File $logPath

    # Execute
    & dotnet $benchDll.FullName -- @argsList 2>&1 | Tee-Object -FilePath $logPath -Encoding utf8 | Out-Null

    if (-not (Test-Path $jsonPath)) { throw "Expected JSON output not found: $jsonPath" }
    $obj = Get-Content $jsonPath -Raw | ConvertFrom-Json
    return [pscustomobject]@{
      Label = $label
      Raw   = $obj
      TotalNodes = [int64]$obj.TotalNodes
      TotalElapsedMs = [double]$obj.TotalElapsedMs
      AggregateNps = [double]$obj.AggregateNps
    }
  } finally { Pop-Location }
}

Write-Host "`n=== Run BASELINE ==="
$base = Invoke-BenchJson -dir $runA -label "baseline"

Write-Host "`n=== Run CANDIDATE ==="
$cand = Invoke-BenchJson -dir $runB -label "candidate"

# --- Compute deltas (Aggregate over the suite)
$nodesDeltaPct = Pct $base.TotalNodes $cand.TotalNodes
$timeDeltaPct  = Pct $base.TotalElapsedMs $cand.TotalElapsedMs
$npsDeltaPct   = Pct $base.AggregateNps $cand.AggregateNps

# --- Persist results
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$jsonOut = @{
  BaselineCommit = $baseCommit
  Candidate      = $candidateLabel
  Suite          = $Suite
  Depth          = $Depth
  Repeat         = $Repeat
  Warmup         = $Warmup
  Threads        = ($Threads -gt 0) ? $Threads : $null
  KeepHistory    = [bool]$KeepHistory
  HighPriority   = [bool]$HighPriority
  Baseline       = $base.Raw
  CandidateRes   = $cand.Raw
  Deltas = @{
    NodesPct   = $nodesDeltaPct
    ElapsedPct = $timeDeltaPct
    NpsPct     = $npsDeltaPct
  }
  TimestampUtc   = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 6

$jsonPath = Join-Path $evalRoot "result-$stamp.json"
$jsonOut | Out-File $jsonPath -Encoding utf8

$mdPath = Join-Path $evalRoot "result-$stamp.md"
$md = @"
# Forklift Perf A/B — $stamp

**Baseline**: `$baseCommit`  
**Candidate**: `$candidateLabel` (working tree)

**Suite**: `$Suite`  **Depth**: `$Depth`  **Repeat**: `$Repeat`  **Warmup**: `$Warmup`  **Threads**: `$((($Threads -gt 0) ? $Threads : 'default'))`

| Metric       | Baseline            | Candidate           | Δ % |
|-------------:|--------------------:|--------------------:|----:|
| Nodes        | $($base.TotalNodes) | $($cand.TotalNodes) | $nodesDeltaPct |
| Elapsed (ms) | $([math]::Round($base.TotalElapsedMs,2)) | $([math]::Round($cand.TotalElapsedMs,2)) | $timeDeltaPct |
| NPS          | $([math]::Round($base.AggregateNps,0)) | $([math]::Round($cand.AggregateNps,0)) | $npsDeltaPct |

Per-position medians:
| Position | Nodes | ms | NPS |
|---------:|------:|---:|----:|
"@

# Join on position name order from baseline summary to print both sets neatly
$basePos = @{}
foreach($r in $base.Raw.Results){ $basePos[$r.Name] = $r }
$candPos = @{}
foreach($r in $cand.Raw.Results){ $candPos[$r.Name] = $r }

foreach($name in $basePos.Keys){
  $br = $basePos[$name]; $cr = $candPos[$name]
  $md += ("| {0} | {1} → {2} | {3} → {4} | {5} → {6} |`n" -f
    $name,
    ($br.NodesMedian), ($cr.NodesMedian),
    ([math]::Round($br.ElapsedMsMedian,0)), ([math]::Round($cr.ElapsedMsMedian,0)),
    ([math]::Round($br.NpsMedian,0)), ([math]::Round($cr.NpsMedian,0)))
}

$md += @"

Logs & artifacts:
- JSON: `$($jsonPath)`
- Baseline build logs: `.eval/logs/build-baseline-core.log`, `.eval/logs/build-baseline-bench.log`
- Candidate build logs: `.eval/logs/build-candidate-core.log`
- Runs: `.eval/logs/run-baseline.log`, `.eval/logs/run-candidate.log`
"@
$md | Out-File $mdPath -Encoding utf8

Write-Host "`n=== Summary ==="
Get-Content $mdPath

# --- Perf gate (optional)
if ($FailIfNpsDropPct -ne 0.0 -and $npsDeltaPct -lt $FailIfNpsDropPct) {
  Write-Host "`nERROR: Candidate Aggregate NPS delta $npsDeltaPct% is below threshold $FailIfNpsDropPct%. Failing." -ForegroundColor Red
  if (-not $KeepWorktree) { git worktree remove --force $baseWT }
  exit 1
}

if (-not $KeepWorktree) {
  Write-Host "`nCleaning baseline worktree..."
  git worktree remove --force $baseWT
}

Write-Host "`nDone. Results:"
Write-Host "  $jsonPath"
Write-Host "  $mdPath"
