param(
  [string]$RepositoryRoot,
  [string]$ArtifactsRoot,

  # Refs to compare (both become detached worktrees)
  [string]$BaselineGitRef = "main",
  [string]$CandidateGitRef = "HEAD",

  [string]$Configuration = "Release",
  [string]$OptionalDefineConstants = "",                  # e.g. 'BAKE_TABLES;FLAG'
  [string]$Suite = "minimal",
  [int]   $Depth = 4,

  [double]$TolerancePct = 3.0,                            # BDNA tolerance for regressions
  [int]   $MaxErrors = 0,                                 # 0 => any regression fails
  [string]$Filter = "*.Candidate*",                       # limit analysis scope
  [int]   $KeepRuns = 100,                                # rolling history size

  [switch]$EnableBdna = $false
)

$ErrorActionPreference = "Stop"

# --- Resolve repository root ---
if (-not $RepositoryRoot) {
  try {
    $RepositoryRoot = (git rev-parse --show-toplevel 2>$null).Trim()
  }
  catch {
    $RepositoryRoot = (Split-Path -Path $PSScriptRoot -Parent)
  }
}
$RepositoryRoot = (Resolve-Path $RepositoryRoot).Path

# --- Default artifacts folder: sibling of the repo root ---
if (-not $ArtifactsRoot) {
  $ArtifactsRoot = $env:FORKLIFT_ARTIFACTS
  if (-not $ArtifactsRoot) {
    $repoParent = Split-Path -Path $RepositoryRoot -Parent
    $repoName = Split-Path -Leaf $RepositoryRoot
    $ArtifactsRoot = Join-Path $repoParent ("{0}Artifacts" -f $repoName)
  }
}

# --- Resolve and create if needed ---
$ArtifactsRoot = (Resolve-Path (New-Item -ItemType Directory -Force -Path $ArtifactsRoot)).Path

# --- Validation: ensure artifacts are not inside the repo ---
$repoFull = [IO.Path]::GetFullPath($RepositoryRoot)
$artFull = [IO.Path]::GetFullPath($ArtifactsRoot)

# Normalize for case-insensitive filesystems
if ($artFull.StartsWith($repoFull.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
  throw "❌ ArtifactsRoot '$ArtifactsRoot' must not be inside RepositoryRoot '$RepositoryRoot'. " +
  "Please set -ArtifactsRoot or `$env:FORKLIFT_ARTIFACTS to a sibling folder (e.g., $repoFullArtifacts)."
}

Write-Host "RepositoryRoot: $RepositoryRoot"
Write-Host "ArtifactsRoot : $ArtifactsRoot"

# --- Derived paths ---
$BaselineOutputDir = Join-Path $ArtifactsRoot "baseline"
$CandidateOutputDir = Join-Path $ArtifactsRoot "candidate"
$WorktreesRoot = Join-Path $ArtifactsRoot "_worktrees"
$BaselineWorktreeDir = Join-Path $WorktreesRoot "baseline"
$CandidateWorktreeDir = Join-Path $WorktreesRoot "candidate"

$ResultsDir = Join-Path $RepositoryRoot "BenchmarkDotNet.Artifacts\results"
$BdnaRoot = Join-Path $RepositoryRoot ".eval\bdna"
$AggregatesDir = Join-Path $BdnaRoot "aggregates"
$ReportsDir = Join-Path $BdnaRoot "reports"
$OutDir = Join-Path $ArtifactsRoot ".eval\out"
$SummaryJsonPath = Join-Path $OutDir "bdna-summary.json"
$SummaryMdPath = Join-Path $OutDir "bdna-summary.md"

# --- Prep dirs ---
New-Item -ItemType Directory -Force -Path $BaselineOutputDir, $CandidateOutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $WorktreesRoot, $AggregatesDir, $ReportsDir | Out-Null

# --- Clean any previous temp worktrees ---
foreach ($dir in @($BaselineWorktreeDir, $CandidateWorktreeDir)) {
  if (Test-Path $dir) {
    git -C $RepositoryRoot worktree remove --force "$dir" | Out-Null
    if ($LASTEXITCODE -ne 0) {
      Write-Error "Failed to remove existing worktree at $dir"
      exit $LASTEXITCODE
    }
  }
}

# --- Create two detached worktrees (Baseline & Candidate) ---
git -C $RepositoryRoot worktree add --force "$BaselineWorktreeDir"  "$BaselineGitRef"
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create baseline worktree at ref '$BaselineGitRef'"; exit $LASTEXITCODE }

git -C $RepositoryRoot worktree add --force "$CandidateWorktreeDir" "$CandidateGitRef"
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create candidate worktree at ref '$CandidateGitRef'"; exit $LASTEXITCODE }

# --- Restore once (determinism) ---
dotnet restore "$RepositoryRoot\Forklift.sln"
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to restore Forklift solution."; exit $LASTEXITCODE }

# --- Build BASELINE (detached worktree) ---
$BaselineProject = Join-Path $BaselineWorktreeDir  "Forklift.Core\Forklift.Core.csproj"
$BaselineBuildArgs = @("build", $BaselineProject, "-c", $Configuration, "-o", $BaselineOutputDir)
if ($OptionalDefineConstants) { $BaselineBuildArgs += @("-p:DefineConstants=$OptionalDefineConstants") }
dotnet @BaselineBuildArgs
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to build baseline Forklift.Core at ref '$BaselineGitRef'"; exit $LASTEXITCODE }

# --- Build CANDIDATE (detached worktree) ---
$CandidateProject = Join-Path $CandidateWorktreeDir "Forklift.Core\Forklift.Core.csproj"
$CandidateBuildArgs = @("build", $CandidateProject, "-c", $Configuration, "-o", $CandidateOutputDir)
if ($OptionalDefineConstants) { $CandidateBuildArgs += @("-p:DefineConstants=$OptionalDefineConstants") }
dotnet @CandidateBuildArgs
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to build candidate Forklift.Core at ref '$CandidateGitRef'"; exit $LASTEXITCODE }

# --- Tear down temp worktrees (artifacts remain) ---
foreach ($dir in @($BaselineWorktreeDir, $CandidateWorktreeDir)) {
  git -C $RepositoryRoot worktree remove --force "$dir"
  if ($LASTEXITCODE -ne 0) { Write-Error "Failed to remove worktree at $dir"; exit $LASTEXITCODE }
}

# --- Paths to feed the harness ---
$BaselineDll = Join-Path $BaselineOutputDir  "Forklift.Core.dll"
$CandidateDll = Join-Path $CandidateOutputDir "Forklift.Core.dll"
Write-Host "`nBaseline:  $BaselineDll"
Write-Host "Candidate: $CandidateDll`n"

# --- Run the benchmark harness (Program.cs loads both DLLs) ---
dotnet run -c $Configuration --project "$RepositoryRoot\Forklift.Benchmark\Forklift.Benchmark.csproj" -- `
  --baseline $BaselineDll `
  --candidate $CandidateDll `
  --suite $Suite `
  --depth $Depth

if ($LASTEXITCODE -ne 0) { Write-Error "Benchmark run failed with exit code $LASTEXITCODE"; exit $LASTEXITCODE }

# --- Helper: latest BDN CSV & time parsing (for local summaries) ---
function Get-LatestBdnCsv {
  param([string]$ResultsDir)
  Get-ChildItem -Path $ResultsDir -Filter *.csv -File -ErrorAction SilentlyContinue |
  Sort-Object LastWriteTimeUtc -Descending |
  Select-Object -First 1
}

function Convert-ToNumber([object]$x) {
  if ($null -eq $x) { return $null }
  $s = "$x".Trim()
  if ($s -eq '') { return $null }
  if ($s -match '^\s*([\d\.,]+)\s*([KMB])?/s\s*$') {
    $n = [double]($matches[1] -replace ',', '')
    switch ($matches[2]) { 'K' { return $n * 1e3 } 'M' { return $n * 1e6 } 'B' { return $n * 1e9 } default { return $n } }
  }
  [double]($s -replace ',', '')
}

function Convert-DurationToMs([string]$text) {
  if (-not $text) { return $null }

  # Normalize quotes and whitespace (regular + NBSP + NNBSP + thin)
  $s = $text.Trim() -replace '[\u00A0\u202F\u2009]', ' '
  $s = $s.Trim('"', "'")

  # Normalize unit: map μ (Greek mu) and µ (micro sign) to 'u'
  $s = $s -replace '([0-9]),([0-9]{3})', '$1,$2'  # keep thousands commas
  $s = $s -replace 'μ', 'u'
  $s = $s -replace 'µ', 'u'
  $s = $s -replace '\s+', ' '                     # collapse spaces

  # Match "<number> <unit>"
  if ($s -match '^\s*([\d\.,]+)\s*(ns|us|ms|s)\s*$') {
    $n = [double]($matches[1] -replace ',', '')
    switch ($matches[2]) {
      'ns' { return $n / 1e6 }  # nanoseconds -> ms
      'us' { return $n / 1e3 }  # microseconds -> ms
      'ms' { return $n }        # milliseconds
      's' { return $n * 1e3 }  # seconds -> ms
    }
  }

  # Fallback: try to parse as a bare number (assume ms)
  try { return [double]($s -replace ',', '') } catch { return $null }
}

# --- Optionally run BDNA aggregate/analyse/report (CI) ---
$HadRegressions = $false

if ($EnableBdna) {
  # Ensure tool is available (local manifest preferred)
  if (-not (Test-Path (Join-Path $RepositoryRoot ".config\dotnet-tools.json"))) {
    Push-Location $RepositoryRoot; dotnet new tool-manifest | Out-Null; Pop-Location
  }
  if (-not (dotnet tool list --tool-path $null 2>$null | Select-String -SimpleMatch "bdna")) {
    Push-Location $RepositoryRoot; dotnet tool install bdna | Out-Null; Pop-Location
  }

  # Run metadata
  $Branch = (git -C $RepositoryRoot rev-parse --abbrev-ref HEAD)
  $Commit = (git -C $RepositoryRoot rev-parse HEAD)
  $Build = (git -C $RepositoryRoot rev-parse --short=12 HEAD)
  $BuildUri = ""
  if (-not $BuildUri) {
    if ($env:GITHUB_SERVER_URL -and $env:GITHUB_REPOSITORY -and $env:GITHUB_RUN_ID) {
      $BuildUri = "$($env:GITHUB_SERVER_URL)/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)"
    }
    elseif ($env:BUILD_REPOSITORY_URI -and $env:BUILD_BUILDID) {
      $BuildUri = "$($env:SYSTEM_TEAMFOUNDATIONSERVERURI)$($env:SYSTEM_TEAMPROJECT)/_build/results?buildId=$($env:BUILD_BUILDID)"
    }
    elseif ($env:CI_PIPELINE_URL) {
      $BuildUri = $env:CI_PIPELINE_URL
    }
  }

  # Aggregate into rolling store
  $aggArgs = @(
    "aggregate",
    "--new", $ResultsDir,
    "--aggregates", $AggregatesDir,
    "--output", $AggregatesDir,
    "--runs", $KeepRuns,
    "--build", $Build,
    "--branch", $Branch,
    "--commit", $Commit
  )
  if ($BuildUri) { $aggArgs += @("--builduri", $BuildUri) }

  & dotnet bdna @aggArgs
  if ($LASTEXITCODE -ne 0) { Write-Error "BDNA aggregate failed with exit code $LASTEXITCODE"; exit $LASTEXITCODE }

  # Analyse drift vs store
  dotnet bdna analyse `
    --aggregates "$AggregatesDir" `
    --tolerance  $TolerancePct `
    --maxerrors  $MaxErrors `
    --filter     $Filter `
    --statistic  MedianTime

  if ($LASTEXITCODE -ne 0) {
    $HadRegressions = $true
    Write-Warning "Performance regressions detected by BDNA (exit $LASTEXITCODE)."
  }

  # Export compact reports (CSV/JSON) for CI artifacts
  dotnet bdna report `
    --aggregates "$AggregatesDir" `
    --reporter csv `
    --reporter json `
    --output    "$ReportsDir" `
    --verbose

  if ($LASTEXITCODE -ne 0) { Write-Error "BDNA report generation failed with exit code $LASTEXITCODE"; exit $LASTEXITCODE }

  Write-Host "`nBDNA reports written to: $ReportsDir"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# === SHAPE RESULTS for comparison/table ===
# Path A: BDNA enabled -> read store; Path B: BDNA disabled -> read latest BDN CSV directly.

$Pairs = @()

if ($EnableBdna) {
  $AggregateFile = Join-Path $AggregatesDir "aggregatebenchmarks.data.json"
  if (!(Test-Path $AggregateFile)) { Write-Warning "BDNA aggregate file not found: $AggregateFile"; return }

  $Aggregate = Get-Content $AggregateFile -Raw | ConvertFrom-Json
  if (-not $Aggregate) { Write-Warning "BDNA aggregate file is empty."; return }

  $LatestBuild = $Aggregate | Select-Object -Last 1
  $LatestRun = $LatestBuild.runs | Select-Object -Last 1
  $Results = @($LatestRun.results)

  function Get-PositionName([string]$parameters) {
    if ($parameters -match 'PositionName=(.+)$') { return $Matches[1] }
    return $parameters
  }

  $Shaped = foreach ($r in $Results) {
    [pscustomobject]@{
      FullName            = $r.fullName
      Type                = $r.type
      Method              = $r.method
      PositionName        = Get-PositionName $r.parameters
      MeanTimeNs          = [double]$r.meanTime
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

  $Pairs = $Shaped | Group-Object PositionName | ForEach-Object {
    $group = $_.Group
    $baseline = $group | Where-Object { $_.Method -eq 'Baseline' }  | Select-Object -First 1
    $candidate = $group | Where-Object { $_.Method -eq 'Candidate' } | Select-Object -First 1
    if (-not $baseline -or -not $candidate) { return }

    $baseMs = $baseline.MeanTimeNs / 1e6
    $candMs = $candidate.MeanTimeNs / 1e6
    $deltaMs = $candMs - $baseMs
    $deltaPct = if ($baseMs) { ($deltaMs / $baseMs) * 100.0 } else { 0.0 }

    $allocDeltaBytes = $candidate.BytesAllocatedPerOp - $baseline.BytesAllocatedPerOp
    $allocDeltaPct = if ($baseline.BytesAllocatedPerOp) { ($allocDeltaBytes / $baseline.BytesAllocatedPerOp) * 100.0 } else { 0.0 }

    [pscustomobject]@{
      PositionName     = $_.Name
      BaselineMeanMs   = [math]::Round($baseMs, 3)
      CandidateMeanMs  = [math]::Round($candMs, 3)
      DeltaMs          = [math]::Round($deltaMs, 3)
      DeltaPct         = [math]::Round($deltaPct, 2)
      BaselineAllocKB  = [math]::Round($baseline.BytesAllocatedPerOp / 1024.0, 2)
      CandidateAllocKB = [math]::Round($candidate.BytesAllocatedPerOp / 1024.0, 2)
      AllocDeltaKB     = [math]::Round($allocDeltaBytes / 1024.0, 2)
      AllocDeltaPct    = [math]::Round($allocDeltaPct, 2)
      BaselineGen0     = $baseline.Gen0
      CandidateGen0    = $candidate.Gen0
      BaselineGen1     = $baseline.Gen1
      CandidateGen1    = $candidate.Gen1
      BaselineGen2     = $baseline.Gen2
      CandidateGen2    = $candidate.Gen2
    }
  }

  # Pull Nodes/Op, TotalNodes, Agg NPS from the latest BDN CSV (has the custom columns)
  $latestCsv = Get-LatestBdnCsv -ResultsDir $ResultsDir
  if ($latestCsv) {
    $csvRows = Import-Csv -Path $latestCsv.FullName
    $byKey = @{}
    foreach ($r in $csvRows) { if ($r.PositionName -and $r.Method) { $byKey["$($r.PositionName)|$($r.Method)"] = $r } }
    foreach ($p in $Pairs) {
      $baseRow = $byKey["$($p.PositionName)|Baseline"]
      $candRow = $byKey["$($p.PositionName)|Candidate"]

      $nodesPerOp = if ($candRow -and $candRow.'Nodes/Op') { Convert-ToNumber $candRow.'Nodes/Op' }
      elseif ($baseRow -and $baseRow.'Nodes/Op') { Convert-ToNumber $baseRow.'Nodes/Op' }
      else { $null }

      $baseTotalNodes = if ($baseRow) { Convert-ToNumber $baseRow.'TotalNodes' } else { $null }
      $candTotalNodes = if ($candRow) { Convert-ToNumber $candRow.'TotalNodes' } else { $null }

      $baseAggNpsStr = if ($baseRow) { $baseRow.'Agg NPS' } else { $null }
      $candAggNpsStr = if ($candRow) { $candRow.'Agg NPS' } else { $null }

      Add-Member -InputObject $p -NotePropertyName NodesPerOp     -NotePropertyValue $nodesPerOp -Force
      Add-Member -InputObject $p -NotePropertyName BaseTotalNodes -NotePropertyValue $baseTotalNodes -Force
      Add-Member -InputObject $p -NotePropertyName CandTotalNodes -NotePropertyValue $candTotalNodes -Force
      Add-Member -InputObject $p -NotePropertyName BaseAggNpsStr  -NotePropertyValue $baseAggNpsStr -Force
      Add-Member -InputObject $p -NotePropertyName CandAggNpsStr  -NotePropertyValue $candAggNpsStr -Force
    }
  }

}
else {
  # --- BDNA disabled: read the latest BDN CSV directly ---
  $latestCsv = Get-LatestBdnCsv -ResultsDir $ResultsDir
  if (-not $latestCsv) {
    Write-Error "No BenchmarkDotNet CSV found in $ResultsDir. Cannot build summary."
    exit 1
  }

  $csvRows = Import-Csv -Path $latestCsv.FullName

  # Index by (PositionName, Method)
  $byPosition = $csvRows | Group-Object PositionName
  foreach ($g in $byPosition) {
    $pos = $g.Name
    $baseRow = $g.Group | Where-Object { $_.Method -eq 'Baseline' }  | Select-Object -First 1
    $candRow = $g.Group | Where-Object { $_.Method -eq 'Candidate' } | Select-Object -First 1
    if (-not $baseRow -or -not $candRow) { continue }

    # BDN CSV has "Mean" like "6.752 us" — convert to ms$baseMs = Convert-DurationToMs $baseRow.Mean
    $candMs = Convert-DurationToMs $candRow.Mean

    if ($baseMs -eq $null -or $candMs -eq $null) {
      Write-Warning "Could not parse Mean for '$pos' (base='${($baseRow.Mean)}', cand='${($candRow.Mean)}'). Skipping."
      continue
    }
    
    $deltaMs = $candMs - $baseMs
    $deltaPct = if ($baseMs) { ($deltaMs / $baseMs) * 100.0 } else { 0.0 }

    $baseAllocKB = [math]::Round(([double]($baseRow.Allocated -replace ' KB', '' -replace ',', '')), 2)
    $candAllocKB = [math]::Round(([double]($candRow.Allocated -replace ' KB', '' -replace ',', '')), 2)
    $allocDeltaKB = $candAllocKB - $baseAllocKB
    $allocDeltaPct = if ($baseAllocKB) { ($allocDeltaKB / $baseAllocKB) * 100.0 } else { 0.0 }

    $nodesPerOp =
    if ($candRow.'Nodes/Op') { Convert-ToNumber $candRow.'Nodes/Op' }
    elseif ($baseRow.'Nodes/Op') { Convert-ToNumber $baseRow.'Nodes/Op' }
    else { $null }

    $baseTotalNodes = if ($baseRow.'TotalNodes') { Convert-ToNumber $baseRow.'TotalNodes' } else { $null }
    $candTotalNodes = if ($candRow.'TotalNodes') { Convert-ToNumber $candRow.'TotalNodes' } else { $null }

    $baseAggNpsStr = $baseRow.'Agg NPS'
    $candAggNpsStr = $candRow.'Agg NPS'

    $Pairs += [pscustomobject]@{
      PositionName     = $pos
      BaselineMeanMs   = [math]::Round($baseMs, 3)
      CandidateMeanMs  = [math]::Round($candMs, 3)
      DeltaMs          = [math]::Round($deltaMs, 3)
      DeltaPct         = [math]::Round($deltaPct, 2)
      BaselineAllocKB  = $baseAllocKB
      CandidateAllocKB = $candAllocKB
      AllocDeltaKB     = [math]::Round($allocDeltaKB, 2)
      AllocDeltaPct    = [math]::Round($allocDeltaPct, 2)
      BaselineGen0     = $baseRow.Gen0
      CandidateGen0    = $candRow.Gen0
      NodesPerOp       = $nodesPerOp
      BaseTotalNodes   = $baseTotalNodes
      CandTotalNodes   = $candTotalNodes
      BaseAggNpsStr    = $baseAggNpsStr
      CandAggNpsStr    = $candAggNpsStr
    }
  }
}

# Persist machine-readable summary
$Pairs | ConvertTo-Json -Depth 4 | Out-File -Encoding UTF8 $SummaryJsonPath

# Emit Markdown summary
$md = New-Object System.Text.StringBuilder
$null = $md.AppendLine("# Forklift Benchmark Summary" + ($(if ($EnableBdna) { " (BDNA)" } else { "" })))
$null = $md.AppendLine()
$null = $md.AppendLine("| Position | Baseline (ms) | Candidate (ms) | Δ ms | Δ % | Nodes/Op | Base TotalNodes | Cand TotalNodes | Base AggNPS | Cand AggNPS | Baseline KB | Candidate KB | Δ KB | Δ % | Gen0 B | Gen0 C |")
$null = $md.AppendLine("|---------:|--------------:|---------------:|-----:|----:|---------:|----------------:|----------------:|:-----------:|:-----------:|------------:|-------------:|-----:|----:|------:|------:|")

foreach ($row in $Pairs | Sort-Object PositionName) {
  $nodesPerOp = if ($row.NodesPerOp -ne $null) { '{0:N0}' -f $row.NodesPerOp } else { '' }
  $baseTot = if ($row.BaseTotalNodes -ne $null) { '{0:N0}' -f $row.BaseTotalNodes } else { '' }
  $candTot = if ($row.CandTotalNodes -ne $null) { '{0:N0}' -f $row.CandTotalNodes } else { '' }
  $baseNps = if ($row.BaseAggNpsStr) { $row.BaseAggNpsStr } else { '' }
  $candNps = if ($row.CandAggNpsStr) { $row.CandAggNpsStr } else { '' }

  $null = $md.AppendLine(('{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11}|{12}|{13}|{14}|{15}' -f
      ("| **$($row.PositionName)** "),
      (" {0,12:N3} " -f $row.BaselineMeanMs),
      (" {0,13:N3} " -f $row.CandidateMeanMs),
      (" {0,5:N3} " -f $row.DeltaMs),
      (" {0,4:N2}%" -f $row.DeltaPct),
      (" {0,9}" -f $nodesPerOp),
      (" {0,16}" -f $baseTot),
      (" {0,16}" -f $candTot),
      (" {0,11}" -f $baseNps),
      (" {0,11}" -f $candNps),
      (" {0,10:N2} " -f $row.BaselineAllocKB),
      (" {0,11:N2} " -f $row.CandidateAllocKB),
      (" {0,5:N2} " -f $row.AllocDeltaKB),
      (" {0,4:N2}%" -f $row.AllocDeltaPct),
      (" {0,6}" -f $row.BaselineGen0),
      (" {0,6}" -f $row.CandidateGen0)
    ))
}
$md.ToString() | Out-File -Encoding UTF8 $SummaryMdPath

Write-Host ""
Write-Host "===== Summary ====="
$Pairs |
Sort-Object PositionName |
Select-Object `
@{Name = 'Position'; Expression = { $_.PositionName } },
@{Name = 'Base(ms)'; Expression = { [math]::Round($_.BaselineMeanMs, 3) } },
@{Name = 'Cand(ms)'; Expression = { [math]::Round($_.CandidateMeanMs, 3) } },
@{Name = 'Δms'; Expression = { [math]::Round($_.DeltaMs, 3) } },
@{Name = 'Δ%'; Expression = { "{0:N2}%" -f $_.DeltaPct } },
@{Name = 'Nodes/Op'; Expression = { if ($_.NodesPerOp -ne $null) { '{0:N0}' -f $_.NodesPerOp } else { '' } } },
@{Name = 'BaseNodes'; Expression = { if ($_.BaseTotalNodes -ne $null) { '{0:N0}' -f $_.BaseTotalNodes } else { '' } } },
@{Name = 'CandNodes'; Expression = { if ($_.CandTotalNodes -ne $null) { '{0:N0}' -f $_.CandTotalNodes } else { '' } } },
@{Name = 'BaseAggNPS'; Expression = { $_.BaseAggNpsStr } },
@{Name = 'CandAggNPS'; Expression = { $_.CandAggNpsStr } },
@{Name = 'BaseKB'; Expression = { [math]::Round($_.BaselineAllocKB, 2) } },
@{Name = 'CandKB'; Expression = { [math]::Round($_.CandidateAllocKB, 2) } },
@{Name = 'ΔKB'; Expression = { [math]::Round($_.AllocDeltaKB, 2) } },
@{Name = 'Δ%Alloc'; Expression = { "{0:N2}%" -f $_.AllocDeltaPct } } |
Format-Table -AutoSize | Out-String | Write-Host

Write-Host "(Saved JSON -> $SummaryJsonPath)"
Write-Host "(Saved Markdown -> $SummaryMdPath)"

# If BDNA was enabled and flagged regressions, fail now (after writing artifacts)
if ($EnableBdna -and $HadRegressions) {
  exit 2
}
