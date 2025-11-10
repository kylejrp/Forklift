param(
  [switch]$PushMain = $false,
  [switch]$Schedule = $false,
  [switch]$BenchPR = $false,
  [switch]$CiPR = $false,
  [switch]$All = $false,
  $ArtifactsRoot = $null,
  $CacheServerPath = $null
)

& docker desktop start

if ($All) {
  $PushMain = $true
  $Schedule = $true
  $BenchPR = $true
  $CiPR = $true
}

if (-not $ArtifactsRoot) { $ArtifactsRoot = Join-Path $env:TEMP 'act-artifacts' }
if (-not $CacheServerPath) { $CacheServerPath = Join-Path $env:TEMP 'act-cache' }

# Make sure artifact root exists if specified
if ($ArtifactsRoot -ne $null) {
  if (-not (Test-Path -Path $ArtifactsRoot -PathType Container)) {
    Write-Host "Creating artifact root directory at '$ArtifactsRoot'"
    New-Item -ItemType Directory -Path $ArtifactsRoot -Force | Out-Null
  }
}

# Make sure cache server path exists if specified
if ($CacheServerPath -ne $null) {
  if (-not (Test-Path -Path $CacheServerPath -PathType Container)) {
    Write-Host "Creating cache server directory at '$CacheServerPath'"
    New-Item -ItemType Directory -Path $CacheServerPath -Force | Out-Null
  }
}

# Optional: skip PR comment step locally
$env:DRY_RUN = "true"

# If you want the script to be able to hit the API (e.g., real repos), supply a token:
$env:GITHUB_TOKEN = (gh auth token)

# Set UTF-8 + ANSI once per session (optional but recommended)
$enc = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $enc
$OutputEncoding = $enc
$env:LANG = 'C.UTF-8'
$env:LC_ALL = 'C.UTF-8'
$PSStyle.OutputRendering = 'Ansi'

# Detect already-loaded ProcLog.Pump in dynamic assemblies as well
$procLogLoaded = [AppDomain]::CurrentDomain.GetAssemblies() |
ForEach-Object { $_.GetType('ProcLog.Pump', $false, $true) } |
Where-Object { $_ } |
Select-Object -First 1

if (-not $procLogLoaded) {
  Add-Type -Language CSharp -TypeDefinition @"
#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ProcLog {
  public static class Pump {
    public static int Run(string exe, string[] args, string logPath, bool forceColor, out string? error)
    {
      error = null;
      var psi = new ProcessStartInfo {
        FileName = exe,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        StandardOutputEncoding = new UTF8Encoding(false),
        StandardErrorEncoding  = new UTF8Encoding(false),
      };
      foreach (var a in args) psi.ArgumentList.Add(a);
      if (forceColor) {
        psi.Environment["CLICOLOR_FORCE"] = "1";
        psi.Environment["FORCE_COLOR"]    = "1";
      }

      Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)) ?? ".");
      using (var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
      using (var sw = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true })
      using (var p  = new Process(){ StartInfo = psi, EnableRaisingEvents = true })
      {
        DataReceivedEventHandler onData = (sender, e) => {
          if (string.IsNullOrEmpty(e.Data)) return;
          try { sw.WriteLine(e.Data); } catch {}
          try { Console.WriteLine(e.Data); } catch {}
        };

        ConsoleCancelEventHandler? cancelHandler = null;
        try {
          if (!p.Start()) { error = "Failed to start process."; return -1; }
          p.OutputDataReceived += onData;
          p.ErrorDataReceived  += onData;
          p.BeginOutputReadLine();
          p.BeginErrorReadLine();

          cancelHandler = (s, e) => {
            e.Cancel = true; // keep pwsh alive
            try { if (!p.HasExited) p.Kill(true); } catch {}
          };
          Console.CancelKeyPress += cancelHandler;

          p.WaitForExit();
          p.WaitForExit(200); // let async readers flush
          return p.ExitCode;
        }
        catch (Exception ex) {
          error = ex.Message;
          try { if (!p.HasExited) p.Kill(true); } catch {}
          return -1;
        }
        finally {
          if (cancelHandler != null) Console.CancelKeyPress -= cancelHandler;
          try { p.CancelOutputRead(); } catch {}
          try { p.CancelErrorRead();  } catch {}
        }
      }
    }
  }
}
#nullable disable
"@
}

function Invoke-Logged {
  param(
    [Parameter(Mandatory)][string]$Exe,
    [string[]]$CmdArgs = @(),
    [Parameter(Mandatory)][string]$LogPath,
    [switch]$ForceColor,
    [switch]$PassThru
  )

  $exePath = (Get-Command $Exe -ErrorAction Stop).Source

  $dir = Split-Path -Parent $LogPath
  if ([string]::IsNullOrWhiteSpace($dir)) { $dir = '.' }
  $null = New-Item -ItemType Directory -Force -Path $dir

  $err = $null
  $code = [ProcLog.Pump]::Run($exePath, $CmdArgs, $LogPath, [bool]$ForceColor, [ref]$err)
  if ($err) { Write-Warning $err }
  $global:LASTEXITCODE = $code
  if ($code -ne 0) { Write-Warning "Process exited with code $code" }
  if ($PassThru) { return $code }
}

function Invoke-ActRun {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)] [ValidateSet('push', 'pull_request', 'schedule', 'workflow_dispatch')]
    [string]$Event,
    [Parameter(Mandatory)] [string]$WorkflowPath,
    [Parameter(Mandatory)] [string]$Job,
    [Parameter(Mandatory)] [string]$EventJson,
    [string]$Suite,        # 'minimal'|'fast'|'full'
    [switch]$DryRun,
    [string]$LogPath = 'act.log',
    [string]$ArtifactsRoot = $script:ArtifactsRoot,
    [string]$CacheServerPath = $script:CacheServerPath,
    [hashtable]$ExtraEnv,
    [hashtable]$Secrets
  )

  $cmdArgs = @('act', $Event, '-W', $WorkflowPath, '-j', $Job, '-e', $EventJson, '--reuse')

  if ($env:GITHUB_TOKEN) { $cmdArgs += @('-s', "GITHUB_TOKEN=$($env:GITHUB_TOKEN)") }
  if ($Secrets) { foreach ($k in $Secrets.Keys) { $cmdArgs += @('-s', "$k=$($Secrets[$k])") } }

  if ($DryRun) { $cmdArgs += @('--env', 'DRY_RUN=true') }
  if ($Suite) {
    $matrixJson = @{ suite = $Suite } | ConvertTo-Json -Compress
    $cmdArgs += @('--matrix', $matrixJson)
  }
  if ($ExtraEnv) { foreach ($k in $ExtraEnv.Keys) { $cmdArgs += @('--env', "$k=$($ExtraEnv[$k])") } }

  if ($ArtifactsRoot) { $cmdArgs += @('--artifact-server-path', $ArtifactsRoot) }
  if ($CacheServerPath) { $cmdArgs += @('--cache-server-path', $CacheServerPath) }

  Invoke-Logged -LogPath $LogPath -Exe 'gh' -CmdArgs $cmdArgs -ForceColor
}

function Run-BenchPR {
  Invoke-ActRun `
    -Event        'pull_request' `
    -WorkflowPath '.github/workflows/benchmark.yml' `
    -Job          'benchmark-suite' `
    -EventJson    '.github/workflows/events/pr.json' `
    -Suite        'minimal' `
    -DryRun `
    -LogPath      'act-bench-pr.log'
}

function Run-PushMain {
  Invoke-ActRun `
    -Event        'push' `
    -WorkflowPath '.github/workflows/benchmark.yml' `
    -Job          'benchmark-suite' `
    -EventJson    '.github/workflows/events/push-main.json' `
    -DryRun `
    -LogPath      'act-push-main.log'
}

function Run-Schedule {
  Invoke-ActRun `
    -Event        'schedule' `
    -WorkflowPath '.github/workflows/benchmark.yml' `
    -Job          'benchmark-suite' `
    -EventJson    '.github/workflows/events/schedule.json' `
    -DryRun `
    -LogPath      'act-schedule.log'
}

function Run-CiPR {
  Invoke-ActRun `
    -Event        'pull_request' `
    -WorkflowPath '.github/workflows/ci.yml' `
    -Job          'build-test' `
    -EventJson    '.github/workflows/events/pr.json' `
    -DryRun `
    -LogPath      'act-ci-pr.log'
}

if (-not ($PushMain -or $Schedule -or $BenchPR -or $CiPR)) {
  Write-Host "No action specified. Use one or more of: -PushMain -Schedule -BenchPR -CiPR"
  exit 1
}

if ($PushMain) { Run-PushMain }
if ($Schedule) { Run-Schedule }
if ($BenchPR) { Run-BenchPR }
if ($CiPR) { Run-CiPR }
