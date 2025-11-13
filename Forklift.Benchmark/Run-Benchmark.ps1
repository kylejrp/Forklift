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
  [int]   $Threads,
  [switch]$ParallelRoot,

  [double]$TolerancePct = 1.0,                            # BDNA tolerance for regressions
  [int]   $MaxErrors = 0,                                 # 0 => any regression fails
  [string]$Filter = "Candidate",                       # limit analysis scope
  [int]   $KeepRuns = 100,                                # rolling history size

  [switch]$EnableBdna = $false,
  [switch]$Quiet = $false
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
  throw "This script requires pwsh / PowerShell 7+"
}

$script:createdWorktrees = @()   # track for cleanup
$script:powerPlanChanged = $false
$script:originalSchemeGuid = $null

function Say($msg) { if (-not $Quiet) { Write-Host $msg } }

function Add-Worktree {
  param(
    [Parameter(Mandatory)] [string]$Path,
    [Parameter(Mandatory)] [string]$Ref
  )
  git worktree add --detach $Path $Ref
  if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create worktree at $Path for ref '$Ref'"
    exit $LASTEXITCODE
  }
  $script:createdWorktrees += $Path
}

function Remove-WorktreeSafe {
  param([Parameter(Mandatory)] [string]$Path)
  try { if (Test-Path $Path) { git worktree remove --force $Path 2>$null } } catch {}
  try { if (Test-Path $Path) { Remove-Item -Recurse -Force $Path -ErrorAction SilentlyContinue } } catch {}
}

function Ensure-HighPerf {
  try {
    $HIGHPERF_SCHEME_GUID = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'
    $script:originalSchemeGuid = ((powercfg /GETACTIVESCHEME) -replace '.*GUID:\s+([a-fA-F0-9-]+).*', '$1').ToLower()
    if ($script:originalSchemeGuid -ne $HIGHPERF_SCHEME_GUID) {
      powercfg /SETACTIVE SCHEME_MIN
      $script:powerPlanChanged = $true
    }
  }
  catch {}
}

function Restore-PowerPlan {
  if ($script:powerPlanChanged -and $script:originalSchemeGuid) {
    try { powercfg /SETACTIVE $script:originalSchemeGuid } catch {}
  }
}

function Test-IsCI {
  # Common CI environment flags across providers
  $vars = @(
    'GITHUB_ACTIONS', 'CI', 'TF_BUILD', 'BUILD_BUILDID', 'TEAMCITY_VERSION', 'JENKINS_URL',
    'GITLAB_CI', 'CIRCLECI', 'TRAVIS', 'APPVEYOR', 'BUILDKITE', 'DRONE'
  )
  foreach ($v in $vars) {
    $val = [Environment]::GetEnvironmentVariable($v)
    if ($val -and $val.Trim().ToLowerInvariant() -ne 'false' -and $val.Trim() -ne '0') { return $true }
  }
  return $false
}


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

# Normalize with a trailing separator on the repo (so siblings don't match)
$ds = [IO.Path]::DirectorySeparatorChar
$repoFull = [IO.Path]::GetFullPath( ($RepositoryRoot.TrimEnd('\', '/')) + $ds )
$artFull = [IO.Path]::GetFullPath( $ArtifactsRoot )
$rel = [IO.Path]::GetRelativePath($repoFull, $artFull)

# Inside if the relative path doesn't climb out (doesn't start with '..') and isn't the repo itself ('.')
$inside = ($rel -ne '.') -and (-not $rel.StartsWith('..'))

if ($inside) {
  $repoParent = Split-Path -Path $repoFull -Parent
  $repoName = Split-Path -Leaf  $repoFull.TrimEnd('\', '/')
  $suggest = Join-Path $repoParent ("{0}Artifacts" -f $repoName)

  throw "❌ ArtifactsRoot '$ArtifactsRoot' must NOT be inside RepositoryRoot '$RepositoryRoot'." +
  "`n   Suggested sibling: $suggest" +
  "`n   Override via: -ArtifactsRoot <path>  or  `$env:FORKLIFT_ARTIFACTS=<path>"
}

# --- Derived paths ---
$BaselineOutputDir = Join-Path $ArtifactsRoot "baseline"
$CandidateOutputDir = Join-Path $ArtifactsRoot "candidate"
$WorktreesRoot = Join-Path $ArtifactsRoot "_worktrees"
$BaselineWorktreeDir = Join-Path $WorktreesRoot "baseline"
$CandidateWorktreeDir = Join-Path $WorktreesRoot "candidate"

$ResultsDir = Join-Path $RepositoryRoot "BenchmarkDotNet.Artifacts\results"
$BdnaRoot = Join-Path $RepositoryRoot ".eval\bdna"
$AggregatesDir = Join-Path $BdnaRoot "aggregates\$Suite"
$ReportsDir = Join-Path $BdnaRoot "reports"
$OutDir = Join-Path $ArtifactsRoot ".eval\out"
$BdnaSummaryJsonPath = Join-Path $OutDir "bdna-summary.json"
$BdnaSummaryMdPath = Join-Path $OutDir "bdna-summary.md"
$AbSummaryJsonPath = Join-Path $OutDir "ab-summary.json"
$AbSummaryMdPath = Join-Path $OutDir "ab-summary.md"

# --- Prep dirs ---
New-Item -ItemType Directory -Force -Path $BaselineOutputDir, $CandidateOutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $WorktreesRoot, $AggregatesDir, $ReportsDir, $OutDir | Out-Null

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

try {
  Push-Location $RepositoryRoot

  Ensure-HighPerf

  # --- Create two detached worktrees (Baseline & Candidate) ---
  Add-Worktree -Path $BaselineWorktreeDir  -Ref $BaselineGitRef
  if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create baseline worktree at ref '$BaselineGitRef'"; exit $LASTEXITCODE }

  $baseRef = (git -C $BaselineWorktreeDir rev-parse --short=12 HEAD)
  Say "Baseline worktree at $BaselineGitRef -> $baseRef"

  Add-Worktree -Path $CandidateWorktreeDir -Ref $CandidateGitRef
  if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create candidate worktree at ref '$CandidateGitRef'"; exit $LASTEXITCODE }

  $candRef = (git -C $CandidateWorktreeDir rev-parse --short=12 HEAD)
  Say "Candidate worktree at $CandidateGitRef -> $candRef"

  # --- Optionally apply uncommitted diff if CandidateGitRef == HEAD (not CI) ---
  if ($CandidateGitRef -eq "HEAD" -and -not (Test-IsCI)) {
    Write-Host "Applying local uncommitted diff to candidate worktree..."

    # Apply staged + unstaged changes (tracked files) against HEAD directly to the candidate worktree
    & git -C $RepositoryRoot diff --binary --no-color HEAD | git -C $CandidateWorktreeDir apply --whitespace=nowarn -
    if ($LASTEXITCODE -ne 0) {
      Write-Warning "⚠️ Failed to apply local diff to candidate worktree; continuing with clean commit version."
    }
    else {
      Write-Host "✅ Local diff applied to candidate worktree."
    }

    # Copy untracked (non-ignored) files as well
    $untracked = git -C $RepositoryRoot ls-files --others --exclude-standard
    foreach ($file in $untracked) {
      $src = Join-Path $RepositoryRoot $file
      $dst = Join-Path $CandidateWorktreeDir $file
      New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
      Copy-Item $src $dst -Force -ErrorAction SilentlyContinue
    }
    if ($untracked) { Write-Host "✅ Copied untracked files into candidate worktree." }

  }
  else {
    if (Test-IsCI) { Write-Host "CI detected; skipping local-diff application." }
  }

  # --- Restore once (determinism) ---
  dotnet restore "$RepositoryRoot\Forklift.sln"
  if ($LASTEXITCODE -ne 0) { Write-Error "Failed to restore Forklift solution."; exit $LASTEXITCODE }

  # Shared deterministic flags
  $RepoRoot = Resolve-Path $RepositoryRoot
  $DeterministicFlags = @(
    "-p:Deterministic=true",
    "-p:ContinuousIntegrationBuild=true",
    "-p:DebugType=portable",
    "-p:EmbedAllSources=false",
    "-p:PathMap=$RepoRoot=/src",
    "-p:RepositoryRoot=$RepoRoot",
    "-p:UseSharedCompilation=false",
    "-p:TreatWarningsAsErrors=false"
  )

  # --- Build BASELINE (detached worktree) ---
  $BaselineProject = Join-Path $BaselineWorktreeDir "Forklift.Core\Forklift.Core.csproj"
  $BaselineBuildArgs = @("build", $BaselineProject, "-c", $Configuration, "-o", $BaselineOutputDir) + $DeterministicFlags
  if ($OptionalDefineConstants) { $BaselineBuildArgs += @("-p:DefineConstants=$OptionalDefineConstants") }

  dotnet @BaselineBuildArgs
  if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build baseline Forklift.Core at ref '$BaselineGitRef'"
    exit $LASTEXITCODE
  }

  # --- Build CANDIDATE (detached worktree) ---
  $CandidateProject = Join-Path $CandidateWorktreeDir "Forklift.Core\Forklift.Core.csproj"
  $CandidateBuildArgs = @("build", $CandidateProject, "-c", $Configuration, "-o", $CandidateOutputDir) + $DeterministicFlags
  if ($OptionalDefineConstants) { $CandidateBuildArgs += @("-p:DefineConstants=$OptionalDefineConstants") }

  dotnet @CandidateBuildArgs
  if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build candidate Forklift.Core at ref '$CandidateGitRef'"
    exit $LASTEXITCODE
  }
  function Get-BranchLabel {
    param(
      [string]$CandidateWorktreeDir,
      [string]$BaselineGitRef,
      [string]$CandidateGitRef
    )
    # CI-first: prefer GitHub env if available
    $ghRefName = $env:GITHUB_REF_NAME
    $ghHeadRef = $env:GITHUB_HEAD_REF   # PR head
    $ghBaseRef = $env:GITHUB_BASE_REF   # PR base
    if ($ghHeadRef) { return $ghHeadRef }              # pull_request runs
    if ($ghRefName) { return $ghRefName }              # push / schedule
    # Local / detached: try symbolic-ref, else fall back to input refs
    $sym = (& git -C $CandidateWorktreeDir symbolic-ref --short -q HEAD 2>$null)
    if ($sym) { return $sym }
    if ($BaselineGitRef -eq 'main') { return 'main' }
    if ($CandidateGitRef -and $CandidateGitRef -notmatch '^[0-9a-f]{7,40}$') { return $CandidateGitRef }
    return 'local'
  }

  $Branch = Get-BranchLabel -CandidateWorktreeDir $CandidateWorktreeDir `
    -BaselineGitRef $BaselineGitRef `
    -CandidateGitRef $CandidateGitRef
  $Commit = (git -C $CandidateWorktreeDir rev-parse HEAD)
  $Build = (git -C $CandidateWorktreeDir rev-parse --short=12 HEAD)

  Write-Host "Candidate build info: Branch='$Branch' Commit='$Commit' Build='$Build'"

  # --- Calculate if builds are the byte-identical ---
  # --- Define DLL output paths ---
  $BaselineDll = Join-Path $BaselineOutputDir  "Forklift.Core.dll"
  $CandidateDll = Join-Path $CandidateOutputDir "Forklift.Core.dll"

  # Sanity check: ensure both exist
  if (-not (Test-Path $BaselineDll)) { Write-Error "Missing baseline DLL at $BaselineDll"; exit 1 }
  if (-not (Test-Path $CandidateDll)) { Write-Error "Missing candidate DLL at $CandidateDll"; exit 1 }

  $BaselineHash = (Get-FileHash -Algorithm SHA256 $BaselineDll).Hash
  $CandidateHash = (Get-FileHash -Algorithm SHA256 $CandidateDll).Hash

  Write-Host "Baseline SHA256 : $BaselineHash"
  Write-Host "Candidate SHA256: $CandidateHash"

  if ($BaselineHash -ne $CandidateHash) {
    Say "✅  Baseline and Candidate DLLs differ in content!"
  }
  else {
    Write-Warning "⚠️ Baseline and Candidate DLLs are byte-identical."
  }

  # --- Tear down temp worktrees (artifacts remain) ---
  foreach ($dir in @($BaselineWorktreeDir, $CandidateWorktreeDir)) {
    git -C $RepositoryRoot worktree remove --force "$dir"
    if ($LASTEXITCODE -ne 0) { Write-Error "Failed to remove worktree at $dir"; exit $LASTEXITCODE }
  }

  # --- Paths to feed the harness ---
  $BaselineDll = Join-Path $BaselineOutputDir  "Forklift.Core.dll"
  $CandidateDll = Join-Path $CandidateOutputDir "Forklift.Core.dll"
  Say "`nBaseline:  $BaselineDll"
  Say "Candidate: $CandidateDll`n"

  # --- Run the benchmark harness (Program.cs loads both DLLs) ---
  $benchArgs = @(
    '--baseline', $BaselineDll,
    '--candidate', $CandidateDll,
    '--suite', $Suite,
    '--depth', $Depth
  )
  if ($PSBoundParameters.ContainsKey('Threads')) { $benchArgs += @('--threads', $Threads) }
  if ($ParallelRoot.IsPresent) { $benchArgs += '--parallelRoot' }

  $benchProject = Join-Path $RepositoryRoot "Forklift.Benchmark/Forklift.Benchmark.csproj"

  dotnet run -c $Configuration --project $benchProject -- @benchArgs

  if ($LASTEXITCODE -ne 0) { Write-Error "Benchmark run failed with exit code $LASTEXITCODE"; exit $LASTEXITCODE }

  # --- Helper: latest BDN JSON & time parsing (for local summaries) ---
  function Get-LatestFullReportJsonFile {
    param([string]$ResultsDir)
    $c = Get-ChildItem -Path $ResultsDir -Filter *-report-full.json -File -ErrorAction SilentlyContinue
    if (-not $c) { throw "No *-report-full.json found in $ResultsDir" }
    $c | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
  }

  $full = Get-LatestFullReportJsonFile -ResultsDir $ResultsDir
  $latestJson = Get-Content -Raw -Encoding UTF8 $full.FullName | ConvertFrom-Json

  if ($null -eq $latestJson -or -not $latestJson.PSObject.Properties['Benchmarks']) {
    Write-Error "Unexpected JSON. Expected top-level object with Benchmarks[]. File: $($full.FullName)"
    exit 1
  }
  $rows = $latestJson.Benchmarks

  if (-not $rows -or $rows.Count -eq 0) {
    throw "No benchmarks found in '$($latestJson.FullName)'."
  }
  Say ("Found {0} benchmark row(s) in JSON" -f $rows.Count)

  function Parse-Params([string]$qs) {
    $m = @{}
    foreach ($kv in $qs.Split('&')) {
      $k, $v = $kv.Split('=', 2)
      if ($null -ne $k -and $k -ne '') { $m[$k] = $v }
    }
    return $m
  }
  function Get-Role([object]$r) {
    # Only accept Method exactly Baseline/Candidate (case-insensitive)
    $m = [string]$r.Method
    if ($m -match '^(?i)Baseline$') { return 'Baseline' }
    if ($m -match '^(?i)Candidate$') { return 'Candidate' }
    return $null
  }

  $byKey = @{}
  $diag = @()

  foreach ($r in $rows) {
    $role = Get-Role $r
    $ok = $true
    $why = @()

    if (-not $role) { $ok = $false; $why += 'Method not Baseline/Candidate' }

    $params = $null
    if ($ok) {
      if (-not $r.PSObject.Properties['Parameters']) { $ok = $false; $why += 'Missing Parameters' }
      else { $params = Parse-Params ([string]$r.Parameters) }
    }

    # Parameters are a query string: "PositionName=startpos&Depth=1&Threads=0&ParallelRoot=False"
    # Extract and normalize key parts
    $pos = $null; $dep = $null; $thr = $null; $par = $null
    if ($ok) {
      $pos = [string]$params['PositionName']
      $dep = if ($params['Depth']) { [int]$params['Depth'] } else { $null }

      # Normalize Threads: '?' or empty → $null; digits → int
      if ($params['Threads'] -and $params['Threads'] -match '^\d+$') { $thr = [int]$params['Threads'] }
      elseif (-not $params['Threads'] -or $params['Threads'] -eq '?' ) { $thr = $null }
      else { $thr = $null } # any other token → treat as unspecified

      # Normalize ParallelRoot
      if ($params['ParallelRoot'] -match '^(?i:true)$') { $par = $true }
      elseif ($params['ParallelRoot'] -match '^(?i:false)$') { $par = $false }
      else { $par = $null }

      if (-not $pos -or $dep -eq $null) { $ok = $false; $why += 'Missing PositionName/Depth' }
    }

    # Pull statistic: Median (ns) → convert to μs for our A/B output
    $us = $null
    if ($ok) {
      $hasStats = $r.PSObject.Properties['Statistics'] -and $r.Statistics.PSObject.Properties['Median']
      if (-not $hasStats) { $ok = $false; $why += 'Missing Statistics.Median' }
      else { $us = [double]$r.Statistics.Median / 1000.0 }  # ns → μs
    }

    # Form the composite key (empty for nullables to align Baseline/Candidate)
    if ($ok) {
      $thrKey = if ($thr -eq $null) { '' } else { [string]$thr }
      $parKey = if ($par -eq $null) { '' } else { if ($par) { 'True' } else { 'False' } }
      $key = "$pos|$dep|$thrKey|$parKey"

      if (-not $byKey.ContainsKey($key)) {
        $byKey[$key] = @{
          Pos = $pos; Dep = $dep; Thr = $thr; Par = $par
          Baseline = $null
          Candidate = $null
        }
      }
      $byKey[$key][$role] = $us
      $diag += [pscustomobject]@{ Key = $key; Role = $role; Us = $us; OK = $true; Why = "" }
    }
    else {
      $diag += [pscustomobject]@{ Key = ""; Role = ""; Us = $null; OK = $false; Why = ($why -join '; ') }
    }
  }

  # Warn if any rows lacked a role
  $unclassified = @($rows | Where-Object { -not (Get-Role $_) })
  if ($unclassified.Count -gt 0) {
    Write-Warning ("Found {0} benchmark row(s) without a Baseline/Candidate role. First few method names: {1}" -f `
        $unclassified.Count, (($unclassified | Select-Object -First 5 | ForEach-Object { "" + $_.Method }) -join ', '))
  }

  # Build paired comparisons
  $Pairs = @()
  foreach ($e in $byKey.GetEnumerator()) {
    $v = $e.Value
    if ($v.Baseline -ne $null -and $v.Candidate -ne $null) {
      $Pairs += [pscustomobject]@{
        PositionName = $v.Pos
        Depth        = $v.Dep
        Threads      = $v.Thr
        ParallelRoot = $v.Par
        BaselineUs   = [double]$v.Baseline
        CandidateUs  = [double]$v.Candidate
        DeltaUs      = [double]$v.Candidate - [double]$v.Baseline
        DeltaPct     = if ($v.Baseline -ne 0) { (([double]$v.Candidate - [double]$v.Baseline) / [double]$v.Baseline) * 100.0 } else { $null }
      }
    }
  }

  if (-not $Pairs -or $Pairs.Count -eq 0) {
    Write-Warning "No A/B pairs formed. First few row diagnostics:"
    $diag | Select-Object -First 6 | Format-Table -AutoSize | Out-String | Write-Host
  }

  # --- Optionally run BDNA aggregate/analyse/report (CI) ---
  $HadRegressions = $false

  if ($EnableBdna) {
    Push-Location $RepositoryRoot
    dotnet tool restore | Out-Null
    try { & dotnet tool run bdna --version | Out-Null }
    catch { dotnet tool install --local bdna | Out-Null }
    Pop-Location

    # Run metadata
    $BuildUri = ""
    if (-not $BuildUri) {
      if ($env:GITHUB_SERVER_URL -and $env:GITHUB_REPOSITORY -and $env:GITHUB_RUN_ID) {
        $BuildUri = "$($env:GITHUB_SERVER_URL)/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)"
      }
      elseif ($env:CI_PIPELINE_URL) {
        $BuildUri = $env:CI_PIPELINE_URL
      }
      else {
        Write-Warning "No build URI could be determined from environment variables."
      }
    }

    # Warn if no historical data found for BDNA trend comparison
    if (-not (Test-Path (Join-Path $AggregatesDir "*"))) {
      Write-Warning "No existing BDNA aggregate data found at '$AggregatesDir'."
      Write-Warning "This run will initialize a new history store — future PRs will compare against this baseline."
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

    Say ("dotnet tool run bdna " + ($aggArgs -join ' '))

    & dotnet tool run bdna @aggArgs
    if ($LASTEXITCODE -ne 0) { Write-Error "BDNA aggregate failed with exit code $LASTEXITCODE"; exit $LASTEXITCODE }

    Say "dotnet tool run bdna analyse --aggregates $AggregatesDir --tolerance $TolerancePct --maxerrors $MaxErrors --filter $Filter --statistic MedianTime"

    # Analyse drift vs store
    dotnet tool run bdna analyse `
      --aggregates "$AggregatesDir" `
      --tolerance  $TolerancePct `
      --maxerrors  $MaxErrors `
      --filter     $Filter `
      --statistic  MedianTime

    if ($LASTEXITCODE -ne 0) {
      $HadRegressions = $true
      Write-Warning "Performance regressions detected by BDNA (exit $LASTEXITCODE)."
    }

    # the command we're about to run
    Say "dotnet tool run bdna report --aggregates $AggregatesDir --reporter csv --reporter json --output $ReportsDir --verbose"

    # Export compact reports (CSV/JSON) for CI artifacts
    dotnet tool run bdna report `
      --aggregates "$AggregatesDir" `
      --reporter csv `
      --reporter json `
      --output    "$ReportsDir" `
      --verbose

    if ($LASTEXITCODE -ne 0) { Write-Error "BDNA report generation failed with exit code $LASTEXITCODE"; exit $LASTEXITCODE }

    Say "`nBDNA reports written to: $ReportsDir"

    # -----------------------------
    # Build BDNA Trend Summary
    # -----------------------------
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

    $NoBdnaHistory = $false
    if (-not (Get-ChildItem -Path $AggregatesDir -Recurse -ErrorAction SilentlyContinue)) {
      Write-Warning "No BDNA aggregates found from main history; trend analysis will start fresh for this run."
      $NoBdnaHistory = $true
    }

    # Pull trend deltas from the compact report
    $BenchJsonPath = Join-Path $ReportsDir "benchmarks.json"
    $TrendMedianDeltaPct = $null
    $TrendWorstDeltaPct = $null
    $TrendBestDeltaPct = $null
    $ComparedRows = 0

    if (Test-Path $BenchJsonPath) {
      try {
        $root = Get-Content -Raw -Encoding UTF8 $BenchJsonPath | ConvertFrom-Json
        if ($root -isnot [System.Array]) {
          throw "Unexpected JSON: expected a top-level array of benchmark objects."
        }

        # Build maps of median times per key for Baseline and Candidate
        $baselineMap = @{}
        $candidateMap = @{}

        function Get-Key([object]$cell) {
          # Stable key priority: commitSha -> buildNumber -> creation
          if ($cell.PSObject.Properties['commitSha'] -and $cell.commitSha) { return "commit:$($cell.commitSha)" }
          if ($cell.PSObject.Properties['buildNumber'] -and $cell.buildNumber) { return "build:$($cell.buildNumber)" }
          if ($cell.PSObject.Properties['creation'] -and $cell.creation) { return "ts:$($cell.creation)" }
          return $null
        }

        foreach ($bench in $root) {
          if (-not $bench.PSObject.Properties['method']) { continue }
          if (-not $bench.PSObject.Properties['cells']) { continue }

          $role = '' + $bench.method  # "Baseline" or "Candidate"
          foreach ($cell in $bench.cells) {
            $k = Get-Key $cell
            if (-not $k) { continue }

            # We use medianTime for trend (matches your BDNA analyse --statistic MedianTime)
            if (-not $cell.PSObject.Properties['medianTime']) { continue }
            $v = [double]$cell.medianTime

            if ($role -eq 'Baseline') {
              # Last write wins if duplicate key appears; that’s fine for rolling updates
              $baselineMap[$k] = $v
            }
            elseif ($role -eq 'Candidate') {
              $candidateMap[$k] = $v
            }
          }
        }

        # Pair by intersecting keys and compute delta%
        $trendPairs = @()
        foreach ($k in $baselineMap.Keys) {
          if ($candidateMap.ContainsKey($k)) {
            $b = $baselineMap[$k]
            $c = $candidateMap[$k]
            if ($b -ne $null -and $b -ne 0 -and $c -ne $null) {
              $trendPairs += (($c - $b) / $b) * 100.0
            }
          }
        }

        if ($trendPairs.Count -gt 0) {
          $ComparedRows = $trendPairs.Count
          $sorted = @($trendPairs) ; [Array]::Sort($sorted)
          $n = $sorted.Count
          if ($n % 2 -eq 1) { $TrendMedianDeltaPct = [math]::Round($sorted[ [int]([math]::Floor($n / 2)) ], 2) }
          else { $TrendMedianDeltaPct = [math]::Round( ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2.0, 2) }
          $TrendWorstDeltaPct = [math]::Round($sorted[-1], 2)
          $TrendBestDeltaPct = [math]::Round($sorted[0], 2)
        }
      }
      catch {
        Write-Warning "Failed to parse BDNA benchmarks.json (strict parser): $($_.Exception.Message)"
      }
    }

    $bdnaSummary = [pscustomobject]@{
      HasBdnaRegression   = $HadRegressions
      TolerancePct        = $TolerancePct
      NoHistory           = $NoBdnaHistory
      TrendMedianDeltaPct = $TrendMedianDeltaPct
      TrendWorstDeltaPct  = $TrendWorstDeltaPct
      TrendBestDeltaPct   = $TrendBestDeltaPct
      ComparedRows        = $ComparedRows
    }

    $bdnaSummary | ConvertTo-Json -Depth 6 | Out-File -Encoding UTF8 $BdnaSummaryJsonPath

    # Human-friendly markdown
    $trendMedian = if ($TrendMedianDeltaPct -ne $null) { ('{0:N2} %' -f $TrendMedianDeltaPct) }   else { '—' }
    $trendWorst = if ($TrendWorstDeltaPct -ne $null) { ('{0:N2} %' -f $TrendWorstDeltaPct) }  else { '—' }
    $trendBest = if ($TrendBestDeltaPct -ne $null) { ('{0:N2} %' -f $TrendBestDeltaPct) }   else { '—' }
    $tolStr = ('{0:N0} %' -f $TolerancePct)
    $compRuns = if ($ComparedRows) { $ComparedRows } else { 0 }
    $regressionDetectedEmoji = if (-not $HadRegressions) { '✅' } else { '❌' }

    $bdnaMd = @()
    $bdnaMd += "# 📈 BDNA Trend Summary (Candidate ↔ main)"
    $bdnaMd += ""
    $bdnaMd += "| Metric | Value | Interpretation |"
    $bdnaMd += "|--------|------:|----------------|"
    $bdnaMd += "| **Median Δ%** | $trendMedian | $(if ($TrendMedianDeltaPct -ne $null) { if ($TrendMedianDeltaPct -gt $TolerancePct) { 'Above tolerance ❌' } else { 'Within tolerance ✅' } } else { '—' }) |"
    $bdnaMd += "| **Worst Δ%** | $trendWorst | Highest slowdown across benchmarks |"
    $bdnaMd += "| **Best Δ%** | $trendBest | Largest speedup (negative is good) |"
    $bdnaMd += "| **Tolerance** | $tolStr | Gate threshold |"
    $bdnaMd += "| **Regression detected?** | $regressionDetectedEmoji | $(if ($HadRegressions) { 'Trend gate triggered' } else { 'No trend regressions' }) |"
    $bdnaMd += "| **Compared Rows** | $compRuns | Benchmarks included in trend calc |"

    $bdnaMd -join "`n" | Out-File -Encoding UTF8 $BdnaSummaryMdPath
    $bdnaMd -join "`n" | Write-Host
  }

  # Ensure output dir exists
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

  # Compute A/B regression flags using TolerancePct (positive Δ% = slower)
  $HasAbRegression = $false
  $WorstDeltaPct = $null
  $RegressingPairs = @()

  foreach ($row in $Pairs) {
    if ($row.BaselineUs -gt 0 -and $row.CandidateUs -gt 0) {
      $d = (($row.CandidateUs - $row.BaselineUs) / $row.BaselineUs) * 100.0
      if ($WorstDeltaPct -eq $null -or $d -gt $WorstDeltaPct) { $WorstDeltaPct = $d }
      if ($d -gt $TolerancePct) {
        $HasAbRegression = $true
        $RegressingPairs += [pscustomobject]@{
          PositionName = $row.PositionName; Depth = $row.Depth; Threads = $row.Threads; ParallelRoot = $row.ParallelRoot
          BaselineUs = [math]::Round($row.BaselineUs, 3)
          CandidateUs = [math]::Round($row.CandidateUs, 3)
          DeltaUs = [math]::Round($row.DeltaUs, 3)
          DeltaPct = [math]::Round($d, 2)
        }
      }
    }
  }

  # Persist machine-readable A/B summary (read by the GitHub Action)
  $abSummary = [pscustomobject]@{
    HasAbRegression = $HasAbRegression
    WorstDeltaPct   = if ($WorstDeltaPct -ne $null) { [math]::Round($WorstDeltaPct, 2) } else { $null }
    TolerancePct    = $TolerancePct
    Pairs           = $Pairs | Select-Object PositionName, Depth, Threads, ParallelRoot, BaselineUs, CandidateUs, DeltaUs, DeltaPct
    Regressions     = $RegressingPairs
  }
  $abSummary | ConvertTo-Json -Depth 6 | Out-File -Encoding UTF8 $AbSummaryJsonPath

  # Human-readable A/B table (header now matches columns)
  $md = New-Object System.Text.StringBuilder
  $null = $md.AppendLine("# Forklift Benchmark Summary (A/B: Baseline vs Candidate)")
  $null = $md.AppendLine()
  $null = $md.AppendLine("| Position | Baseline (μs) | Candidate (μs) | Δ μs | Δ % |")
  $null = $md.AppendLine("|---------:|--------------:|---------------:|-----:|----:|")

  if (-not $Pairs -or $Pairs.Count -eq 0) {
    $null = $md.AppendLine()
    $null = $md.AppendLine("> _No benchmark pairs were found (ensure both **Baseline** and **Candidate** rows exist)._")
  }
  else {
    foreach ($row in $Pairs | Sort-Object PositionName) {
      $posLabel = "$($row.PositionName) depth=$($row.Depth ?? '?') threads=$($row.Threads ?? '?') parallelRoot=$($row.ParallelRoot ?? '?')"
      $cols = @(
        "**$posLabel**",
        ('{0:N3}' -f $row.BaselineUs),
        ('{0:N3}' -f $row.CandidateUs),
        ('{0:N3}' -f $row.DeltaUs),
        ('{0:N2}%' -f $row.DeltaPct)
      )
      $null = $md.AppendLine('| ' + ($cols -join ' | ') + ' |')
    }
  }
  $md.ToString() | Out-File -Encoding UTF8 $AbSummaryMdPath


  Write-Host ""
  Write-Host "===== A/B Summary ====="
  if (-not $Pairs -or $Pairs.Count -eq 0) {
    Write-Host "> No A/B pairs were produced. Check JSON role parsing and parameter keys (see warnings above)."
  }
  else {
    $Pairs |
    Sort-Object PositionName |
    Select-Object `
    @{Name = 'Position'; Expression = { $_.PositionName } },
    @{Name = 'Base(μs)'; Expression = { '{0:N3}' -f $_.BaselineUs } },
    @{Name = 'Candidate(μs)'; Expression = { '{0:N3}' -f $_.CandidateUs } },
    @{Name = 'Δ μs'; Expression = { '{0:N3}' -f $_.DeltaUs } },
    @{Name = 'Δ%'; Expression = { '{0:N2}%' -f $_.DeltaPct } } |
    Format-Table -AutoSize | Out-String | Write-Host
  }

  Write-Host "(Saved A/B JSON -> $AbSummaryJsonPath)"
  Write-Host "(Saved A/B Markdown -> $AbSummaryMdPath)"

  if ($EnableBdna) {
    Write-Host "(Saved BDNA JSON -> $BdnaSummaryJsonPath)"
    Write-Host "(Saved BDNA Markdown -> $BdnaSummaryMdPath)"
  }

  if ($HasAbRegression) {
    Write-Host "Performance regressions detected in A/B comparison (exceeding $TolerancePct% tolerance)."
    exit 1
  }

  # If BDNA was enabled and flagged regressions, fail now (after writing artifacts)
  if ($EnableBdna -and $HadRegressions) {
    exit 2
  }

}
finally {
  # Always attempt to restore environment & clean transient assets
  try { Pop-Location } catch {}
  Restore-PowerPlan

  foreach ($wt in $script:createdWorktrees) { Remove-WorktreeSafe -Path $wt }
}
