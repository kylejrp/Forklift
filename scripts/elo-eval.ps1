[CmdletBinding()]
param(
    [string]$EngineProject = 'Forklift.ConsoleClient/Forklift.ConsoleClient.csproj',
    [string]$EngineName = 'Forklift',
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
    [switch]$InstallTools
)

# Must be after the param block
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PSDefaultParameterValues['Out-File:Encoding']    = 'utf8'
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
    if ($env:ENGINE_PROJECT)       { $EngineProject  = $env:ENGINE_PROJECT }
    if ($env:ENGINE_NAME)          { $EngineName     = $env:ENGINE_NAME }
    if ($env:MATCH_DIR)            { $MatchDir       = $env:MATCH_DIR }
    if ($env:CURRENT_ENGINE_DIR)   { $CurrentOutDir  = $env:CURRENT_ENGINE_DIR }
    if ($env:PREVIOUS_ENGINE_DIR)  { $PreviousOutDir = $env:PREVIOUS_ENGINE_DIR }

    if ($VsRef) {
        $VsParent = $false
    } elseif (-not $PSBoundParameters.ContainsKey('VsParent')) {
        $VsParent = $true
    }

    # Use different names to avoid clobbering PowerShell's read-only $IsWindows etc.
    $OnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
    $OnLinux   = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)
    $OnMacOS   = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)

    function Get-CommandOrNull {
        param([string]$Name)
        try { return Get-Command $Name -ErrorAction Stop } catch { return $null }
    }

    function Invoke-Checked {
    param(
        [Parameter(Mandatory=$true)][string]$Command,
        [string[]]$Arguments,
        [switch]$IgnoreExitCode
    )
    Write-Host "[elo-eval] $Command $($Arguments -join ' ')"
    & $Command @Arguments
    $exit = $LASTEXITCODE

    if ($IgnoreExitCode) {
        # We chose to ignore this exit; prevent it from leaking and failing the script.
        $global:LASTEXITCODE = 0
        return
    }

    if ($exit -ne 0) {
        throw "Command '$Command' failed with exit code $exit"
    }

    # Successful native call; avoid letting any previous non-zero leak.
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
        if (-not $OnWindows -and $Path) { & chmod '+x' $Path }
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
            [string]$OutputDirectory,
            [string]$EngineNameParam
        )
        $expectedName = if ($OnWindows) { "$EngineNameParam.exe" } else { $EngineNameParam }
        $expectedPath = Join-Path $OutputDirectory $expectedName
        if (Test-Path $expectedPath) {
            Set-Executable -Path $expectedPath
            return $expectedPath
        }

        $published = Get-ChildItem -Path $OutputDirectory -File | Sort-Object LastWriteTime -Descending
        foreach ($file in $published) {
            try {
                Move-Item -LiteralPath $file.FullName -Destination $expectedPath -Force
                Set-Executable -Path $expectedPath
                return $expectedPath
            } catch {
                continue
            }
        }
        throw "Unable to normalize published binary in $OutputDirectory"
    }

    function Get-LogicalProcessorCount {
        param([int]$Requested)
        if ($Requested -gt 0) { return $Requested }
        try {
            if ($OnWindows) {
                $count = (Get-CimInstance Win32_Processor | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum
                if ($count -gt 0) { return [int]$count }
            } elseif ($OnLinux) {
                $nproc = Get-CommandOrNull 'nproc'
                if ($nproc) {
                    $value = (& $nproc.Source).Trim()
                    $parsed = 0
                    if ([int]::TryParse($value, [ref]$parsed) -and $parsed -gt 0) { return $parsed }
                }
                $cpuinfo = Get-Content -ErrorAction SilentlyContinue /proc/cpuinfo | Where-Object { $_ -like 'processor*' }
                $count = $cpuinfo.Count
                if ($count -gt 0) { return [int]$count }
            } elseif ($OnMacOS) {
                $sysctl = Get-CommandOrNull 'sysctl'
                if ($sysctl) {
                    $value = (& $sysctl.Source -n hw.logicalcpu).Trim()
                    $parsed = 0
                    if ([int]::TryParse($value, [ref]$parsed) -and $parsed -gt 0) { return $parsed }
                }
            }
        } catch { }
        return 1
    }

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

    $baselineRef = Get-BaselineRef
    if (-not $baselineRef) {
        Write-Host '[elo-eval] No suitable baseline; skipping Elo evaluation.'
        Write-Output 'Elo evaluation skipped: no suitable baseline reference.'
        exit 0
    }
    Write-Host "[elo-eval] Baseline ref: $baselineRef"

    $runtime = if ($OnWindows) { 'win-x64' } else { 'linux-x64' }

    if (Test-Path $CurrentOutDir) { Remove-Item $CurrentOutDir -Recurse -Force }
    Ensure-Directory -Path $CurrentOutDir
    $publishArgs = @('publish', $EngineProject, '-c', 'Release', '-r', $runtime, '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $CurrentOutDir)
    Invoke-Checked -Command 'dotnet' -Arguments $publishArgs
    $currentBinary = Normalize-EngineBinary -OutputDirectory $CurrentOutDir -EngineNameParam $EngineName

    if (Test-Path $PreviousOutDir) { Remove-Item $PreviousOutDir -Recurse -Force }
    Ensure-Directory -Path $PreviousOutDir
    $baselineBuildSuccess = $true
    try {
        Invoke-Checked -Command 'git' -Arguments @('checkout', $baselineRef)
        Invoke-Checked -Command 'dotnet' -Arguments @('publish', $EngineProject, '-c', 'Release', '-r', $runtime, '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $PreviousOutDir)
    } catch {
        $baselineBuildSuccess = $false
        Write-Host "[elo-eval] Baseline build failed: $($_.Exception.Message)"
    } finally {
        Invoke-Checked -Command 'git' -Arguments @('checkout', '-') -IgnoreExitCode
    }

    if (-not $baselineBuildSuccess) {
        Write-Host '[elo-eval] Baseline build failed; skipping Elo evaluation.'
        Write-Output "Elo evaluation skipped: baseline build failed for '$baselineRef'."
        exit 0
    }

    $baselineBinary = Normalize-EngineBinary -OutputDirectory $PreviousOutDir -EngineNameParam $EngineName

    if (-not $OnWindows) {
        if ($InstallTools.IsPresent) { Write-Host '[elo-eval] InstallTools switch has no effect on non-Windows platforms.' }
        Invoke-Checked -Command 'bash' -Arguments @('./scripts/install-chess-tools.sh')
    } else {
        $cutechessCmd = Get-CommandOrNull 'cutechess-cli'
        if (-not $cutechessCmd -and $InstallTools) {
            $toolsRoot     = Join-Path $repoRoot '.tools'
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
            } catch {
                Write-Warning "Failed to install cutechess-cli automatically: $($_.Exception.Message)"
            }
            $cutechessCmd = Get-CommandOrNull 'cutechess-cli'
        }
        if (-not $cutechessCmd) { throw 'cutechess-cli is required but was not found on PATH.' }
        if (-not (Get-CommandOrNull 'ordo')) { Write-Warning 'ordo not found; ratings will be skipped.' }
    }

    $cutechess = Get-CommandOrNull 'cutechess-cli'
    if (-not $cutechess) { throw 'cutechess-cli is required but unavailable.' }

    $ordoCmd = Get-CommandOrNull 'ordo'

    Ensure-Directory -Path $MatchDir
    Ensure-Directory -Path (Join-Path $MatchDir 'logs')

    $toolVersionsPath = Join-Path $MatchDir 'tool-versions.txt'
    $toolLines = @()
    try   { $toolLines += "cutechess-cli: $(& $cutechess.Source --version | Select-Object -First 1)" }
    catch { $toolLines += 'cutechess-cli: (version unavailable)' }
    if ($ordoCmd) {
        try   { $toolLines += "ordo: $(& $ordoCmd.Source -h 2>&1 | Select-Object -First 1)" }
        catch { $toolLines += 'ordo: (version unavailable)' }
    } else {
        $toolLines += 'ordo: not available'
    }
    $toolLines | Set-Content -Path $toolVersionsPath

    $openingsPath = if ([System.IO.Path]::IsPathRooted($OpeningsFile)) { $OpeningsFile } else { Join-Path $repoRoot $OpeningsFile }
    Ensure-Directory -Path (Split-Path -Parent $openingsPath)
    if (-not (Test-Path $openingsPath)) {
        $tmpOpenings = Join-Path ([System.IO.Path]::GetTempPath()) 'openings.epd'
        $downloaded = $false
        try {
            Download-File -Uri 'https://raw.githubusercontent.com/official-stockfish/books/master/MiniChess/MiniChess.epd' -Destination $tmpOpenings
            $lines = Get-Content -Path $tmpOpenings -ErrorAction Stop | Select-Object -First 200
            if ($lines.Count -gt 0) {
                $lines | Set-Content -Path $openingsPath
                $downloaded = $true
            }
        } catch {
            Write-Warning "Failed to download openings book: $($_.Exception.Message)"
        } finally {
            if (Test-Path $tmpOpenings) { Remove-Item $tmpOpenings -Force }
        }
        if (-not $downloaded) {
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
    }

    $currentBinary  = Resolve-Path $currentBinary
    $baselineBinary = Resolve-Path $baselineBinary
    if (-not $OnWindows) {
        Set-Executable -Path $currentBinary.Path
        Set-Executable -Path $baselineBinary.Path
    }

    $resolvedConcurrency = Get-LogicalProcessorCount -Requested $Concurrency
    $pgnPath = Join-Path $MatchDir 'latest-vs-previous.pgn'
    $logPath = Join-Path $MatchDir 'logs/cutechess-cli.log'

    $sprtArgs = @()
    if ($Sprt) { $sprtArgs = $Sprt -split '\s+' | Where-Object { $_ } }

    $newEngineCmd = 'cmd=' + (Quote-CutechessArgument $currentBinary.Path)
    $oldEngineCmd = 'cmd=' + (Quote-CutechessArgument $baselineBinary.Path)
    $openingsArg  = 'file=' + (Quote-CutechessArgument $openingsPath)

    $cutechessArgs = @(
        '-engine', $newEngineCmd, 'name=New', 'proto=uci',
        '-engine', $oldEngineCmd, 'name=Old', 'proto=uci',
        '-each', "tc=$TimeControl", 'timemargin=100',
        '-games', $Games,
        '-repeat',
        '-concurrency', $resolvedConcurrency,
        '-openings', $openingsArg, 'format=epd', 'order=random', 'plies=8',
        '-draw', 'movenumber=50', 'movecount=8', 'score=10',
        '-resign', 'movecount=8', 'score=800', 'twosided=true'
    )
    if ($sprtArgs.Count -gt 0) { $cutechessArgs += '-sprt'; $cutechessArgs += $sprtArgs }
    $cutechessArgs += @('-pgnout', (Quote-CutechessArgument $pgnPath))

    $cutechessOutput = & $cutechess.Source @cutechessArgs 2>&1 | Tee-Object -FilePath $logPath
    $cutechessExit = $LASTEXITCODE
    if ($cutechessExit -ne 0) { throw "cutechess-cli exited with code $cutechessExit" }

    $gameCount = 0
    if (Test-Path $pgnPath) {
        $gameCount = (Select-String -Pattern '^\[Result "' -Path $pgnPath -ErrorAction SilentlyContinue | Measure-Object).Count
    }

    $ratingsTxt = Join-Path $MatchDir 'ratings.txt'
    $ratingsCsv = Join-Path $MatchDir 'ratings.csv'
    $ordoRan = $false
    if ($gameCount -gt 0 -and $ordoCmd) {
        $ordoArgs = @('-A', '"Old"=' + $AnchorOld, '-p', $pgnPath, '-o', $ratingsTxt, '-c', $ratingsCsv)
        & $ordoCmd.Source @ordoArgs
        if ($LASTEXITCODE -eq 0) { $ordoRan = $true } else { Write-Warning 'Ordo failed to compute ratings.' }
    }

    $summaryLines = New-Object System.Collections.Generic.List[string]
    $summaryLines.Add("Baseline ref: $baselineRef")
    if ($gameCount -gt 0) {
        $scoreLine = $cutechessOutput | Where-Object { $_ -match 'Score of New vs Old:' } | Select-Object -Last 1
        if ($scoreLine -and $scoreLine -match 'Score of New vs Old:\s+([0-9\.]+)\s*-\s*([0-9\.]+)\s*-\s*([0-9\.]+)') {
            $summaryLines.Add("Score (New vs Old): $($Matches[1])/$($Matches[2])/$($Matches[3])")
        }
        $sprtLine = $cutechessOutput | Where-Object { $_ -match 'SPRT' } | Select-Object -Last 1
        if ($sprtLine) { $summaryLines.Add($sprtLine.Trim()) }
        $summaryLines.Add("Games played: $gameCount")
    } else {
        $summaryLines.Add('No completed games recorded in PGN.')
    }
    if ($ordoRan -and (Test-Path $ratingsCsv)) {
        try {
            $csvContent = Import-Csv -Path $ratingsCsv
            $newRow = $csvContent | Where-Object { $_.Player -eq 'New' }
            $oldRow = $csvContent | Where-Object { $_.Player -eq 'Old' }
            if ($newRow) {
                $culture   = [System.Globalization.CultureInfo]::InvariantCulture
                $diff      = [double]::Parse($newRow.Difference, $culture)
                $err       = if ($newRow.Error) { [double]::Parse($newRow.Error, $culture) } else { $null }
                $newRating = [double]::Parse($newRow.Rating, $culture)
                $oldRating = if ($oldRow) { [double]::Parse($oldRow.Rating, $culture) } else { [double]$AnchorOld }
                $line = "Ordo: New {0:F2} vs Old {1:F2} (Δ {2:F2})" -f $newRating, $oldRating, $diff
                if ($err -ne $null) { $line += " ±{0:F2}" -f $err }
                $summaryLines.Add($line)
            }
        } catch {
            Write-Warning "Failed to parse Ordo output: $($_.Exception.Message)"
        }
    }
    if ($gameCount -lt 10) {
        Write-Warning 'Fewer than 10 games were played; statistics may be unreliable.'
    }

    foreach ($line in $summaryLines) { Write-Output $line }
}
finally {
    Pop-Location
}
