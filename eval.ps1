# -------------------------
# eval.ps1 (header)
# -------------------------
[CmdletBinding()]
param(
  [string]$MainRef = "origin/main",
  [string]$Configuration = "Release",

  # Quick/CI/Deep preset mapping
  [ValidateSet("quick", "ci", "deep")]
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

function Show-StageProgress([string]$activity, [string]$status, [int]$percent) {
  # Write-Progress collapses automatically in PS7; keep it lightweight
  Write-Progress -Activity $activity -Status $status -PercentComplete $percent
}

function Run-Quiet {
  param(
    [scriptblock]$Cmd,
    [string]$LogPath
  )
  $out = & $Cmd 2>&1
  $out | Out-File $LogPath -Encoding utf8
  if ($VerbosePreference -eq 'Continue') { $out }
}

function Colorize([double]$pct) {
  if ($null -eq $pct) { return @{Color = 'Yellow'; Tag = ' ? ' } }
  if ($pct -ge 0.5) { return @{Color = 'Green'; Tag = '↑ ' } }
  if ($pct -le -0.5) { return @{Color = 'Red'; Tag = '↓ ' } }
  return @{Color = 'Yellow'; Tag = '→ ' }
}

function Bar([double]$pct) {
  # ASCII bar from -15%..+15%, 31 columns, center at zero
  $span = 15.0
  $p = [math]::Max(-$span, [math]::Min($span, $pct))
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
    [string]$BaseStr,
    [string]$CandStr,
    [double]$Pct
  )
  $sty = Colorize $Pct
  $bar = Bar $Pct
  $pctStr = "{0,7:N3}%" -f $Pct
  $line = "{0,-14} {1,14} → {2,14}   {3}  {4}" -f $Name, $BaseStr, $CandStr, $pctStr, $bar
  Write-Host $line -ForegroundColor $sty.Color
}

# --- Preset → default mappings (you can tweak later)
switch ($Preset) {
  "quick" {
    if (-not $Suite) { $Suite = "minimal" }
    if (-not $Depth) { $Depth = 3 }
    if (-not $Repeat) { $Repeat = 1 }
    if (-not $Warmup) { $Warmup = 0 }
    $SkipCorrectness = $true
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

Write-Host "Preset: $Preset  Suite=$Suite Depth=$Depth Repeat=$Repeat Warmup=$Warmup Threads=$Threads"

function Resolve-GitRoot { git rev-parse --show-toplevel }
function Ensure-Dir([string]$p) { if (-not(Test-Path $p)) { New-Item -ItemType Directory -Path $p | Out-Null } }
function ShortHash([string]$h) { if ($h.Length -ge 7) { $h.Substring(0, 7) } else { $h } }
function Pct([double]$a, [double]$b) { if ($a -eq 0) { return $null } [math]::Round((($b - $a) / $a) * 100.0, 3) }

# --- Layout
$repo = Resolve-GitRoot
Set-Location $repo

$evalRoot = Join-Path $repo ".eval"
$outRoot = Join-Path $evalRoot "out"
$logsRoot = Join-Path $evalRoot "logs"
$baseWT = Join-Path $evalRoot "baseline"
$benchOut = Join-Path $outRoot "baseline-bench"
$baseCoreOut = Join-Path $outRoot "baseline-core"
$candCoreOut = Join-Path $outRoot "candidate-core"
$runA = Join-Path $outRoot "run-baseline"
$runB = Join-Path $outRoot "run-candidate"
Ensure-Dir $evalRoot; Ensure-Dir $outRoot; Ensure-Dir $logsRoot

# --- Identify commits
try { $baseCommit = (git rev-parse $MainRef).Trim() } catch { $baseCommit = (git rev-parse HEAD).Trim() }
$baseShort = ShortHash $baseCommit
$candidateLabel = "WORKTREE"
$baseSubject = (git show -s --format=%s $baseCommit).Trim()
Write-Host ("Baseline: {0} — {1}" -f $baseCommit, $baseSubject)
Write-Host "Candidate: WORKTREE (uncommitted)"

# --- Baseline worktree
Show-StageProgress "Setup" "Preparing baseline worktree" 10
if (Test-Path $baseWT) {
  $existing = (git -C $baseWT rev-parse HEAD).Trim()
  if ($existing -ne $baseCommit) {
    Run-Quiet { git worktree remove --force --quiet $baseWT } (Join-Path $logsRoot "worktree-remove.log")
    Run-Quiet { git worktree add --detach --quiet $baseWT $baseCommit } (Join-Path $logsRoot "worktree-add.log")
  }
}
else {
  Run-Quiet { git worktree add --detach --quiet $baseWT $baseCommit } (Join-Path $logsRoot "worktree-add.log")
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

Show-StageProgress "Build" "Baseline Forklift.Core" 25
Run-Quiet { dotnet build (Join-Path $baseWT "Forklift.Core/Forklift.Core.csproj") @msbuildFlags "/p:OutputPath=$baseCoreOut" } (Join-Path $logsRoot "build-baseline-core.log")

Show-StageProgress "Build" "Benchmark (working tree)" 40
Run-Quiet { dotnet build (Join-Path $repo "Forklift.Benchmark/Forklift.Benchmark.csproj") @msbuildFlags "/p:OutputPath=$benchOut" } (Join-Path $logsRoot "build-bench.log")


# --- Build candidate Forklift.Core from working tree
Ensure-Dir $candCoreOut
Show-StageProgress "Build" "Candidate Forklift.Core" 55
Run-Quiet { dotnet build (Join-Path $repo "Forklift.Core/Forklift.Core.csproj") @msbuildFlags "/p:OutputPath=$candCoreOut" } (Join-Path $logsRoot "build-candidate-core.log")

# --- Prepare run folders using the SAME benchmark bits; swap Core dll
if (Test-Path $runA) { Remove-Item $runA -Recurse -Force }
if (Test-Path $runB) { Remove-Item $runB -Recurse -Force }
Ensure-Dir $runA; Ensure-Dir $runB
Copy-Item -Path (Join-Path $benchOut '*') -Destination $runA -Recurse -Force
Copy-Item -Path (Join-Path $benchOut '*') -Destination $runB -Recurse -Force

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
function Invoke-BenchJson([string]$dir, [string]$label) {
  Push-Location $dir
  try {
    $benchDll = Get-ChildItem -Filter "Forklift.Benchmark.dll" | Select-Object -First 1
    if (-not $benchDll) { throw "Forklift.Benchmark.dll not found in $dir" }

    $jsonPath = Join-Path $logsRoot "bench-$label.json"
    $argsList = @("--preset", $Preset, "--suite", $Suite, "--depth", $Depth, "--repeat", $Repeat, "--warmup", $Warmup, "--json", "--out", $jsonPath)
    if ($Preset -eq "quick") { $argsList += "--skipCorrectness" }
    if ($HighPriority) { $argsList += "--highPriority" }
    if ($KeepHistory) { $argsList += "--keepHistory" }
    if ($Threads -gt 0) { $argsList += @("--threads", $Threads) }

    $logPath = Join-Path $logsRoot "run-$label.log"
    $cmd = @("dotnet", $benchDll.FullName, "--") + $argsList
    ($cmd -join " ") | Out-File $logPath

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

Show-StageProgress "Run" "Baseline benchmark" 70
$base = Invoke-BenchJson -dir $runA -label "baseline"

Show-StageProgress "Run" "Candidate benchmark" 85
$cand = Invoke-BenchJson -dir $runB -label "candidate"

# --- Compute deltas (Aggregate over the suite)
Show-StageProgress "Summarize" "Computing deltas" 95
$nodesDeltaPct = Pct $base.TotalNodes $cand.TotalNodes
$timeDeltaPct = Pct $base.TotalElapsedMs $cand.TotalElapsedMs
$npsDeltaPct = Pct $base.AggregateNps $cand.AggregateNps

# --- Persist results
Write-Host ""

# Pretty console deltas (quiet, aligned, rounded)
PrintDeltaLine "Nodes"        ($base.TotalNodes.ToString("N0"))            ($cand.TotalNodes.ToString("N0"))            $nodesDeltaPct
PrintDeltaLine "Elapsed (ms)" ($base.TotalElapsedMs.ToString("N2"))        ($cand.TotalElapsedMs.ToString("N2"))        $timeDeltaPct
PrintDeltaLine "NPS"          ([math]::Round($base.AggregateNps, 0).ToString("N0")) ([math]::Round($cand.AggregateNps, 0).ToString("N0")) $npsDeltaPct

Write-Progress -Activity "Done" -Completed

# Also keep the Markdown + JSON artifacts
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$jsonPath = Join-Path $evalRoot "result-$stamp.json"
$mdPath = Join-Path $evalRoot "result-$stamp.md"

$jsonOut = @{
  BaselineCommit = $baseCommit
  Candidate      = $candidateLabel
  Suite          = $Suite
  Depth          = $Depth
  Repeat         = $Repeat
  Warmup         = $Warmup
  Threads        = if ($Threads -gt 0) { $Threads } else { $null }
  KeepHistory    = [bool]$KeepHistory
  HighPriority   = [bool]$HighPriority
  Baseline       = $base.Raw
  CandidateRes   = $cand.Raw
  Deltas         = @{
    NodesPct   = $nodesDeltaPct
    ElapsedPct = $timeDeltaPct
    NpsPct     = $npsDeltaPct
  }
  TimestampUtc   = (Get-Date).ToUniversalTime().ToString("o")
}
$jsonOut | ConvertTo-Json -Depth 6 | Out-File $jsonPath -Encoding utf8

$threadsLabel = if ($Threads -gt 0) { "$Threads" } else { "default" }
$md = @"
# Forklift Perf A/B — $stamp

**Baseline**: `$baseCommit`  
**Candidate**: `$candidateLabel` (working tree)

**Suite**: `$Suite`  **Depth**: `$Depth`  **Repeat**: `$Repeat`  **Warmup**: `$Warmup`  **Threads**: `$threadsLabel`

| Metric       | Baseline | Candidate | Δ % |
|-------------:|---------:|----------:|----:|
| Nodes        | $($base.TotalNodes) | $($cand.TotalNodes) | $nodesDeltaPct |
| Elapsed (ms) | $([math]::Round($base.TotalElapsedMs,2)) | $([math]::Round($cand.TotalElapsedMs,2)) | $timeDeltaPct |
| NPS          | $([math]::Round($base.AggregateNps,0)) | $([math]::Round($cand.AggregateNps,0)) | $npsDeltaPct |

Per-position medians:
| Position | Nodes (B → C) | ms (B → C) | NPS (B → C) |
|---------:|---------------:|-----------:|------------:|
"@

# rows
foreach ($br in $base.Raw.Results) {
  $name = $br.Name
  $cr = $cand.Raw.Results | Where-Object Name -eq $name
  $md += ("| {0} | {1} → {2} | {3} → {4} | {5} → {6} |`n" -f
    $name,
    $br.NodesMedian, $cr.NodesMedian,
    ([math]::Round($br.ElapsedMsMedian, 2)), ([math]::Round($cr.ElapsedMsMedian, 2)),
    ([math]::Round($br.NpsMedian, 0)), ([math]::Round($cr.NpsMedian, 0)))
}


$md += @"

Logs & artifacts:
- JSON: `$jsonPath`
- Baseline build logs: `.eval/logs/build-baseline-core.log`
- Benchmark build logs: `.eval/logs/build-bench.log`
- Candidate build logs: `.eval/logs/build-candidate-core.log`
- Runs: `.eval/logs/run-baseline.log`, `.eval/logs/run-candidate.log`
"@
$md | Out-File $mdPath -Encoding utf8

Write-Host ""
Write-Host "Saved:" -ForegroundColor DarkGray
Write-Host "  $jsonPath"
Write-Host "  $mdPath"
Write-Host ""

# --- Perf gate (optional)
if ($FailIfNpsDropPct -ne 0.0 -and $npsDeltaPct -lt $FailIfNpsDropPct) {
  Write-Host ("FAIL: Aggregate NPS delta {0:N3}% < threshold {1:N3}%." -f $npsDeltaPct, $FailIfNpsDropPct) -ForegroundColor Red
  if (-not $KeepWorktree) { Run-Quiet { git worktree remove --force --quiet $baseWT } (Join-Path $logsRoot "worktree-remove.log") }
  exit 1
}
else {
  Write-Host ("OK: Aggregate NPS delta {0:N3}% ≥ threshold {1:N3}%." -f $npsDeltaPct, $FailIfNpsDropPct) -ForegroundColor Green
}

if (-not $KeepWorktree) {
  Run-Quiet { git worktree remove --force --quiet $baseWT } (Join-Path $logsRoot "worktree-remove.log")
}