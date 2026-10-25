# Verify.ps1 - the single gate: compile, test, packaging dry-run.
#
#   powershell -ExecutionPolicy Bypass -File .\Verify.ps1
#   powershell -ExecutionPolicy Bypass -File .\Verify.ps1 -Quick     (skip the exe build)
#   powershell -ExecutionPolicy Bypass -File .\Verify.ps1 -Fix       (repair BOM / line endings)
#
# Why one command: the old workflow had three separate checks (compile, run tests, build the
# exe) and nothing forced you to run them. Forgetting step 2 is how a 564-line "successful"
# refactor ships with half its assertions red. This script is the one thing to remember, and
# .git/hooks/pre-commit calls it so remembering is no longer a requirement.
#
# Exit code is the contract: 0 = everything green, 1 = something failed.
#
# Relationship to tools\check-syntax.ps1: that one is the offline gate for machines without
# Windows (it compiles the same sources with dotnet at LangVersion 5). This one is the real
# thing and needs Windows, because it calls CodeDOM and Build.ps1. They overlap on the syntax
# check on purpose - the offline one is a subset, never a replacement.
# Every check prints one line, PASS or FAIL, so the output is skimmable and diffable.

param(
    [switch]$Quick,
    [switch]$Fix,
    [switch]$Verbose
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$script:failures = New-Object 'System.Collections.Generic.List[string]'
$script:checks = 0

function Step([string]$name) {
    $script:checks++
    Write-Host ('  ' + $name + ' ... ') -NoNewline
}

function Pass([string]$detail) {
    if ($detail) { Write-Host ('PASS  ' + $detail) -ForegroundColor Green }
    else { Write-Host 'PASS' -ForegroundColor Green }
}

function Fail([string]$reason, [string]$fixHint) {
    Write-Host 'FAIL' -ForegroundColor Red
    Write-Host ('        ' + $reason) -ForegroundColor Red
    if ($fixHint) { Write-Host ('        -> ' + $fixHint) -ForegroundColor Yellow }
    $script:failures.Add($reason)
}

Write-Host ''
Write-Host 'MouseMeter verify' -ForegroundColor Cyan
Write-Host ('  ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '   ' + $root) -ForegroundColor Gray
Write-Host ''

# ---------- 1. source hygiene: the two constraints that break the build silently ----------

Step 'encoding: .ps1 files carry a UTF-8 BOM'

# PowerShell 5.1 decodes a BOM-less .ps1 as the system ANSI codepage. Chinese comments then
# come out as mojibake, which has already corrupted array initialisers in this project.
$bomless = @()
Get-ChildItem $root -Filter *.ps1 -Recurse |
    Where-Object { $_.FullName -notmatch '\\\.git\\' } |
    ForEach-Object {
        $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
        $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        if (-not $hasBom) { $bomless += $_.FullName.Substring($root.Length).TrimStart('\', '/') }
    }

if ($bomless.Count -eq 0) {
    Pass
} elseif ($Fix) {
    foreach ($rel in $bomless) {
        $full = Join-Path $root $rel
        $text = [System.IO.File]::ReadAllText($full, [System.Text.Encoding]::UTF8)
        # ReadAllText on a BOM-less UTF-8 file already gives the right string; write it back
        # with a BOM so the next PowerShell 5.1 run decodes it identically.
        [System.IO.File]::WriteAllText($full, $text, (New-Object System.Text.UTF8Encoding $true))
    }
    Pass ('added BOM to ' + $bomless.Count + ' file(s): ' + ($bomless -join ', '))
} else {
    Fail ($bomless.Count.ToString() + ' file(s) without BOM: ' + ($bomless -join ', ')) 'run again with -Fix'
}

Step 'language level: no C# 6+ syntax in src\*.cs'

# CodeDOM compiles at C# 5. A single $"..." or ?. slips through review and fails the real
# build on Windows, so scan for the constructs that are cheap to detect textually.
$csharpViolations = @()
$patterns = @(
    @{ Rx = '\$"' ;      What = 'interpolated string (C# 6)' },
    @{ Rx = '\bnameof\s*\(' ; What = 'nameof (C# 6)' },
    @{ Rx = '\?\.' ;     What = 'null-conditional (C# 6)' },
    @{ Rx = '\busing\s+static\b' ; What = 'using static (C# 6)' }
)

Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object {
    $file = $_
    $lineNo = 0
    $inBlockComment = $false
    foreach ($line in [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8)) {
        $lineNo++
        $trim = $line.Trim()

        # Strip comments AND string literals before scanning. A regex such as
        # @"\.\{?...\}\(?:#\{.*\})?$" contains $ followed by " and would otherwise be
        # reported as an interpolated string - a false positive that trains people to ignore
        # the check, which is worse than not having it.
        if ($inBlockComment) {
            if ($trim -match '\*/') { $inBlockComment = $false; $trim = $trim.Substring($trim.IndexOf('*/') + 2) }
            else { continue }
        }
        if ($trim.StartsWith('//')) { continue }
        if ($trim.StartsWith('/*')) { $inBlockComment = $true; continue }

        $code = [regex]::Replace($trim, '"(?:[^"\\]|\\.)*"', '""')      # "..." with escapes
        $code = [regex]::Replace($code, '@"(?:[^"]|"")*"', '@""')          # @"..." verbatim
        $code = [regex]::Replace($code, "'(?:[^'\\]|\\.)*'", "''")     # '...'
        $code = [regex]::Replace($code, '//.*$', '')                       # trailing comment

        foreach ($p in $patterns) {
            if ($code -match $p.Rx) {
                $csharpViolations += ($file.Name + ':' + $lineNo + ' ' + $p.What)
            }
        }
    }
}

if ($csharpViolations.Count -eq 0) {
    Pass
} else {
    Fail (($csharpViolations | Select-Object -First 5) -join '; ') 'see START-HERE.md 2.1'
}

Step 'dependencies: only the four allowed assemblies'

$allowed = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
$badRefs = @()
Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object {
    $text = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
    # Add-Type / Assembly.Load of anything outside the whitelist would not resolve at runtime.
    foreach ($m in [regex]::Matches($text, 'Assembly\.Load\w*\(\s*"([^"]+)"')) {
        if ($allowed -notcontains $m.Groups[1].Value) { $badRefs += ($_.Name + ' -> ' + $m.Groups[1].Value) }
    }
}

if ($badRefs.Count -eq 0) { Pass }
else { Fail ($badRefs -join '; ') 'the CodeDOM host only resolves the four framework assemblies' }

# ---------- 2. compile + test ----------

# CodeDOM compilation only exists on .NET Framework (Windows PowerShell 5.1).
# Under PowerShell 7 / .NET Core it throws PlatformNotSupportedException, so there
# the compile+test gates delegate to tools\check-syntax.ps1 (dotnet SDK + reference
# packages, LangVersion 5), which produces an equivalent check.
$canCodeDom = $PSVersionTable.PSVersion.Major -lt 6

Step ('compile (C# 5)' + $(if ($canCodeDom) { ' [CodeDOM]' } else { ' [dotnet ref-pack]' }))

$build = $null
if ($canCodeDom) {
    try {
        . (Join-Path $root 'Compiler.ps1')
        $build = Build-MouseMeter -Root $root -Force
        Pass ($build.Chars.ToString() + ' chars')
    } catch {
        $detail = ($_.Exception.Message -split "`r?`n" | Where-Object { $_.Trim() }) -join ' | '
        Fail $detail 'fix the compile errors above, then re-run'
    }
} else {
    $offline = Join-Path $root 'tools\check-syntax.ps1'
    if (-not (Test-Path $offline)) {
        Fail 'tools\check-syntax.ps1 missing' 'restore it from the repo'
    } else {
        $cout = & $offline 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0) {
            $build = $true  # sentinel: compile gate passed, proceed to tests
            Pass (($cout -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 1))
        } else {
            $detail = ($cout -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 1)
            Fail $detail 'run tools\check-syntax.ps1 directly for full output'
        }
    }
}

if ($build) {
    Step 'unit tests'

    # The test runner lives in tests\ and is compiled with the app sources into the same DLL,
    # but its entry point is TestRunner.Main. Compiling a second assembly keeps the app build
    # untouched; both share Compiler.ps1 so they cannot drift.
    if (-not $canCodeDom) {
        $stamp = Join-Path $root '.verify-last'
        $out = & (Join-Path $root 'tools\check-syntax.ps1') -RunTests 2>&1 | Out-String
        Set-Content -Path $stamp -Value ($out -split "`r?`n" | Where-Object { $_ -match '^(passed:|  FAIL)' }) -Encoding UTF8
        $summary = ($out -split "`r?`n" | Where-Object { $_ -match '^passed:' }) -join ''
        if ($LASTEXITCODE -eq 0 -and $summary) {
            Pass $summary
        } else {
            $fails = ($out -split "`r?`n" | Where-Object { $_ -match 'FAIL' }) -join ' | '
            Fail ('tests red: ' + $fails) 'run tools\check-syntax.ps1 -RunTests directly to see full output'
        }
        # Skip the CodeDOM path below.
    } else {
    $testDll = Join-Path $env:TEMP ('mousemeter-tests-' + [guid]::NewGuid().ToString('N') + '.exe')
    try {
        $provider = New-Object Microsoft.CSharp.CSharpCodeProvider
        $cp = New-Object System.CodeDom.Compiler.CompilerParameters
        $cp.GenerateInMemory = $false
        $cp.GenerateExecutable = $true
        $cp.OutputAssembly = $testDll
        $cp.MainClass = 'MouseMeter.TestRunner'
        $cp.CompilerOptions = '/optimize /warn:0'
        foreach ($a in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')) {
            [void]$cp.ReferencedAssemblies.Add($a)
        }

        $src = Get-MouseMeterSource -Root $root
        $src += "`n" + [System.IO.File]::ReadAllText((Join-Path $root 'tests\TestRunner.cs'), [System.Text.Encoding]::UTF8)
        $res = $provider.CompileAssemblyFromSource($cp, $src)

        $errs = @($res.Errors | Where-Object { -not $_.IsWarning })
        if ($errs.Count -gt 0) {
            Fail ('test build failed: line ' + $errs[0].Line + ': ' + $errs[0].ErrorText) 'tests\TestRunner.cs'
        } else {
            # Capture instead of streaming so a failure does not scroll the useful summary away.
            $out = & $testDll 2>&1 | Out-String
            $summary = ($out -split "`r?`n" | Where-Object { $_ -match '^passed:' }) -join ''

            # Hand the result to Sync-Docs.ps1. It publishes the assertion count in the docs, and
            # the only trustworthy source for that number is the runner's own output — a scan of
            # the test source miscounts generic arguments and would report tests that never ran.
            $stamp = Join-Path $root '.verify-last'
            Set-Content -Path $stamp -Value ($out -split "`r?`n" | Where-Object { $_ -match '^(passed:|  FAIL)' }) -Encoding UTF8

            if ($LASTEXITCODE -eq 0 -and $summary) {
                Pass $summary
            } else {
                $fails = ($out -split "`r?`n" | Where-Object { $_ -match 'FAIL' }) -join ' | '
                Fail ('tests red: ' + $fails) 'run the test exe directly to see full output'
                if ($Verbose) { Write-Host $out }
            }
        }
    } catch {
        Fail ('could not run tests: ' + $_.Exception.Message) ''
    } finally {
        if (Test-Path $testDll) { Remove-Item $testDll -Force -ErrorAction SilentlyContinue }
    }
    }
}

# ---------- 3. packaging ----------

if (-not $Quick -and -not $canCodeDom) {
    # Build.ps1 is a CodeDOM pipeline; on pwsh 7 it cannot run. Packaging stays a Windows step.
    Write-Host ''
    Write-Host '  [skip] package step: Build.ps1 requires Windows PowerShell 5.1' -ForegroundColor DarkYellow
}

if (-not $Quick -and $canCodeDom) {
    Step 'package (dist\MouseMeter.exe)'

    $exe = Join-Path $root 'dist\MouseMeter.exe'
    $before = if (Test-Path $exe) { (Get-Item $exe).LastWriteTimeUtc } else { [datetime]::MinValue }

    try {
        & (Join-Path $root 'Build.ps1') | Out-Null
        if (-not (Test-Path $exe)) { throw 'Build.ps1 produced no exe' }
        $after = (Get-Item $exe).LastWriteTimeUtc
        if ($after -le $before -and $before -ne [datetime]::MinValue) {
            throw 'exe not refreshed (Build.ps1 may have bailed out early)'
        }
        $kb = [math]::Round((Get-Item $exe).Length / 1KB, 1)
        Pass ($kb.ToString() + ' KB')
    } catch {
        Fail $_.Exception.Message 'if the exe is running, quit it from the tray first'
    }
}

# ---------- summary ----------

Write-Host ''
if ($script:failures.Count -eq 0) {
    Write-Host ('  all ' + $script:checks + ' checks passed') -ForegroundColor Green
    Write-Host ''
    exit 0
} else {
    Write-Host ('  ' + $script:failures.Count + ' of ' + $script:checks + ' checks failed') -ForegroundColor Red
    Write-Host ''
    exit 1
}
