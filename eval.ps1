# -------------------------
# eval.ps1  (no-cache, verbose variable names, live progress, null-safe metrics)
# -------------------------
[CmdletBinding()]
param(
  [string]$MainRef = "main",
  [string]$Configuration = "Release",

  # Forward-only flags for the benchmark; null means "don't pass it"
  [ValidateSet("quick", "ci", "deep")]
  [string]$Preset,
  [string]$Suite,
  [int]$Depth,
  [int]$Repeat,
  [int]$Warmup,
  [int]$Threads,
  [int]$Trials = 10,
  [int]$AffinityMask,

  [switch]$HighPriority,
  [switch]$KeepHistory,
  [double]$TolerancePercent = 0.5,
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

$script:TopLevelParams = @{}
$PSBoundParameters.GetEnumerator() | ForEach-Object { $script:TopLevelParams[$_.Key] = $_.Value }

$ErrorActionPreference = "Stop"
# Set $VerbosePreference without using "if as expression"
if ($PSBoundParameters.ContainsKey('Verbose')) {
  $VerbosePreference = 'Continue'
} else {
  $VerbosePreference = 'SilentlyContinue'
}
$script:ExitCode = 0

# ---- Progress manager (monotonic + nested) ----
$script:ProgressBook = @{}  # id -> @{ LastPct = -1; LastStatus = ""; Activity = "" }

function Set-Progress {
  param(
    [int]$Id,
    [int]$ParentId = -1,
    [string]$Activity,
    [string]$Status,
    [int]$Percent,
    [switch]$Completed
  )

  if ($Completed) {
    Write-Progress -Id $Id -Completed
    $script:ProgressBook.Remove($Id) | Out-Null
    return
  }

  $state = $script:ProgressBook[$Id]
  if (-not $state) {
    $state = @{ LastPct = -1; LastStatus = ""; Activity = $Activity }
    $script:ProgressBook[$Id] = $state
  }

  # Clamp percent to [0,100] and ensure monotonic increase, but always allow 100 to be set
  $clampedPercent = [Math]::Max(0, [Math]::Min(100, $Percent))
  if ($clampedPercent -eq $state.LastPct -and $Status -eq $state.LastStatus) { return }

  # Always allow 100 to be set, even if LastPct is higher (for completion)
  if ($clampedPercent -lt $state.LastPct -and $clampedPercent -ne 100) {
    $clampedPercent = $state.LastPct
  }

  Write-Progress -Id $Id `
    -Activity $Activity `
    -Status $Status `
    -PercentComplete $clampedPercent `
    -ParentId $ParentId

  $state.LastPct    = $clampedPercent
  $state.LastStatus = $Status
  $state.Activity   = $Activity
}

function Complete-Progress { param([int]$Id) Write-Progress -Id $Id -Completed }

function Invoke-Quiet {
  param(
    [scriptblock]$Command,
    [string]$LogPath,
    [object[]]$ArgumentList,
    [string]$OnFail = "Command failed"
  )
  $output = & $Command @ArgumentList 2>&1
  $exit = $LASTEXITCODE
  $output | Out-File $LogPath -Encoding utf8
  if ($VerbosePreference -eq 'Continue') { $output }
  if ($exit -ne 0) {
    $tail = ($output | Select-Object -Last 60) -join "`n"
    throw "$OnFail (exit $exit). See log: $LogPath`n---- tail ----`n$tail"
  }
}

function Colorize([double]$Percent) {
  if ($null -eq $Percent) { return @{ Color = 'Yellow'; Tag = ' ? ' } }
  if ($Percent -ge 0.5) { return @{ Color = 'Green'; Tag = '↑ ' } }
  if ($Percent -le -0.5) { return @{ Color = 'Red'; Tag = '↓ ' } }
  return @{ Color = 'Yellow'; Tag = '→ ' }
}
function Bar([double]$Percent) {
  $span = 15.0
  $clamped = [math]::Max(-$span, [math]::Min($span, ($Percent ?? 0)))
  $columns = 31
  $mid = [int][math]::Floor($columns / 2)
  $pos = $mid + [int][math]::Round(($clamped / $span) * $mid)
  $chars = New-Object char[] $columns
  for ($i = 0; $i -lt $columns; $i++) { $chars[$i] = ' ' }
  $chars[$mid] = '|'
  if ($pos -gt $mid) { for ($i = $mid + 1; $i -le $pos; $i++) { $chars[$i] = '█' } }
  elseif ($pos -lt $mid) { for ($i = $pos; $i -lt $mid; $i++) { $chars[$i] = '█' } }
  -join $chars
}
function PrintDeltaLine {
  param(
    [string]$Name,
    [string]$BaselineString,
    [string]$CandidateString,
    $Percent,                 # accept null/any; we'll normalize
    [switch]$LowerIsBetter
  )

  if ($null -eq $Percent -or ($Percent -isnot [double] -and $Percent -isnot [single] -and $Percent -isnot [decimal] -and $Percent -isnot [int])) {
    $percentString = "   n/a "
    $bar = Bar 0
    $line = "{0,-14} {1,14} → {2,14}   {3}  {4}" -f $Name, $BaselineString, $CandidateString, $percentString, $bar
    Write-Host $line -ForegroundColor Yellow
    return
  }

  $p = [double]$Percent
  if ($LowerIsBetter) { $percentForColor = -1 * $p } else { $percentForColor = $p }
  $colorInfo = Colorize $percentForColor
  $bar = Bar $p
  $percentString = "{0,7:N3}%" -f $p
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
function Test-IsRegisteredWorktree([string]$Path) {
  $porcelain = git worktree list --porcelain 2>$null
  if (-not $porcelain) { return $false }
  foreach ($line in ($porcelain -split "`n")) {
    if ($line -like "worktree *") {
      $wtPath = $line.Substring(9)
      if ([IO.Path]::GetFullPath($wtPath) -eq [IO.Path]::GetFullPath($Path)) { return $true }
    }
  }
  return $false
}
function Remove-WorktreeArtifact {
  [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
  param([switch]$Force)
  if ($KeepWorktree) { return }
  $targets = @()
  if (Test-Path $baselineWorktree) { $targets += @{ Path = $baselineWorktree; Log = "worktree-remove-baseline.log" } }
  if ($candidateMode -eq "REF" -and (Test-Path $candidateWorktree)) { $targets += @{ Path = $candidateWorktree; Log = "worktree-remove-candidate.log" } }
  if ($BenchmarkReference -and (Test-Path $benchmarkWorktree)) { $targets += @{ Path = $benchmarkWorktree; Log = "worktree-remove-benchmark.log" } }
  foreach ($t in $targets) {
    try {
      if ($PSCmdlet.ShouldProcess($t.Path, "git worktree remove --force")) {
        if (Test-IsRegisteredWorktree $t.Path) {
          Invoke-Quiet -Command { param($p) git worktree remove --force $p } `
            -LogPath (Join-Path $logsRoot $t.Log) -ArgumentList $t.Path -OnFail "git worktree remove failed"
        }
        if (Test-Path $t.Path) { Remove-Item $t.Path -Recurse -Force -ErrorAction SilentlyContinue }
      }
    } catch { Write-Host "Cleanup warning for '$($t.Path)': $($_.Exception.Message)" -ForegroundColor Yellow }
  }
  try {
    Invoke-Quiet -Command { git worktree prune } `
      -LogPath (Join-Path $logsRoot "worktree-prune.log") -ArgumentList @() -OnFail "git worktree prune failed"
  } catch { Write-Host "Cleanup warning during 'git worktree prune': $($_.Exception.Message)" -ForegroundColor Yellow }
}

# --- Layout and repo info
$repoRoot = Resolve-GitRoot
Set-Location $repoRoot

$evaluationRoot = Join-Path $repoRoot ".eval"
$outRoot = Join-Path $evaluationRoot "out"
$logsRoot = Join-Path $evaluationRoot "logs"
$baselineWorktree = Join-Path $evaluationRoot "baseline"
$candidateWorktree = Join-Path $evaluationRoot "candidate"
$benchmarkWorktree = $null
$runBaselineDirectory = Join-Path $outRoot "run-baseline"
$runCandidateDirectory = Join-Path $outRoot "run-candidate"

New-Directory $evaluationRoot
New-Directory $outRoot
New-Directory $logsRoot

# ---- Progress slices ----
$OverallId = 1
$BuildId = 2
$TrialsId = 3

$setupPct = 10
$buildPct = 35
$trialsPct = 55

$overallBase = 0
Set-Progress -Id $OverallId -Activity "Forklift Perf A/B" -Status "Setup" -Percent $overallBase

# Default baseline ref to local 'main' unless user passed -MainRef
if (-not $PSBoundParameters.ContainsKey('MainRef') -or [string]::IsNullOrWhiteSpace($MainRef)) { $MainRef = 'main' }

function Resolve-CommitStrict {
  param([string]$RefName)
  $commit = (git rev-parse $RefName 2>$null).Trim()
  if (-not $commit) { throw "Unable to resolve ref '$RefName'." }
  $commit
}

# If MainRef is a remote-tracking ref, fetch that remote first
$remotes = (git remote 2>$null) -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }
foreach ($remote in $remotes) {
  if ($MainRef -like "$remote/*") {
    Invoke-Quiet -Command { git fetch $using:remote --tags --prune } `
      -LogPath (Join-Path $logsRoot "git-fetch-$remote.log") -ArgumentList @() -OnFail "git fetch $remote failed"
    break
  }
}

$baselineCommit = Resolve-CommitStrict $MainRef
$baselineShortHash = ShortHash $baselineCommit
$baselineSubject = (git show -s --format=%s $baselineCommit).Trim()
$baselineWhenUnix = (git show -s --format=%ct $baselineCommit).Trim()
$baselineWhen = [DateTimeOffset]::FromUnixTimeSeconds([int64]$baselineWhenUnix).ToLocalTime()

if ([string]::IsNullOrWhiteSpace($CandidateRef)) {
  $candidateMode = 'WORKTREE'
  $candidateCommit = (git rev-parse HEAD).Trim()
  $candidateLabel = 'WORKTREE'
  $candidateSubject = 'working tree'
  $candidateWhen = Get-Date
} else {
  $candidateMode = 'REF'
  $candidateCommit = Resolve-CommitStrict $CandidateRef
  $candidateLabel = ShortHash $candidateCommit
  $candidateSubject = (git show -s --format=%s $candidateCommit).Trim()
  $candidateWhenUnix = (git show -s --format=%ct $candidateCommit).Trim()
  $candidateWhen = [DateTimeOffset]::FromUnixTimeSeconds([int64]$candidateWhenUnix).ToLocalTime()
}

Write-Host ("Baseline:  {0} — {1} ({2:yyyy-MM-dd HH:mm})" -f $baselineCommit, $baselineSubject, $baselineWhen)
if ($candidateMode -eq 'REF') { Write-Host ("Candidate: {0} — {1} ({2:yyyy-MM-dd HH:mm})" -f $candidateCommit, $candidateSubject, $candidateWhen) }
else { Write-Host "Candidate: WORKTREE (uncommitted)" }

# --- Prepare worktrees (force fresh baseline; candidate only when comparing to a ref)
Set-Progress -Id $OverallId -Activity "Forklift Perf A/B" -Status "Preparing worktrees" -Percent ($overallBase + 2)

# Remove baseline worktree if present
if (Test-Path $baselineWorktree) {
  if (Test-IsRegisteredWorktree $baselineWorktree) {
    Invoke-Quiet -Command { param($p) git worktree remove --force $p } `
      -LogPath (Join-Path $logsRoot "worktree-remove-baseline.log") `
      -ArgumentList $baselineWorktree `
      -OnFail "git worktree remove (baseline) failed"
  }
  if (Test-Path $baselineWorktree) { Remove-Item $baselineWorktree -Recurse -Force }
  Invoke-Quiet -Command { git worktree prune } `
    -LogPath (Join-Path $logsRoot "worktree-prune.log") `
    -ArgumentList @() `
    -OnFail "git worktree prune failed"
}

# Add fresh baseline worktree at the exact commit
Invoke-Quiet -Command { param($p, $c) git worktree add --detach --quiet $p $c } `
  -LogPath (Join-Path $logsRoot "worktree-add-baseline.log") `
  -ArgumentList $baselineWorktree, $baselineCommit `
  -OnFail "git worktree add (baseline) failed"

# Verify head
$verifyBaselineHead = (git -C $baselineWorktree rev-parse HEAD).Trim()
if ($verifyBaselineHead -ne $baselineCommit) { throw "Baseline worktree HEAD mismatch. Expected $baselineCommit, got $verifyBaselineHead" }

# Candidate worktree only if comparing to a specific ref
if ($candidateMode -eq "REF") {
  if (Test-Path $candidateWorktree) {
    if (Test-IsRegisteredWorktree $candidateWorktree) {
      Invoke-Quiet -Command { param($p) git worktree remove --force $p } `
        -LogPath (Join-Path $logsRoot "worktree-remove-candidate.log") `
        -ArgumentList $candidateWorktree `
        -OnFail "git worktree remove (candidate) failed"
    }
    if (Test-Path $candidateWorktree) { Remove-Item $candidateWorktree -Recurse -Force }
    Invoke-Quiet -Command { git worktree prune } `
      -LogPath (Join-Path $logsRoot "worktree-prune.log") `
      -ArgumentList @() `
      -OnFail "git worktree prune failed"
  }

  Invoke-Quiet -Command { param($p, $c) git worktree add --detach --quiet $p $c } `
    -LogPath (Join-Path $logsRoot "worktree-add-candidate.log") `
    -ArgumentList $candidateWorktree, $candidateCommit `
    -OnFail "git worktree add (candidate) failed"

  $verifyCandidateHead = (git -C $candidateWorktree rev-parse HEAD).Trim()
  if ($verifyCandidateHead -ne $candidateCommit) { throw "Candidate worktree HEAD mismatch. Expected $candidateCommit, got $verifyCandidateHead" }
}

Set-Progress -Id $OverallId -Activity "Forklift Perf A/B" -Status "Setup complete" -Percent ($overallBase + $setupPct)
$overallBase += $setupPct

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

# --- Build progress (parent & slices)
$buildChunk = [math]::Floor($buildPct / 3)
Set-Progress -Id $BuildId -ParentId $OverallId -Activity "Build" -Status "Baseline Core" -Percent 0
Set-Progress -Id $OverallId -Activity "Forklift Perf A/B" -Status "Build: Baseline Core" -Percent ($overallBase + 5)

# --- Build Forklift.Core (baseline)
Invoke-Quiet -Command {
  param($worktreePath, $flags, $outDir)
  dotnet build (Join-Path $worktreePath "Forklift.Core/Forklift.Core.csproj") @flags "/p:OutputPath=$outDir"
} -LogPath (Join-Path $logsRoot "build-baseline-core.log") -ArgumentList $baselineWorktree, $msbuildFlags, $baselineCoreOutputDirectory

$baselineCoreDllPath = Join-Path $baselineCoreOutputDirectory "Forklift.Core.dll"
if (-not (Test-Path $baselineCoreDllPath)) { throw "Baseline Core build produced no Forklift.Core.dll at $baselineCoreDllPath" }

Set-Progress -Id $BuildId -ParentId $OverallId -Activity "Build" -Status "Candidate Core" -Percent $buildChunk
Set-Progress -Id $OverallId -Activity "Forklift Perf A/B" -Status "Build: Candidate Core" -Percent ($overallBase + $buildChunk)

# --- Build Forklift.Core (candidate)
if ($candidateMode -eq "REF") {
  Invoke-Quiet -Command {
    param($worktreePath, $flags, $outDir)
    dotnet build (Join-Path $worktreePath "Forklift.Core/Forklift.Core.csproj") @flags "/p:OutputPath=$outDir"
  } -LogPath (Join-Path $logsRoot "build-candidate-core.log") -ArgumentList $candidateWorktree, $msbuildFlags, $candidateCoreOutputDirectory
} else {
  Invoke-Quiet -Command {
    param($repoPath, $flags, $outDir)
    dotnet build (Join-Path $repoPath "Forklift.Core/Forklift.Core.csproj") @flags "/p:OutputPath=$outDir"
  } -LogPath (Join-Path $logsRoot "build-candidate-core.log") -ArgumentList $repoRoot, $msbuildFlags, $candidateCoreOutputDirectory
}

$candidateCoreDllPath = Join-Path $candidateCoreOutputDirectory "Forklift.Core.dll"
if (-not (Test-Path $candidateCoreDllPath)) { throw "Candidate Core build produced no Forklift.Core.dll at $candidateCoreDllPath" }

Set-Progress -Id $BuildId -ParentId $OverallId -Activity "Build" -Status "Benchmark App" -Percent ($buildChunk * 2)
Set-Progress -Id $OverallId -Activity "Forklift Perf A/B" -Status "Build: Benchmark App" -Percent ($overallBase + $buildChunk * 2)

# --- Build Forklift.Benchmark bits (single rebuild)
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
    } else {
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

if ($candidateMode -eq 'REF') {
  $stampCandidate = $candidateLabel
} else {
  $stampCandidate = 'WORKTREE'
}
Copy-Item $candidateCoreDllPath (Join-Path $runCandidateDirectory ("Forklift.Core.{0}.dll" -f $stampCandidate)) -Force

Set-Progress -Id $BuildId -ParentId $OverallId -Activity "Build" -Status "Complete" -Percent 100
Complete-Progress -Id $BuildId
Set-Progress -Id $OverallId -Activity "Forklift Perf A/B" -Status "Build complete" -Percent 100
$overallBase = 100

function New-BenchArguments {
  $p = $script:TopLevelParams
  $effectivePreset = 'quick'
  if ($p.ContainsKey('Preset') -and $null -ne $Preset -and $Preset) { $effectivePreset = $Preset }

  $arguments = @("--json", "--preset=$effectivePreset")
  if ($p.ContainsKey('Suite') -and $Suite) { $arguments += "--suite=$Suite" }
  if ($p.ContainsKey('Depth') -and $Depth) { $arguments += "--depth=$Depth" }
  if ($p.ContainsKey('Repeat') -and $Repeat) { $arguments += "--repeat=$Repeat" }
  if ($p.ContainsKey('Warmup') -and $Warmup) { $arguments += "--warmup=$Warmup" }
  if ($p.ContainsKey('Threads') -and $Threads -gt 0) { $arguments += "--threads=$Threads" }
  if ($PSBoundParameters.ContainsKey('AffinityMask') -and $AffinityMask -gt 0) { $arguments += @("--affinity", $AffinityMask) }
  if ($HighPriority) { $arguments += "--highPriority" }
  if ($KeepHistory) { $arguments += "--keepHistory" }
  return $arguments
}

function Invoke-BenchJson {
  param(
    [string]$WorkingDirectory,
    [string]$Label,
    [int]$ProgressId,
    [int]$ParentProgressId,
    [int]$SliceStartPercent,
    [int]$SliceEndPercent,
    [int]$SoftSeconds = 6
  )

  Push-Location $WorkingDirectory
  try {
    $benchDll = Get-ChildItem -Filter "Forklift.Benchmark.dll" | Select-Object -First 1
    if (-not $benchDll) { throw "Forklift.Benchmark.dll not found in $WorkingDirectory" }

    $jsonOutputPath = Join-Path $logsRoot "bench-$Label.json"
    $runLogPath     = Join-Path $logsRoot "run-$Label.log"
    if (Test-Path $jsonOutputPath) { Remove-Item $jsonOutputPath -Force }
    if (Test-Path $runLogPath)    { Remove-Item $runLogPath -Force }

    $arguments = New-BenchArguments
    $arguments += @("--out", $jsonOutputPath)
    if ($arguments -contains "--") { throw "BUG: Bare '--' in argument list will break Program.Args parsing." }

    # Tame JIT / ReadyToRun variance
    $jitVars = @('COMPlus_ReadyToRun','COMPlus_TieredCompilation','COMPlus_TC_QuickJitForLoops','COMPlus_TieredPGO','DOTNET_ReadyToRun')
    $old = @{}
    foreach ($k in $jitVars) { $old[$k] = (Get-Item "Env:$k" -ErrorAction SilentlyContinue).Value; Set-Item "Env:$k" '0' }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName              = "dotnet"
    $psi.Arguments             = '"' + $benchDll.FullName + '" ' + ($arguments -join ' ')
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true
    $psi.WorkingDirectory       = $WorkingDirectory

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    $null = $proc.Start()

    $stdout = $proc.StandardOutput
    $stderr = $proc.StandardError
    $outSb  = [System.Text.StringBuilder]::new()
    $errSb  = [System.Text.StringBuilder]::new()

    $sliceSpan = [Math]::Max(1, $SliceEndPercent - $SliceStartPercent)
    $t0 = Get-Date

    Set-Progress -Id $ProgressId -ParentId $ParentProgressId `
      -Activity "Trial run: $Label" -Status "Starting…" -Percent $SliceStartPercent

    while (-not $proc.HasExited) {
      while (-not $stdout.EndOfStream) { [void]$outSb.AppendLine($stdout.ReadLine()) }
      while (-not $stderr.EndOfStream) { [void]$errSb.AppendLine($stderr.ReadLine()) }

      $elapsed = (Get-Date) - $t0
      $ratio   = [Math]::Min(0.98, $elapsed.TotalSeconds / [Math]::Max(1,$SoftSeconds))
      $pct     = $SliceStartPercent + [Math]::Floor($sliceSpan * $ratio)

      Set-Progress -Id $ProgressId -ParentId $ParentProgressId `
        -Activity "Trial run: $Label" -Status ("Running… {0:N1}s" -f $elapsed.TotalSeconds) -Percent $pct

      Start-Sleep -Milliseconds 120
    }

    while (-not $stdout.EndOfStream) { [void]$outSb.AppendLine($stdout.ReadLine()) }
    while (-not $stderr.EndOfStream) { [void]$errSb.AppendLine($stderr.ReadLine()) }

    Set-Progress -Id $ProgressId -ParentId $ParentProgressId `
      -Activity "Trial run: $Label" -Status "Finalizing…" -Percent $SliceEndPercent
    Set-Progress -Id $ProgressId -Completed

    [System.IO.File]::WriteAllText($runLogPath, $outSb.ToString() + "`n" + $errSb.ToString(), [System.Text.Encoding]::UTF8)

    foreach ($k in $jitVars) {
      if ($null -eq $old[$k]) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue } else { Set-Item "Env:$k" $old[$k] }
    }

    if ($proc.ExitCode -ne 0) {
      $tail = (Get-Content $runLogPath -Tail 60) -join "`n"
      throw "Benchmark exited with code $($proc.ExitCode). See $runLogPath`n---- tail ----`n$tail"
    }
    if (-not (Test-Path $jsonOutputPath)) {
      $tail = (Get-Content $runLogPath -Tail 60) -join "`n"
      throw "Expected JSON output not found: $jsonOutputPath`n---- tail ----`n$tail"
    }

    $parsed = Get-Content $jsonOutputPath -Raw | ConvertFrom-Json
    [pscustomobject]@{
      Label          = $Label
      Raw            = $parsed
      TotalNodes     = [int64]$parsed.TotalNodes
      TotalElapsedMs = [double]$parsed.TotalElapsedMs
      AggregateNps   = [double]$parsed.AggregateNps
    }
  }
  finally { Pop-Location }
}

# --- Trials progress parent
Set-Progress -Id $TrialsId -ParentId $OverallId -Activity "Trials" -Status "Preparing" -Percent 0
Set-Progress -Id $OverallId -Activity "Forklift Perf A/B" -Status "Trials" -Percent ($overallBase + 0)

# --- Trials: alternate order each trial and collect deltas
$trialRows = @()
$totalRuns = $Trials * 2
$perRunPct = [Math]::Max(1, [Math]::Floor($trialsPct / $totalRuns))
$trialsBase = $overallBase
$ParentProgressId = 10
$ChildProgressStart = 1000

Set-Progress -Id $ParentProgressId -Activity "Forklift Perf A/B" -Status "Preparing…" -Percent 0

for ($t = 0; $t -lt $Trials; $t++) {
  $swapOrder = ($t % 2) -eq 0

  $trialBase = $ChildProgressStart + ($t * 2)
  $childA = $trialBase + 0
  $childB = $trialBase + 1

  $trialSliceStart = [Math]::Floor(($t    * 100.0) / $Trials)
  $trialSliceEnd   = [Math]::Floor((($t+1)* 100.0) / $Trials)

  $half = [Math]::Max(1, [Math]::Floor(($trialSliceEnd - $trialSliceStart) / 2))
  $aStart = $trialSliceStart
  $aEnd   = [Math]::Min(100, $trialSliceStart + $half)
  $bStart = $aEnd
  $bEnd   = $trialSliceEnd

  Set-Progress -Id $ParentProgressId -Activity "Forklift Perf A/B [Trials: $($t+1)/$Trials]" -Status "Running…" -Percent $trialSliceStart

  if ($swapOrder) {
    $candidateRun = Invoke-BenchJson -WorkingDirectory $runCandidateDirectory -Label "candidate" `
      -ProgressId $childA -ParentProgressId $ParentProgressId `
      -SliceStartPercent $aStart -SliceEndPercent $aEnd
    $baselineRun = Invoke-BenchJson -WorkingDirectory $runBaselineDirectory -Label "baseline" `
      -ProgressId $childB -ParentProgressId $ParentProgressId `
      -SliceStartPercent $bStart -SliceEndPercent $bEnd
  } else {
    $baselineRun = Invoke-BenchJson -WorkingDirectory $runBaselineDirectory -Label "baseline" `
      -ProgressId $childA -ParentProgressId $ParentProgressId `
      -SliceStartPercent $aStart -SliceEndPercent $aEnd
    $candidateRun = Invoke-BenchJson -WorkingDirectory $runCandidateDirectory -Label "candidate" `
      -ProgressId $childB -ParentProgressId $ParentProgressId `
      -SliceStartPercent $bStart -SliceEndPercent $bEnd
  }

  # ---- Sanity checks per-trial ----
  if ($baselineRun.Raw.BenchmarkAssemblyHash -ne $candidateRun.Raw.BenchmarkAssemblyHash) {
    throw "Different benchmark binaries detected — refuse comparison."
  }

  $fields = @("Suite", "Depth", "Repeat", "Warmup", "Threads")
  foreach ($field in $fields) {
    $bv = $baselineRun.Raw.$field; $cv = $candidateRun.Raw.$field
    if ($field -eq "Threads") {
      if ($null -eq $bv) { $bv = "default" }
      if ($null -eq $cv) { $cv = "default" }
    }
    if ($bv -ne $cv) { throw ("Incomparable config at trial {0}: {1} differs ({2} vs {3})" -f ($t+1), $field, $bv, $cv) }
  }

  if (-not $baselineRun.Raw.RunId -or -not $candidateRun.Raw.RunId) {
    throw "RunId missing — likely stale JSON."
  }

  # ---- Accumulate row (force numeric types) ----
  $nodesDeltaPercent = Get-PercentageChange -Original $baselineRun.TotalNodes -New $candidateRun.TotalNodes
  $timeDeltaPercent  = Get-PercentageChange -Original $baselineRun.TotalElapsedMs -New $candidateRun.TotalElapsedMs
  $aggNpsDelta       = Get-PercentageChange -Original $baselineRun.AggregateNps -New $candidateRun.AggregateNps

  if ($swapOrder) {
    $orderString = "C then B"
  } else {
    $orderString = "B then C"
  }

  $trialRows += [pscustomobject]@{
    Trial     = [int]($t + 1)
    Order     = $orderString
    BaseNps   = [double][math]::Round($baselineRun.AggregateNps, 0)
    CandNps   = [double][math]::Round($candidateRun.AggregateNps, 0)
    NodesPct  = ($null -ne $nodesDeltaPercent) ? [double]$nodesDeltaPercent : 0.0
    TimePct   = ($null -ne $timeDeltaPercent)  ? [double]$timeDeltaPercent  : 0.0
    AggNpsPct = ($null -ne $aggNpsDelta)       ? [double]$aggNpsDelta       : 0.0
  }

  Set-Progress -Id $ParentProgressId -Activity "Forklift Perf A/B [Trials: $($t+1)/$Trials]" -Status "Summarizing…" -Percent $trialSliceEnd
}

# Finish parent once at the end
Set-Progress -Id $ParentProgressId -Activity "Forklift Perf A/B" -Status "Done" -Percent 100
Set-Progress -Id $ParentProgressId -Completed
$overallBase = 100

# ---- Per-trial table and median (null-safe) ----
$trialCount = ($trialRows | Measure-Object).Count
Write-Host ""
Write-Host "Per-trial aggregate NPS deltas (%):" -ForegroundColor DarkGray
if ($trialCount -gt 0) {
  $trialRows | Format-Table Trial, Order, BaseNps, CandNps, @{n = 'Δ NPS %'; e = { "{0:N3}" -f $_.AggNpsPct } } | Out-String | Write-Host
} else {
  Write-Host "(no trials captured)" -ForegroundColor Yellow
}

if ($trialCount -gt 0) {
  $sortedAgg = $trialRows.AggNpsPct | ForEach-Object { [double]$_ } | Sort-Object
  $aggNpsDeltaPercent = $sortedAgg[[int][math]::Floor($sortedAgg.Count / 2)]
} else {
  $aggNpsDeltaPercent = $null
}

# ---- Threads arg + JSON checks (use last trial's runs) ----
if ($PSBoundParameters.ContainsKey('Threads') -and $Threads -gt 0) {
  $expectEq = "--threads=$Threads"
  $baseArgs = @($baselineRun.Raw.Argv)  | ForEach-Object { $_.ToString() }
  $candArgs = @($candidateRun.Raw.Argv) | ForEach-Object { $_.ToString() }

  if ($baseArgs -contains "--" -or $candArgs -contains "--") {
    throw "A bare '--' reached Program.Main args; remove it from the dotnet invocation. See run logs."
  }

  function Test-HasThreads($argv) {
    if ($argv -contains $expectEq) { return $true }
    for ($i = 0; $i -lt $argv.Count - 1; $i++) {
      if ($argv[$i] -eq "--threads" -and $argv[$i + 1] -eq "$Threads") { return $true }
    }
    return $false
  }

  if (-not (Test-HasThreads $baseArgs)) { throw "Baseline Argv missing '$expectEq'. See .eval/logs/run-baseline.log" }
  if (-not (Test-HasThreads $candArgs)) { throw "Candidate Argv missing '$expectEq'. See .eval/logs/run-candidate.log" }

  if ($null -eq $baselineRun.Raw.Threads -or $baselineRun.Raw.Threads -le 0) {
    throw "Baseline JSON Threads is null/<=0 despite '$expectEq'. Benchmark likely not rebuilt."
  }
  if ($null -eq $candidateRun.Raw.Threads -or $candidateRun.Raw.Threads -le 0) {
    throw "Candidate JSON Threads is null/<=0 despite '$expectEq'. Benchmark likely not rebuilt."
  }
}

Write-Host ""
$fields = @("Suite", "Depth", "Repeat", "Warmup", "Threads")
$differences = @()
foreach ($field in $fields) {
  $baseVal = $baselineRun.Raw.$field
  $candVal = $candidateRun.Raw.$field
  if ($field -eq "Threads") {
    $baseVal = $baseVal ?? "default"
    $candVal = $candVal ?? "default"
  }
  if ($baseVal -ne $candVal) { $differences += $field }
}
Write-Host ("Baseline  → Suite={0} Depth={1} Repeat={2} Warmup={3} Threads={4}" -f `
    $baselineRun.Raw.Suite, $baselineRun.Raw.Depth, $baselineRun.Raw.Repeat, $baselineRun.Raw.Warmup, ($baselineRun.Raw.Threads ?? "default"))
Write-Host ("Candidate → Suite={0} Depth={1} Repeat={2} Warmup={3} Threads={4}" -f `
    $candidateRun.Raw.Suite, $candidateRun.Raw.Depth, $candidateRun.Raw.Repeat, $candidateRun.Raw.Warmup, ($candidateRun.Raw.Threads ?? "default"))

if ($differences.Count -gt 0) {
  Write-Host ("ERROR: Incomparable benchmarks! The following fields differ: {0}" -f ($differences -join ", ")) -ForegroundColor Red
  Complete-Progress -Id $OverallId
  exit 2
}

# --- Summarize
function Get-GeometricMean([double[]]$Values) {
  $sumLog = 0.0
  $n = 0
  foreach ($v in $Values) { if ($v -gt 0) { $sumLog += [math]::Log($v); $n++ } }
  if ($n -eq 0) { return $null }
  return [math]::Exp($sumLog / $n)
}

$nodesDeltaPercent = Get-PercentageChange -Original $baselineRun.TotalNodes     -New $candidateRun.TotalNodes
$timeDeltaPercent  = Get-PercentageChange -Original $baselineRun.TotalElapsedMs -New $candidateRun.TotalElapsedMs

$ratioArray = @()
foreach ($baselinePosition in $baselineRun.Raw.Results) {
  $candidatePosition = $candidateRun.Raw.Results | Where-Object Name -eq $baselinePosition.Name
  if ($null -ne $candidatePosition -and $baselinePosition.NpsMedian -gt 0) {
    $ratioArray += ($candidatePosition.NpsMedian / $baselinePosition.NpsMedian)
  }
}
$geometricMeanRatio = Get-GeometricMean $ratioArray
$gmNpsDeltaPercent = $null
if ($null -ne $geometricMeanRatio) {
  $gmNpsDeltaPercent = [math]::Round((($geometricMeanRatio - 1.0) * 100.0), 3)
}

# If we didn't capture trials (or median was null), fall back to GM; otherwise ensure it's numeric
if ($null -eq $aggNpsDeltaPercent) {
  if ($null -ne $gmNpsDeltaPercent) { $aggNpsDeltaPercent = [double]$gmNpsDeltaPercent } else { $aggNpsDeltaPercent = 0.0 }
} else {
  $aggNpsDeltaPercent = [double]$aggNpsDeltaPercent
}

Write-Host ""
PrintDeltaLine -Name "Nodes"        -BaselineString ($baselineRun.TotalNodes.ToString("N0")) `
  -CandidateString ($candidateRun.TotalNodes.ToString("N0")) `
  -Percent $nodesDeltaPercent
PrintDeltaLine -Name "Elapsed (ms)" -BaselineString ($baselineRun.TotalElapsedMs.ToString("N2")) `
  -CandidateString ($candidateRun.TotalElapsedMs.ToString("N2")) `
  -Percent $timeDeltaPercent -LowerIsBetter
PrintDeltaLine -Name "NPS (GM)"     -BaselineString ([math]::Round($baselineRun.AggregateNps, 0).ToString("N0")) `
  -CandidateString ([math]::Round($candidateRun.AggregateNps, 0).ToString("N0")) `
  -Percent $gmNpsDeltaPercent
PrintDeltaLine -Name "NPS (Agg)"    -BaselineString ([math]::Round($baselineRun.AggregateNps, 0).ToString("N0")) `
  -CandidateString ([math]::Round($candidateRun.AggregateNps, 0).ToString("N0")) `
  -Percent $aggNpsDeltaPercent

# --- Persist artifacts (JSON + Markdown)
$timestampStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$jsonResultPath = Join-Path $evaluationRoot "result-$timestampStamp.json"
$markdownResultPath = Join-Path $evaluationRoot "result-$timestampStamp.md"

# compute for JSON without using "if as expression"
$candidateCommitForJson = 'WORKTREE'
if ($candidateMode -eq 'REF') { $candidateCommitForJson = $candidateLabel }

# Also compute Candidate display for Markdown (avoid $(if ...) in here-string)
$candidateDisplay = 'WORKTREE'
if ($candidateMode -eq 'REF') { $candidateDisplay = $candidateLabel }

$jsonEnvelope = @{
  BaselineCommit   = $baselineCommit
  CandidateCommit  = $candidateCommitForJson
  Suite            = $Suite
  Depth            = $Depth
  Repeat           = $Repeat
  Warmup           = $Warmup
  Threads          = ($Threads -gt 0) ? $Threads : $null
  KeepHistory      = [bool]$KeepHistory
  HighPriority     = [bool]$HighPriority
  BaselineResults  = $baselineRun.Raw
  CandidateResults = $candidateRun.Raw
  Deltas           = @{
    NodesPercent        = $nodesDeltaPercent
    ElapsedPercent      = $timeDeltaPercent
    AggNpsPercentMedian = $aggNpsDeltaPercent
    Trials              = $trialRows
  }
  TimestampUtc     = (Get-Date).ToUniversalTime().ToString("o")
}
$jsonEnvelope | ConvertTo-Json -Depth 6 | Out-File $jsonResultPath -Encoding utf8

$markdown = @"
# Forklift Perf A/B — $timestampStamp

**Baseline**:  `$baselineCommit`
**Candidate**: `$candidateDisplay`

**Suite**: `$( $baselineRun.Raw.Suite )`  **Depth**: `$( $baselineRun.Raw.Depth )`  **Repeat**: `$( $baselineRun.Raw.Repeat )`  **Warmup**: `$( $baselineRun.Raw.Warmup )`  **Threads**: $(if ($baselineRun.Raw.Threads) { "$($baselineRun.Raw.Threads)" } else { "default" })

| Metric       | Baseline | Candidate | Δ % |
|-------------:|---------:|----------:|----:|
| Nodes        | $($baselineRun.TotalNodes) | $($candidateRun.TotalNodes) | $nodesDeltaPercent |
| Elapsed (ms) | $([math]::Round($baselineRun.TotalElapsedMs,2)) | $([math]::Round($candidateRun.TotalElapsedMs,2)) | $timeDeltaPercent |
| NPS (Agg)    | $([math]::Round($baselineRun.AggregateNps,0)) | $([math]::Round($candidateRun.AggregateNps,0)) | $aggNpsDeltaPercent |

Per-position medians:
| Position | Nodes (B → C)  | ms (B → C) | NPS (B → C) |
|---------:|---------------:|-----------:|------------:|
"@

foreach ($baselinePosition in $baselineRun.Raw.Results) {
  $candidatePosition = $candidateRun.Raw.Results | Where-Object Name -eq $baselinePosition.Name
  $n2 = "n/a"; if ($candidatePosition) { $n2 = $candidatePosition.NodesMedian }
  $ms2 = "n/a"; if ($candidatePosition) { $ms2 = [math]::Round($candidatePosition.ElapsedMsMedian, 2) }
  $nps2 = "n/a"; if ($candidatePosition) { $nps2 = [math]::Round($candidatePosition.NpsMedian, 0) }
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
if ($aggNpsDeltaPercent -lt (-$TolerancePercent)) {
  Write-Host ("FAIL: NPS {0:N3}% < -{1:N3}% (regression)." -f $aggNpsDeltaPercent, $TolerancePercent) -ForegroundColor Red
  $script:ExitCode = 1
}
elseif ([math]::Abs($aggNpsDeltaPercent) -le $TolerancePercent) {
  Write-Host ("NEUTRAL: |NPS| {0:N3}% ≤ {1:N3}%." -f [math]::Abs($aggNpsDeltaPercent), $TolerancePercent) -ForegroundColor Yellow
}
else {
  Write-Host ("OK: NPS {0:N3}% > {1:N3}% (improvement)." -f $aggNpsDeltaPercent, $TolerancePercent) -ForegroundColor Green
}

Set-Progress -Id $OverallId -Activity "Forklift Perf A/B" -Status "Complete" -Percent 100
Complete-Progress -Id $OverallId

# --- Cleanup worktrees unless requested
Remove-WorktreeArtifact -Force:$true
exit $script:ExitCode
