param(
    [switch]$Settings
)

# MouseMeter launcher
# Compiles src\*.cs on first run (cached by content hash), then starts the app.
# Nothing is installed system-wide; everything lives next to this file.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

. (Join-Path $root 'Compiler.ps1')

function Fail([string]$message) {
    $logDir = Join-Path $env:LOCALAPPDATA 'MouseMeter'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    Add-Content -Path (Join-Path $logDir 'error.log') -Value (
        (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + "`r`n" + $message + "`r`n")
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show($message, 'MouseMeter 启动失败',
        [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error)
    exit 1
}

try {
    $build = Build-MouseMeter -Root $root
} catch {
    Fail $_.Exception.Message
}

try {
    [void][System.Reflection.Assembly]::LoadFrom($build.Path)

    $icoPath = Join-Path $root 'MouseMeter.ico'
    if (-not (Test-Path $icoPath)) {
        try { [MouseMeter.IconFactory]::WriteIco($icoPath) } catch { }
    }

    $argv = @()
    if ($Settings) { $argv += '--settings' }

    [MouseMeter.Program]::Main([string[]]$argv)
} catch {
    Fail ($_.Exception.ToString())
}
