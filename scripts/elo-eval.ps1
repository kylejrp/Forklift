[CmdletBinding()]
param(
    [string]$EngineProject = 'Forklift.ConsoleClient/Forklift.ConsoleClient.csproj',
    [string]$EngineName = 'Forklift.ConsoleClient',
    [switch]$VsParent,
    [string]$VsRef,
    [int]$Games = 300,
    [string]$TimeControl = '1+0.1',
    [string]$Sprt = 'elo0=0 elo1=5 alpha=0.05 beta=0.05',
    [string]$OpeningsFile = 'matches/openings.epd',
    [int]$Concurrency = 0,
    [string]$MatchDir = 'matches',
    [string]$CurrentOutDir = 'artifacts/current/engine',
    [string]$PreviousOutDir = 'artifacts/previous/engine',
    [int]$AnchorOld = 2500,
    [switch]$InstallTools,
    [switch]$DebugCutechessOutput
)

# Must be after the param block
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$PSDefaultParameterValues['Set-Content:Encoding'] = 'utf8'
$PSDefaultParameterValues['Add-Content:Encoding'] = 'utf8'

function Resolve-RepoRoot {
    param([string]$ScriptPath)
    if ([string]::IsNullOrEmpty($ScriptPath)) {
        throw 'Script path is required to resolve repository root.'
    }
    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..')).ProviderPath
}

$repoRoot = Resolve-RepoRoot -ScriptPath $PSCommandPath
Push-Location $repoRoot
try {
    if ($env:ENGINE_PROJECT) { $EngineProject = $env:ENGINE_PROJECT }
    if ($env:ENGINE_NAME) { $EngineName = $env:ENGINE_NAME }
    if ($env:MATCH_DIR) { $MatchDir = $env:MATCH_DIR }
    if ($env:CURRENT_ENGINE_DIR) { $CurrentOutDir = $env:CURRENT_ENGINE_DIR }
    if ($env:PREVIOUS_ENGINE_DIR) { $PreviousOutDir = $env:PREVIOUS_ENGINE_DIR }

    if ($VsRef) {
        $VsParent = $false
    }
    elseif (-not $PSBoundParameters.ContainsKey('VsParent')) {
        $VsParent = $true
    }

    function Get-CommandOrNull {
        param([string]$Name)
        try { return Get-Command $Name -ErrorAction Stop } catch { return $null }
    }

    function Invoke-Checked {
        param(
            [Parameter(Mandatory = $true)][string]$Command,
            [string[]]$Arguments,
            [switch]$IgnoreExitCode,
            [string]$WorkingDirectory
        )
        $prefix = if ($WorkingDirectory) { "[cwd=$WorkingDirectory] " } else { "" }
        Write-Host "[elo-eval] ${prefix}$Command $($Arguments -join ' ')"
        if ($WorkingDirectory) {
            Push-Location $WorkingDirectory
            try {
                & $Command @Arguments
                $exit = $LASTEXITCODE
            }
            finally {
                Pop-Location
            }
        }
        else {
            & $Command @Arguments
            $exit = $LASTEXITCODE
        }

        if ($IgnoreExitCode) {
            $global:LASTEXITCODE = 0
            return
        }
        if ($exit -ne 0) {
            throw "Command '$Command' failed with exit code $exit"
        }
        $global:LASTEXITCODE = 0
    }

    function Ensure-Directory {
        param([string]$Path)
        if (-not [string]::IsNullOrWhiteSpace($Path)) {
            $null = New-Item -ItemType Directory -Force -Path $Path
        }
    }

    function Set-Executable {
        param([string]$Path)
        if (-not $IsWindows -and $Path) { & chmod '+x' $Path }
    }

    function Quote-CutechessArgument {
        param([string]$Value)
        if (-not $Value) { return '""' }
        $escaped = $Value -replace '"', '\"'
        if ($Value -match '\s') { return '"' + $escaped + '"' }
        return $escaped
    }

    function Normalize-EngineBinary {
        param(
            [Parameter(Mandatory)] [string]$OutputDirectory,
            [Parameter(Mandatory)] [string]$EngineName
        )

        if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
            throw "Publish output directory does not exist: $OutputDirectory"
        }

        $dll = Join-Path $OutputDirectory ($EngineName + '.dll')
        $rt = Join-Path $OutputDirectory ($EngineName + '.runtimeconfig.json')

        if ((Test-Path -LiteralPath $dll -PathType Leaf) -and (Test-Path -LiteralPath $rt  -PathType Leaf)) {
            return (Resolve-Path $dll).Path
        }

        $listing = (Get-ChildItem -LiteralPath $OutputDirectory | Select-Object Name, Length, LastWriteTime | Out-String)
        throw "Expected entry DLL '$($EngineName).dll' and runtimeconfig '$($EngineName).runtimeconfig.json' in $OutputDirectory, but one or both were missing.`nContents:`n$listing"
    }


    function Get-LogicalProcessorCount {
        param([int]$Requested)
        if ($Requested -gt 0) { return $Requested }
        try {
            if ($IsLinux) {
                # Prefer cgroup quota (containers)
                $quotaPath = '/sys/fs/cgroup/cpu.max'
                if (Test-Path $quotaPath) {
                    $cpuMax = (Get-Content $quotaPath).Trim()  # e.g. "200000 100000" or "max 100000"
                    $parts = $cpuMax -split '\s+'
                    if ($parts.Length -ge 2 -and $parts[0] -ne 'max') {
                        $quota = [double]$parts[0]
                        $period = [double]$parts[1]
                        $cg = [int][math]::Floor($quota / $period + 0.0001)
                        if ($cg -gt 0) { return $cg }
                    }
                }
                $nproc = Get-Command nproc -ErrorAction SilentlyContinue
                if ($nproc) {
                    $v = (& $nproc.Source).Trim()
                    $p = 0; if ([int]::TryParse($v, [ref]$p) -and $p -gt 0) { return $p }
                }
                $cpuinfo = Get-Content -ErrorAction SilentlyContinue /proc/cpuinfo | Where-Object { $_ -like 'processor*' }
                if ($cpuinfo.Count -gt 0) { return [int]$cpuinfo.Count }
            }
            elseif ($IsWindows) {
                $sum = (Get-CimInstance Win32_Processor | Measure-Object NumberOfLogicalProcessors -Sum).Sum
                if ($sum -gt 0) { return [int]$sum }
            }
            elseif ($IsMacOS) {
                $sysctl = Get-Command sysctl -ErrorAction SilentlyContinue
                if ($sysctl) {
                    $v = (& $sysctl.Source -n hw.logicalcpu).Trim()
                    $p = 0; if ([int]::TryParse($v, [ref]$p) -and $p -gt 0) { return $p }
                }
            }
        }
        catch { }
        return 1
    }

    $resolvedConcurrency = Get-LogicalProcessorCount -Requested $Concurrency
    $resolvedConcurrency = [Math]::Max(1, [Math]::Min($resolvedConcurrency, 8))

    function Download-File {
        param([string]$Uri, [string]$Destination)
        Write-Host "[elo-eval] Downloading $Uri"
        $curlCmd = Get-CommandOrNull 'curl'
        if ($curlCmd) { & $curlCmd.Source -L $Uri -o $Destination; return }
        $wgetCmd = Get-CommandOrNull 'wget'
        if ($wgetCmd) { & $wgetCmd.Source -O $Destination $Uri; return }
        Invoke-WebRequest -Uri $Uri -OutFile $Destination
    }

    function Get-BaselineRef {
        if ($VsRef) { return $VsRef }
        if (-not $VsParent) { return $null }
        $headCommit = (git rev-parse HEAD).Trim()
        $originMain = $null
        try { $originMain = (git rev-parse origin/main).Trim() } catch { $originMain = $null }
        if ($originMain) {
            if ($headCommit -eq $originMain) {
                try { return (git rev-parse HEAD^).Trim() } catch { return $null }
            }
            return $originMain
        }
        try { return (git rev-parse HEAD^).Trim() } catch { return $null }
    }

    function New-TemporaryDirectory {
        $base = Join-Path ([System.IO.Path]::GetTempPath()) ('eloeval_' + [System.Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $base | Out-Null
        return $base
    }

    function Add-GitWorktree {
        param(
            [Parameter(Mandatory = $true)][string]$Path,
            [Parameter(Mandatory = $true)][string]$Ref
        )
        # Detached to avoid branch locking; safer for ephemeral builds
        Invoke-Checked -Command 'git' -Arguments @('worktree', 'add', '--detach', $Path, $Ref)
    }

    function Remove-GitWorktree {
        param([Parameter(Mandatory = $true)][string]$Path)
        # Always force-remove the worktree; then prune to clean up metadata
        Invoke-Checked -Command 'git' -Arguments @('worktree', 'remove', '--force', $Path) -IgnoreExitCode
        Invoke-Checked -Command 'git' -Arguments @('worktree', 'prune') -IgnoreExitCode
        if (Test-Path $Path) {
            # If git left remnants (Windows file locks, etc.), try again.
            try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop } catch { }
        }
    }

    $baselineRef = Get-BaselineRef
    if (-not $baselineRef) {
        Write-Host '[elo-eval] No suitable baseline; skipping Elo evaluation.'
        Write-Output 'Elo evaluation skipped: no suitable baseline reference.'
        exit 0
    }
    Write-Host "[elo-eval] Baseline ref: $baselineRef"

    # Build current from the main working tree
    if (Test-Path $CurrentOutDir) { Remove-Item $CurrentOutDir -Recurse -Force }
    Ensure-Directory -Path $CurrentOutDir
    $publishArgs = @('publish', $EngineProject, '-c', 'Release',
        '--self-contained', 'false',
        '-p:PublishSingleFile=false',
        '-p:UseAppHost=false',
        '-o', $CurrentOutDir)
    Invoke-Checked -Command 'dotnet' -Arguments $publishArgs
    Write-Host "[elo-eval] Contents of ${CurrentOutDir}:"
    Get-ChildItem -LiteralPath $CurrentOutDir | ForEach-Object { Write-Host " - $($_.Name)" }
    $currentBinary = Normalize-EngineBinary -OutputDirectory $CurrentOutDir -EngineName $EngineName
    $currentRef = (git rev-parse HEAD).Trim()


    # Build baseline from a temporary worktree
    if (Test-Path $PreviousOutDir) { Remove-Item $PreviousOutDir -Recurse -Force }
    Ensure-Directory -Path $PreviousOutDir

    $baselineBuildSuccess = $true
    $baselineWorktree = $null
    try {
        $baselineWorktree = New-TemporaryDirectory
        Write-Host "[elo-eval] Creating baseline worktree at: $baselineWorktree"
        Add-GitWorktree -Path $baselineWorktree -Ref $baselineRef

        # Publish from inside the worktree so relative paths (EngineProject) resolve the same
        Invoke-Checked -Command 'dotnet' -Arguments @('restore', $EngineProject) -WorkingDirectory $baselineWorktree
        Invoke-Checked -Command 'dotnet' -Arguments @('publish', $EngineProject, '-c', 'Release', '--self-contained', 'false', '-p:PublishSingleFile=false', '-p:UseAppHost=false', '-o', (Resolve-Path $PreviousOutDir)) -WorkingDirectory $baselineWorktree
        Write-Host "[elo-eval] Contents of ${PreviousOutDir}:"
        Get-ChildItem -LiteralPath $PreviousOutDir | ForEach-Object { Write-Host " - $($_.Name)" }
    }
    catch {
        $baselineBuildSuccess = $false
        Write-Host "[elo-eval] Baseline build failed: $($_.Exception.Message)"
    }
    finally {
        if ($baselineWorktree) {
            Write-Host "[elo-eval] Cleaning up baseline worktree…"
            Remove-GitWorktree -Path $baselineWorktree
        }
    }

    if (-not $baselineBuildSuccess) {
        Write-Host '[elo-eval] Baseline build failed; skipping Elo evaluation.'
        Write-Output "Elo evaluation skipped: baseline build failed for '$baselineRef'."
        exit 0
    }

    $baselineBinary = Normalize-EngineBinary -OutputDirectory $PreviousOutDir -EngineName $EngineName

    if (-not $IsWindows) {
        if ($InstallTools.IsPresent) { Write-Host '[elo-eval] Note: Tools are always installed automatically on non-Windows platforms; the InstallTools switch has no effect.' }
        Invoke-Checked -Command 'bash' -Arguments @('./scripts/install-chess-tools.sh')
    }
    else {
        $cutechess = Get-CommandOrNull 'cutechess-cli'
        if (-not $cutechess -and $InstallTools) {
            $toolsRoot = Join-Path $repoRoot '.tools'
            $cutechessRoot = Join-Path $toolsRoot 'cutechess'
            Ensure-Directory -Path $cutechessRoot
            try {
                $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/cutechess/cutechess/releases/latest' -Headers @{ 'User-Agent' = 'Forklift-Elo-Eval' }
                $asset = $release.assets | Where-Object { $_.name -match 'windows' -and $_.name -match '\.zip$' } | Select-Object -First 1
                if ($asset) {
                    $zipPath = Join-Path $cutechessRoot $asset.name
                    Download-File -Uri $asset.browser_download_url -Destination $zipPath
                    Expand-Archive -Path $zipPath -DestinationPath $cutechessRoot -Force
                    $binary = Get-ChildItem -Path $cutechessRoot -Filter 'cutechess-cli.exe' -Recurse | Select-Object -First 1
                    if ($binary) { $env:PATH = "$($binary.DirectoryName);$env:PATH" }
                }
            }
            catch {
                Write-Warning "Failed to install cutechess-cli automatically: $($_.Exception.Message)"
            }
            $cutechess = Get-CommandOrNull 'cutechess-cli'
        }
        if (-not $cutechess) { throw 'cutechess-cli is required but was not found on PATH.' }
        if (-not (Get-CommandOrNull 'ordo')) { Write-Warning 'ordo not found; ratings will be skipped.' }
    }

    $cutechess = Get-CommandOrNull 'cutechess-cli'
    if (-not $cutechess) { throw 'cutechess-cli is required but unavailable.' }

    $ordoCmd = Get-CommandOrNull 'ordo'

    Ensure-Directory -Path $MatchDir
    Ensure-Directory -Path (Join-Path $MatchDir 'logs')

    $toolVersionsPath = Join-Path $MatchDir 'tool-versions.txt'
    $toolLines = @()
    try { $toolLines += "cutechess-cli: $(& $cutechess.Source --version | Select-Object -First 1)" }
    catch { $toolLines += 'cutechess-cli: (version unavailable)' }

    if ($ordoCmd) {
        try {
            $ov = & $ordoCmd.Source -v | Select-Object -First 1   # e.g. "ordo 1.2.6"
            $ver = ($ov -split '\s+')[1]
            $toolLines += "ordo: $ver"
        }
        catch { $toolLines += 'ordo: (version unavailable)' }
    }
    else {
        $toolLines += 'ordo: not available'
    }
    $toolLines | Set-Content -Path $toolVersionsPath


    $openingsPath = if ([System.IO.Path]::IsPathRooted($OpeningsFile)) { $OpeningsFile } else { Join-Path $repoRoot $OpeningsFile }
    Ensure-Directory -Path (Split-Path -Parent $openingsPath)
    if (-not (Test-Path $openingsPath)) {

        $fallback = @(
            'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1',
            'rnbqkbnr/pppp1ppp/4p3/8/3P4/5N2/PPP1PPPP/RNBQKB1R b KQkq - 1 2',
            'rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1',
            'rnbqkbnr/pp1ppppp/8/2p5/3P4/8/PPP1PPPP/RNBQKBNR w KQkq - 0 2',
            'rnbqkbnr/pppp1ppp/5n2/4p3/3PP3/5N2/PPP2PPP/RNBQKB1R w KQkq - 0 3',
            'r1bqkbnr/pppppppp/2n5/8/3P4/5N2/PPP1PPPP/RNBQKB1R w KQkq - 2 2',
            'rnbqkb1r/pppppppp/5n2/8/3P4/5N2/PPP1PPPP/RNBQKB1R w KQkq - 2 2',
            'r1bqkbnr/pppp1ppp/2n5/4p3/3P4/5N2/PPP1PPPP/RNBQKB1R w KQkq - 2 2',
            'rnbqkbnr/pppppppp/8/8/3P4/8/PPP1PPPP/RNBQKBNR b KQkq - 0 1',
            'rnbqkbnr/pp1ppppp/8/2p5/3P4/8/PPP1PPPP/RNBQKBNR b KQkq - 0 2',
            'rnbqkbnr/pppppppp/8/8/4P3/5N2/PPPP1PPP/RNBQKB1R b KQkq - 1 1',
            'r1bqkbnr/pppp1ppp/2n5/4p3/3P4/5N2/PPP1PPPP/RNBQKB1R b KQkq - 1 2',
            'rnbqkbnr/pppppppp/8/8/3P4/3B4/PPP1PPPP/RNBQK1NR b KQkq - 2 2',
            'rnbqkbnr/ppp1pppp/8/3p4/3P4/8/PPP1PPPP/RNBQKBNR w KQkq - 0 2',
            'rnbqkbnr/pppppppp/8/8/3P4/4P3/PPP2PPP/RNBQKBNR b KQkq - 0 1',
            'r1bqkbnr/pppppppp/2n5/8/3P4/5N2/PPP1PPPP/RNBQKB1R b KQkq - 1 2'
        )
        $fallback | Set-Content -Path $openingsPath
    }

    if (-not $IsWindows) {
        Set-Executable -Path $currentBinary
        Set-Executable -Path $baselineBinary
    }

    $resolvedConcurrency = Get-LogicalProcessorCount -Requested $Concurrency
    $pgnPath = Join-Path $MatchDir 'latest-vs-previous.pgn'
    if (Test-Path $pgnPath) { Remove-Item $pgnPath -Force -ErrorAction SilentlyContinue }
    $logPath = Join-Path $MatchDir 'logs/cutechess-cli.log'
    if (Test-Path $logPath) { Remove-Item $logPath -Force -ErrorAction SilentlyContinue }

    $dotnetCmd = (Get-Command dotnet).Source

    $engineNew = @(
        '-engine',
        "cmd=$dotnetCmd",
        "arg=$currentBinary",
        'name=New',
        'proto=uci'
    )
    $engineOld = @(
        '-engine',
        "cmd=$dotnetCmd",
        "arg=$baselineBinary",
        'name=Old',
        'proto=uci'
    )

    $openingsArg = "file=$openingsPath"

    $cutechessArgs = @()
    $cutechessArgs += $engineNew
    $cutechessArgs += $engineOld
    $cutechessArgs += @(
        '-each', "tc=$TimeControl", 'timemargin=100',
        '-games', $Games,
        '-repeat',
        '-concurrency', $resolvedConcurrency,
        '-openings', $openingsArg, 'format=epd', 'order=random', 'plies=8',
        '-draw', 'movenumber=50', 'movecount=8', 'score=10',
        '-resign', 'movecount=8', 'score=800', 'twosided=true'
    )
    if ($Sprt) {
        $cutechessArgs += '-sprt'
        $cutechessArgs += ($Sprt -split '\s+' | Where-Object { $_ })
    }
    $cutechessArgs += @('-pgnout', $pgnPath)
    if ($DebugCutechessOutput){
        $cutechessArgs += @('-debug', 'all')
    }

    $cutechessCommand = "$($cutechess.Source) " + ($cutechessArgs -join ' ')
    Write-Host "[elo-eval] Running cutechess-cli command: $cutechessCommand"

    & $cutechess.Source @cutechessArgs 2>&1 | Tee-Object -Variable cutechessOutput | Tee-Object -FilePath $logPath
    $cutechessExit = $LASTEXITCODE
    if ($cutechessExit -ne 0) {
        Write-Host "[elo-eval] cutechess-cli output:"
        throw "cutechess-cli exited with code $cutechessExit"
    }

    $gameCount = 0
    if (Test-Path $pgnPath) {
        $gameCount = (Select-String -Pattern '^\[Result "' -Path $pgnPath -ErrorAction SilentlyContinue | Measure-Object).Count
    }

    $ordoCmd = Get-CommandOrNull 'ordo'
    $ratingsTxt = Join-Path $MatchDir 'ratings.txt'
    $ratingsCsv = Join-Path $MatchDir 'ratings.csv'
    $ordoRan = $false
    if ($gameCount -gt 0 -and $ordoCmd) {
        $ordoArgs = @('-a', $AnchorOld, '-A', 'Old', '-p', $pgnPath, '-o', $ratingsTxt, '-c', $ratingsCsv)
        Write-Host "[elo-eval] Running ordo command: $($ordoCmd.Source) $($ordoArgs -join ' ')"
        $ordoOutput = & $ordoCmd.Source @ordoArgs
        if ($LASTEXITCODE -eq 0) {
            $ordoRan = $true
        }
        else {
            Write-Output "[elo-eval] Ordo command failed with exit code $LASTEXITCODE."
            Write-Output "[elo-eval] Ordo output: $ordoOutput"
            Write-Warning 'Ordo failed to compute ratings.'
        }
    }

    $gameCount = 0
    $sprtLine = $null

    if (Test-Path $pgnPath) {
        $gameCount = (Select-String -Pattern '^\[Result "' -Path $pgnPath -ErrorAction SilentlyContinue | Measure-Object).Count
    }

    # Snapshot a default summary so gates/PRs never crash
    $summaryPath = Join-Path $MatchDir 'summary.json'
    $baseSummary = [pscustomobject]@{
        baseline          = $baselineRef
        candidate         = $currentRef
        games             = 0
        sprt              = $null
        cutechess_command = $cutechessCommand
        ordo              = $null
    }
    $baseSummary | ConvertTo-Json -Depth 5 | Set-Content $summaryPath

    $summaryLines = New-Object System.Collections.Generic.List[string]
    $summaryLines.Add("### ♟️ Elo evaluation")
    $summaryLines.Add("")
    $summaryLines.Add("| Name | Value |")
    $summaryLines.Add("|---|---|")
    $summaryLines.Add("| Baseline ref | $baselineRef |")
    $summaryLines.Add("| Candidate ref | $currentRef |")

    if ($gameCount -gt 0) {
        $scoreLine = $cutechessOutput | Where-Object { $_ -match 'Score of New vs Old:' } | Select-Object -Last 1
        if ($scoreLine -and $scoreLine -match 'Score of New vs Old:\s+([0-9\.]+)\s*-\s*([0-9\.]+)\s*-\s*([0-9\.]+)') {
            $summaryLines.Add("| Score (New vs Old) | $($Matches[1])/$($Matches[2])/$($Matches[3]) |")
        }

        # Here, sprtLine looks like "SPRT: llr -0.126 (-4.3%), lbound -2.94, ubound 2.94"
        # We want to convert it to "| SPRT | llr -0.126 (-4.3%), lbound -2.94, ubound 2.94 |"
        $sprtLine = ($cutechessOutput | Select-String -Pattern '\bSPRT\b' | Select-Object -Last 1).Line
        $sprtMdLine = if ($sprtLine) { "| SPRT | " + $sprtLine.Trim().Substring(5) + " |" } else { $null }
        if ($sprtLine) {
            $summaryLines.Add($sprtMdLine)
        }

        $summaryLines.Add("| Games played | $gameCount |")
    }
    else {
        $summaryLines.Add('| Games played | N/A |')
    }

    $existing = Get-Content $summaryPath | ConvertFrom-Json
    $existing.games = $gameCount
    $existing.cutechess_command = $cutechessCommand
    $existing.sprt = if ($sprtLine) { $sprtLine.Trim() } else { $null }
    $existing | ConvertTo-Json -Depth 5 | Set-Content $summaryPath

    if ($ordoRan -and (Test-Path $ratingsCsv)) {
        try {
            $file = (Get-Content -Path $ratingsCsv) -replace '^"#"', '"NUMBER"'
            $csv = ConvertFrom-Csv $file

            # Normalize to friendly names and parse numbers robustly
            $norm = $csv | ForEach-Object {
                $rating = $null
                $ordoError = $null
                $style = [System.Globalization.NumberStyles]::Float
                $cult = [System.Globalization.CultureInfo]::InvariantCulture

                $parsedRating = 0.0
                if ($_.'RATING' -and [double]::TryParse($_.'RATING', $style, $cult, [ref]$parsedRating)) {
                    $rating = $parsedRating
                }
                $parsedError = 0.0
                if ($_.'ERROR' -and $_.'ERROR' -ne '-' -and [double]::TryParse($_.'ERROR', $style, $cult, [ref]$parsedError)) {
                    $ordoError = $parsedError
                }

                [pscustomobject]@{
                    Player = $_.'PLAYER'
                    Rating = $rating
                    Error  = $ordoError      # may be $null for the anchor
                }
            }

            $newRow = $norm | Where-Object { $_.Player -eq 'New' } | Select-Object -First 1
            $oldRow = $norm | Where-Object { $_.Player -eq 'Old' } | Select-Object -First 1

            if ($newRow -and $oldRow) {
                $diff = ($newRow.Rating - $oldRow.Rating)
                $line = "| Ordo | New {0:F2} vs Old {1:F2} (Δ {2:F2}) |" -f $newRow.Rating, $oldRow.Rating, $diff
                if ($newRow.Error -ne $null) { $line += " ±{0:F2}" -f $newRow.Error }
                $summaryLines.Add($line)
            }
            else {
                Write-Warning 'Ordo output did not contain expected player entries.'
            }

            $nr = if ($null -ne $newRow) { $newRow.Rating } else { $null }
            $ne = if ($null -ne $newRow) { $newRow.Error } else { $null }
            $or = if ($null -ne $oldRow) { $oldRow.Rating } else { $null }

            $existing = Get-Content $summaryPath | ConvertFrom-Json
            $existing.ordo = @{
                new   = $nr
                old   = $or
                delta = $diff
                err   = $ne
            }
            $existing | ConvertTo-Json -Depth 5 | Set-Content $summaryPath
        }
        catch {
            $summaryLines.Add('| Ordo | Failed to parse ratings output. |')
            Write-Warning "Failed to parse Ordo output: $($_.Exception.Message)"
        }
    }
    if ($gameCount -lt 10) {
        $summaryLines.Add("")
        $summaryLines.Add("Fewer than 10 games were played; statistics may be unreliable.")
        Write-Warning 'Fewer than 10 games were played; statistics may be unreliable.'
    }

    foreach ($line in $summaryLines) { Write-Output $line }
    foreach ($line in $summaryLines) { Add-Content -Path (Join-Path $MatchDir 'summary.md') -Value $line }
}
finally {
    Pop-Location
}
