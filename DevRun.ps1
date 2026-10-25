# DevRun.ps1 - the inner half of dev.cmd: build from source, restart, stay attached.
#
#   1. merge + compile src\*.cs (skipped when the content hash matches the cache)
#   2. ask any running instance to quit, wait for it to release the mutex
#   3. launch the freshly compiled DLL *in this process*, so exceptions print here
#
# Step 3 is why this exists instead of just calling MouseMeter.ps1: a separate launcher
# would detach and you would never see the stack trace when a paint handler throws.

param(
    [switch]$Settings,
    [switch]$Force,
    [switch]$Verbose
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

. (Join-Path $root 'Compiler.ps1')

function Info($text) { Write-Host $text -ForegroundColor Gray }
function Ok($text) { Write-Host $text -ForegroundColor Green }
function Warn($text) { Write-Host $text -ForegroundColor Yellow }

# ---------- 1. build ----------

$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $build = Build-MouseMeter -Root $root -Force:$Force
} catch {
    Write-Host ''
    Write-Host '  BUILD FAILED - the running copy was left untouched.' -ForegroundColor Red
    Write-Host ''
    # The CodeDOM message is "BUILD FAILED`nline N: text"; indent the detail lines so they
    # stand out from the surrounding chatter.
    foreach ($line in ($_.Exception.Message -split "`r?`n")) {
        if ($line.Trim().Length -eq 0) { continue }
        Write-Host ('    ' + $line) -ForegroundColor Red
    }
    Write-Host ''
    exit 1
}
$sw.Stop()

if ($build.Compiled) {
    Ok ('  compiled in ' + $sw.ElapsedMilliseconds + ' ms  (' + $build.Chars + ' chars)')
} else {
    Info ('  up to date  (cache hit, ' + $sw.ElapsedMilliseconds + ' ms)')
}

# ---------- 2. stop the previous instance ----------

$mutexName = 'MouseMeter.SingleInstance.v2'
$quitEvent = 'Local\MouseMeter.QuitRequest'

function Test-MutexFree() {
    # -ArgumentList is required: without it PowerShell parses the parens as part of the
    # type name and New-Object throws "Cannot find type".
    $m = $null
    try {
        $m = New-Object System.Threading.Mutex -ArgumentList $false, $mutexName
        $got = $m.WaitOne(0)
        if ($got) { $m.ReleaseMutex() }
        return $got
    } catch [System.Threading.AbandonedMutexException] {
        return $true   # previous owner died without releasing: treat as free
    } catch {
        return $true   # cannot probe -> assume free, let the --restart path sort it out
    } finally {
        if ($m -ne $null) { $m.Dispose() }
    }
}

if (-not (Test-MutexFree)) {
    $signalled = $false
    try {
        $ev = [System.Threading.EventWaitHandle]::OpenExisting($quitEvent)
        $ev.Set()
        $ev.Dispose()
        $signalled = $true
    } catch { $signalled = $false }

    if ($signalled) {
        # poll instead of sleeping a fixed amount: a healthy exit takes ~60 ms
        $waited = 0
        while (-not (Test-MutexFree) -and $waited -lt 3000) {
            Start-Sleep -Milliseconds 50
            $waited += 50
        }
        if (Test-MutexFree) { Info ('  stopped previous instance (' + $waited + ' ms)') }
        else { Warn '  previous instance did not exit in 3 s, killing it' }
    }

    if (-not (Test-MutexFree)) {
        Get-Process -Name 'powershell', 'wscript', 'MouseMeter' -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -ne $PID } |
            ForEach-Object {
                # only kill processes whose command line mentions this folder
                $cmd = (Get-CimInstance Win32_Process -Filter ('ProcessId=' + $_.Id) -ErrorAction SilentlyContinue).CommandLine
                if ($cmd -and $cmd -like ('*' + $root + '*')) {
                    try { $_.Kill(); $_.WaitForExit(1000) } catch { }
                }
            }
        Start-Sleep -Milliseconds 300
    }
}

# ---------- 3. launch in this process ----------

$argv = @()
if ($Settings) { $argv += '--settings' }
# Always pass --restart so a stale instance that survived the checks above still bows out
# instead of making this new copy exit on the mutex.
$argv += '--restart'

# Make sure the window shows up again if the user had hidden the widget earlier: the
# settings window is the anchor, and double-clicking the tray is the usual next step.
if ($Verbose) { Info ('  launching with: ' + ($argv -join ' ')) }

[void][System.Reflection.Assembly]::LoadFrom($build.Path)

try {
    [MouseMeter.Program]::Main([string[]]$argv)
} catch {
    Write-Host ''
    Write-Host '  RUNTIME ERROR' -ForegroundColor Red
    Write-Host ($_.Exception.ToString()) -ForegroundColor Red
    Write-Host ''
    Write-Host ('  full log: ' + (Join-Path $env:LOCALAPPDATA 'MouseMeter\error.log')) -ForegroundColor Gray
    exit 1
}
