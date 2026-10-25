# Build.ps1 - compiles src\*.cs into a standalone MouseMeter.exe
#
#   powershell -ExecutionPolicy Bypass -File .\Build.ps1
#   powershell -ExecutionPolicy Bypass -File .\Build.ps1 -Output dist
#
# The exe targets .NET Framework 4.8, which ships with Windows 10 1803+ and Windows 11,
# so the result needs no runtime install. Output is a single ~110 KB file.
#
# Uses the same source merge as Compiler.ps1 (CompileAssemblyFromSource takes one unit),
# so the exe and the script-hosted build can never drift apart.

param(
    [string]$Output = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

. (Join-Path $root 'Compiler.ps1')

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host ('[' + (Get-Date).ToString('HH:mm:ss') + '] ' + $text) -ForegroundColor Cyan
}

Write-Step 'merging sources'
$src = Get-MouseMeterSource -Root $root
Write-Host ('  ' + $src.Length + ' chars from src\*.cs')

$outDir = Join-Path $root $Output
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$exePath = Join-Path $outDir 'MouseMeter.exe'
$icoPath = Join-Path $root 'MouseMeter.ico'

Write-Step 'rendering application icon'
# Always redraw: the .ico is generated from IconFactory, so rebuilding it is how the exe icon
# stays in step with the tray glyph. Skipping when the file merely exists would let a stale
# icon from an older design survive indefinitely.
try {
    # The icon generator lives inside the same source we are about to compile, so
    # bootstrap it through the script compiler (cached by hash) before building.
    $boot = Build-MouseMeter -Root $root -Force
    [void][System.Reflection.Assembly]::LoadFrom($boot.Path)
    [MouseMeter.IconFactory]::WriteIco($icoPath)
    Write-Host ('  MouseMeter.ico redrawn (' + (Get-Item $icoPath).Length + ' bytes)')
} catch {
    Write-Host ('  icon step skipped: ' + $_.Exception.Message) -ForegroundColor Yellow
    if (-not (Test-Path $icoPath)) {
        Write-Host '  no icon available, exe will use the default .NET icon' -ForegroundColor Yellow
    }
}

Write-Step 'compiling MouseMeter.exe'

# Windows locks a running image, so writing over a live exe fails with a bare
# IOException. Say what to do instead of letting csc report "Access denied" on a path.
if (Test-Path $exePath) {
    $lockTest = $null
    try {
        $lockTest = [System.IO.File]::Open($exePath, 'Open', 'ReadWrite', 'None')
    } catch {
        throw ('MouseMeter.exe is running. Quit it from the tray first, then rebuild.' +
               "`n(Run .\dev.cmd to work from source - it restarts the app for you.)")
    } finally {
        if ($lockTest -ne $null) { $lockTest.Dispose() }
    }
}

$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$cp = New-Object System.CodeDom.Compiler.CompilerParameters
$cp.GenerateInMemory = $false
$cp.GenerateExecutable = $true
$cp.OutputAssembly = $exePath
$cp.MainClass = 'MouseMeter.Program'
$cp.IncludeDebugInformation = $false
# CodeDOM does not derive the assembly identity from the output path, and csc has no
# /assemblyname switch; the name comes from AssemblyName in src\AssemblyInfo.cs. Set it
# there, not here, or Explorer still shows an empty "Product name".
$cp.CompilerOptions = '/optimize /warn:0 /target:winexe'

# win32icon must point at a real file or csc fails the whole build
if (Test-Path $icoPath) {
    $cp.CompilerOptions += ' /win32icon:"' + $icoPath + '"'
}

foreach ($a in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')) {
    [void]$cp.ReferencedAssemblies.Add($a)
}

$res = $provider.CompileAssemblyFromSource($cp, $src)
if ($res.Errors.Count -gt 0) {
    $msgs = @()
    foreach ($e in $res.Errors) {
        if ($e.IsWarning) { continue }
        $msgs += ('line ' + $e.Line + ': ' + $e.ErrorText)
    }
    throw ("BUILD FAILED`n" + ($msgs -join "`n"))
}

if (-not (Test-Path $exePath)) { throw 'BUILD FAILED: no output produced' }

Write-Step 'done'
$size = (Get-Item $exePath).Length
Write-Host ('  ' + $exePath)
Write-Host ('  ' + [math]::Round($size / 1KB, 1) + ' KB')
Write-Host ''
Write-Host 'Double-click it, or run:' -ForegroundColor Gray
Write-Host ('  "' + $exePath + '"') -ForegroundColor Gray
Write-Host ('  "' + $exePath + '" --settings') -ForegroundColor Gray
