param(
  [string]$RepositoryRoot          = "C:\code\Forklift",
  [string]$ArtifactsRoot           = "C:\code\ForkliftArtifacts",
  [string]$BaselineGitRef          = "main",            # or a SHA like 744e2b...
  [string]$Configuration           = "Release",
  [string]$OptionalDefineConstants = "",               # e.g. 'BAKE_TABLES;FLAG'
  [string]$Suite                   = "minimal",
  [int]   $Depth                   = 4,
  [double]$TolerancePct            = 2.0,              # BDNA tolerance for regressions
  [int]   $MaxErrors               = 0,                # 0 => any regression fails
  [string]$Filter                  = "*PerftAABench*", # limit analysis scope
  [int]   $KeepRuns                = 100               # rolling history size
)

$ErrorActionPreference = "Stop"

# --- Derived paths ---
$BaselineOutputDir   = Join-Path $ArtifactsRoot "baseline"
$CandidateOutputDir  = Join-Path $ArtifactsRoot "candidate"
$BaselineWorktreeDir = Join-Path $ArtifactsRoot "_worktrees\baseline"

$ResultsDir          = Join-Path $RepositoryRoot "BenchmarkDotNet.Artifacts\results"
$BdnaRoot            = Join-Path $RepositoryRoot ".eval\bdna"
$AggregatesDir       = Join-Path $BdnaRoot "aggregates"
$ReportsDir          = Join-Path $BdnaRoot "reports"
$OutDir              = Join-Path $ArtifactsRoot ".eval\out"
$SummaryJsonPath     = Join-Path $OutDir "bdna-summary.json"
$SummaryMdPath       = Join-Path $OutDir "bdna-summary.md"

# --- Prep dirs ---
New-Item -ItemType Directory -Force -Path $BaselineOutputDir    | Out-Null
New-Item -ItemType Directory -Force -Path $CandidateOutputDir   | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $BaselineWorktreeDir) | Out-Null
New-Item -ItemType Directory -Force -Path $AggregatesDir,$ReportsDir | Out-Null

# --- Clean any previous temp worktree ---
if (Test-Path $BaselineWorktreeDir) {
  git -C $RepositoryRoot worktree remove --force "$BaselineWorktreeDir" | Out-Null
  if($LASTEXITCODE -ne 0) {
    Write-Error "Failed to remove existing baseline worktree at $BaselineWorktreeDir"
    exit $LASTEXITCODE
  }
}

# --- Create detached worktree at baseline ref ---
git -C $RepositoryRoot worktree add --force "$BaselineWorktreeDir" "$BaselineGitRef"
if($LASTEXITCODE -ne 0) {
  Write-Error "Failed to create baseline worktree at ref '$BaselineGitRef'"
  exit $LASTEXITCODE
}

# --- Restore once (determinism) ---
dotnet restore "$RepositoryRoot\Forklift.sln"
if($LASTEXITCODE -ne 0) {
  Write-Error "Failed to restore Forklift solution."
  exit $LASTEXITCODE
}

# --- Build BASELINE (detached worktree) ---
$BaselineProject   = Join-Path $BaselineWorktreeDir "Forklift.Core\Forklift.Core.csproj"
$BaselineBuildArgs = @("build", $BaselineProject, "-c", $Configuration, "-o", $BaselineOutputDir)
if ($OptionalDefineConstants) { $BaselineBuildArgs += @("-p:DefineConstants=$OptionalDefineConstants") }
dotnet @BaselineBuildArgs
if($LASTEXITCODE -ne 0) {
  Write-Error "Failed to build baseline Forklift.Core at ref '$BaselineGitRef'"
  exit $LASTEXITCODE
}

# --- Build CANDIDATE (current tree) ---
$CandidateProject   = Join-Path $RepositoryRoot "Forklift.Core\Forklift.Core.csproj"
$CandidateBuildArgs = @("build", $CandidateProject, "-c", $Configuration, "-o", $CandidateOutputDir)
if ($OptionalDefineConstants) { $CandidateBuildArgs += @("-p:DefineConstants=$OptionalDefineConstants") }
dotnet @CandidateBuildArgs
if($LASTEXITCODE -ne 0) {
  Write-Error "Failed to build candidate Forklift.Core (current tree)."
  exit $LASTEXITCODE
}

# --- Tear down the temp worktree (artifacts remain) ---
git -C $RepositoryRoot worktree remove --force "$BaselineWorktreeDir"
if($LASTEXITCODE -ne 0) {
  Write-Error "Failed to remove baseline worktree at $BaselineWorktreeDir"
  exit $LASTEXITCODE
}

# --- Paths to feed the harness ---
$BaselineDll  = Join-Path $BaselineOutputDir  "Forklift.Core.dll"
$CandidateDll = Join-Path $CandidateOutputDir "Forklift.Core.dll"
Write-Host "`nBaseline:  $BaselineDll"
Write-Host "Candidate: $CandidateDll`n"

# --- Run the benchmark harness (your Program.cs loads the two DLLs) ---
dotnet run -c $Configuration --project "$RepositoryRoot\Forklift.Benchmark\Forklift.Benchmark.csproj" -- `
  --baseline $BaselineDll `
  --candidate $CandidateDll `
  --suite $Suite `
  --depth $Depth

if ($LASTEXITCODE -ne 0) {
  Write-Error "Benchmark run failed with exit code $LASTEXITCODE"
  exit $LASTEXITCODE
}

# --- Ensure BDNA tool is available (local manifest preferred) ---
# If you already checked in a .config/dotnet-tools.json with bdna, these two lines are no-ops.
if (-not (Test-Path (Join-Path $RepositoryRoot ".config\dotnet-tools.json"))) {
  Push-Location $RepositoryRoot
  dotnet new tool-manifest | Out-Null
  Pop-Location
}
if (-not (dotnet tool list --tool-path $null 2>$null | Select-String -SimpleMatch "bdna")) {
  Push-Location $RepositoryRoot
  dotnet tool install bdna | Out-Null
  Pop-Location
}

# --- Add run metadata for BDNA history ---
$Branch   = (git -C $RepositoryRoot rev-parse --abbrev-ref HEAD)
$Commit   = (git -C $RepositoryRoot rev-parse HEAD)
$Build    = (git -C $RepositoryRoot rev-parse --short=12 HEAD)
$BuildUri = ""

if (-not $BuildUri) {
  if ($env:GITHUB_SERVER_URL -and $env:GITHUB_REPOSITORY -and $env:GITHUB_RUN_ID) {
    $BuildUri = "$($env:GITHUB_SERVER_URL)/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)"
  } elseif ($env:BUILD_REPOSITORY_URI -and $env:BUILD_BUILDID) { # Azure DevOps
    $BuildUri = "$($env:SYSTEM_TEAMFOUNDATIONSERVERURI)$($env:SYSTEM_TEAMPROJECT)/_build/results?buildId=$($env:BUILD_BUILDID)"
  } elseif ($env:CI_PIPELINE_URL) { # GitLab
    $BuildUri = $env:CI_PIPELINE_URL
  }
}

# --- Aggregate latest results into rolling store ---
$aggArgs = @(
  "aggregate",
  "--new",        $ResultsDir,
  "--aggregates", $AggregatesDir,
  "--output",     $AggregatesDir,
  "--runs",       $KeepRuns,
  "--build",      $Build,
  "--branch",     $Branch,
  "--commit",     $Commit
)

if ($BuildUri) {
  $aggArgs += @("--builduri", $BuildUri)
}

& dotnet bdna @aggArgs

if ($LASTEXITCODE -ne 0) {
  Write-Error "BDNA aggregate failed with exit code $LASTEXITCODE"
  exit $LASTEXITCODE
}


# --- Analyse for regressions (fail on any beyond tolerance) ---
# Default statistic is MeanTime; consider --statistic MedianTime if you see jitter.
dotnet bdna analyse `
  --aggregates "$AggregatesDir" `
  --tolerance  $TolerancePct `
  --maxerrors  $MaxErrors `
  --filter     $Filter

if ($LASTEXITCODE -ne 0) {
  Write-Error "Performance regressions detected by BDNA (exit $LASTEXITCODE)."
  exit $LASTEXITCODE
}

# --- (Optional) Export compact reports for CI artifacts ---
dotnet bdna report `
  --aggregates "$AggregatesDir" `
  --reporter csv `
  --reporter json `
  --output    "$ReportsDir"`
  --verbose

if ($LASTEXITCODE -ne 0) {
  Write-Error "BDNA report generation failed with exit code $LASTEXITCODE"
  exit $LASTEXITCODE
}

Write-Host "`nBDNA reports written to: $ReportsDir"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$AggregateFile = Join-Path $AggregatesDir "aggregatebenchmarks.data.json"
if (!(Test-Path $AggregateFile)) {
    Write-Warning "BDNA aggregate file not found: $AggregateFile"
    return
}

# Load the latest run (last element); BDNA writes an array of builds, each with runs[].results[]
$Aggregate = Get-Content $AggregateFile -Raw | ConvertFrom-Json
if (-not $Aggregate) {
    Write-Warning "BDNA aggregate file is empty."
    return
}
$LatestBuild = $Aggregate | Select-Object -Last 1
$LatestRun   = $LatestBuild.runs | Select-Object -Last 1
$Results     = @($LatestRun.results)

# Helper to parse PositionName out of the 'parameters' field
function Get-PositionName([string]$parameters) {
    # examples: "PositionName=kiwipete"
    if ($parameters -match 'PositionName=(.+)$') { return $Matches[1] }
    return $parameters
}

# Shape data into objects we can join on {PositionName, Method}
$Shaped = foreach ($r in $Results) {
    [pscustomobject]@{
        FullName            = $r.fullName
        Type                = $r.type
        Method              = $r.method            # "Baseline" or "Candidate"
        PositionName        = Get-PositionName $r.parameters
        MeanTimeNs          = [double]$r.meanTime  # nanoseconds from BDNA
        MedianTimeNs        = [double]$r.medianTime
        Q1TimeNs            = [double]$r.q1Time
        Q3TimeNs            = [double]$r.q3Time
        MinTimeNs           = [double]$r.minTime
        MaxTimeNs           = [double]$r.maxTime
        Gen0                = [double]$r.gen0Collections
        Gen1                = [double]$r.gen1Collections
        Gen2                = [double]$r.gen2Collections
        BytesAllocatedPerOp = [double]$r.bytesAllocatedPerOp
    }
}

# Group by position and pair Baseline/Candidate
$Pairs = $Shaped | Group-Object PositionName | ForEach-Object {
    $group = $_.Group
    $baseline  = $group | Where-Object { $_.Method -eq 'Baseline' }  | Select-Object -First 1
    $candidate = $group | Where-Object { $_.Method -eq 'Candidate' } | Select-Object -First 1

    if (-not $baseline -or -not $candidate) { return }

    # Convert to milliseconds for readability
    $baseMs = $baseline.MeanTimeNs   / 1e6
    $candMs = $candidate.MeanTimeNs  / 1e6

    $deltaMs   = $candMs - $baseMs
    $deltaPct  = if ($baseMs -ne 0) { ($deltaMs / $baseMs) * 100.0 } else { 0.0 }

    $allocDeltaBytes = $candidate.BytesAllocatedPerOp - $baseline.BytesAllocatedPerOp
    $allocDeltaPct   = if ($baseline.BytesAllocatedPerOp -ne 0) {
        ($allocDeltaBytes / $baseline.BytesAllocatedPerOp) * 100.0
    } else { 0.0 }

    [pscustomobject]@{
        PositionName          = $_.Name
        BaselineMeanMs        = [math]::Round($baseMs, 3)
        CandidateMeanMs       = [math]::Round($candMs, 3)
        DeltaMs               = [math]::Round($deltaMs, 3)
        DeltaPct              = [math]::Round($deltaPct, 2)
        BaselineAllocKB       = [math]::Round($baseline.BytesAllocatedPerOp / 1024.0, 2)
        CandidateAllocKB      = [math]::Round($candidate.BytesAllocatedPerOp / 1024.0, 2)
        AllocDeltaKB          = [math]::Round($allocDeltaBytes / 1024.0, 2)
        AllocDeltaPct         = [math]::Round($allocDeltaPct, 2)
        BaselineGen0          = $baseline.Gen0
        CandidateGen0         = $candidate.Gen0
        BaselineGen1          = $baseline.Gen1
        CandidateGen1         = $candidate.Gen1
        BaselineGen2          = $baseline.Gen2
        CandidateGen2         = $candidate.Gen2
    }
}

# Persist machine-readable summary
$Pairs | ConvertTo-Json -Depth 4 | Out-File -Encoding UTF8 $SummaryJsonPath

# Emit a nice Markdown summary (CI/PR-friendly)
$md = New-Object System.Text.StringBuilder
$null = $md.AppendLine("# Forklift Benchmark Summary (BDNA)")
$null = $md.AppendLine()
$null = $md.AppendLine("| Position | Baseline (ms) | Candidate (ms) | Δ ms | Δ % | Baseline KB | Candidate KB | Δ KB | Δ % | Gen0 B | Gen0 C |")
$null = $md.AppendLine("|---------:|--------------:|---------------:|-----:|----:|------------:|-------------:|-----:|----:|------:|------:|")
foreach ($row in $Pairs | Sort-Object PositionName) {
    $null = $md.AppendLine(('{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}' -f
        ("| **$($row.PositionName)** "),
        (" {0,12:N3} " -f $row.BaselineMeanMs),
        (" {0,13:N3} " -f $row.CandidateMeanMs),
        (" {0,5:N3} "  -f $row.DeltaMs),
        (" {0,4:N2}%"  -f $row.DeltaPct),
        (" {0,10:N2} " -f $row.BaselineAllocKB),
        (" {0,11:N2} " -f $row.CandidateAllocKB),
        (" {0,5:N2} "  -f $row.AllocDeltaKB),
        (" {0,4:N2}%"  -f $row.AllocDeltaPct),
        (" {0,6}"      -f $row.BaselineGen0),
        (" {0,6}"      -f $row.CandidateGen0)
    ))
}
$md.ToString() | Out-File -Encoding UTF8 $SummaryMdPath

Write-Host ""
Write-Host "===== BDNA Summary ====="

$Pairs |
    Sort-Object PositionName |
    Select-Object `
        PositionName,
        @{Name='Base(ms)'; Expression={[math]::Round($_.BaselineMeanMs,3)}},
        @{Name='Cand(ms)'; Expression={[math]::Round($_.CandidateMeanMs,3)}},
        @{Name='Δms'; Expression={[math]::Round($_.DeltaMs,3)}},
        @{Name='Δ%'; Expression={"{0:N2}%" -f $_.DeltaPct}},
        @{Name='BaseKB'; Expression={[math]::Round($_.BaselineAllocKB,2)}},
        @{Name='CandKB'; Expression={[math]::Round($_.CandidateAllocKB,2)}},
        @{Name='ΔKB'; Expression={[math]::Round($_.AllocDeltaKB,2)}},
        @{Name='Δ%Alloc'; Expression={"{0:N2}%" -f $_.AllocDeltaPct}},
        @{Name='G0B'; Expression={$_.BaselineGen0}},
        @{Name='G0C'; Expression={$_.CandidateGen0}} |
    Format-Table -AutoSize | Out-String | Write-Host

Write-Host "(Saved JSON -> $SummaryJsonPath)"
Write-Host "(Saved Markdown -> $SummaryMdPath)"

$Regressions = $Pairs | Where-Object { $_.DeltaPct -gt $TolerancePct }
if ($Regressions) {
    Write-Error ("One or more positions regressed by more than {0}%:" -f $TolerancePct)
    $Regressions | Format-Table -AutoSize | Out-String | Write-Error
    exit 2
}