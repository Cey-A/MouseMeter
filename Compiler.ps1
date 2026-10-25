# Compiler.ps1 - merges src\*.cs into one translation unit, caches the compiled DLL
# keyed by a content hash so restarts cost ~15 ms instead of ~1 s.

function Get-MouseMeterSource {
    param([string]$Root)

    $srcDir = Join-Path $Root 'src'
    $files = Get-ChildItem $srcDir -Filter *.cs | Sort-Object Name
    if ($files.Count -eq 0) { throw "no .cs files under $srcDir" }

    $usings = New-Object 'System.Collections.Generic.List[string]'
    $body = New-Object System.Text.StringBuilder

    foreach ($f in $files) {
        $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
        foreach ($line in ($text -split "`r?`n")) {
            if ($line -match '^\s*using\s+[A-Za-z0-9_.]+\s*;\s*$') {
                $trim = $line.Trim()
                if (-not $usings.Contains($trim)) { $usings.Add($trim) }
            } else {
                [void]$body.AppendLine($line)
            }
        }
        [void]$body.AppendLine('')
    }

    return (($usings | ForEach-Object { $_ }) -join "`n") + "`n`n" + $body.ToString()
}

function Build-MouseMeter {
    param(
        [string]$Root = 'C:\Users\23381\Tools\MouseMeter',
        [switch]$Force
    )

    # LOCALAPPDATA is null outside Windows (Linux pwsh, CI containers), and Join-Path with a
    # null base throws "Cannot bind argument to parameter 'Path'". Falling back keeps the
    # verify gate runnable on a build machine without changing where Windows caches go.
    $base = $env:LOCALAPPDATA
    if (-not $base) { $base = Join-Path ([System.IO.Path]::GetTempPath()) 'MouseMeter' }
    $cacheDir = Join-Path $base 'MouseMeter\cache'
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

    $src = Get-MouseMeterSource -Root $Root
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $hash = [System.BitConverter]::ToString(
        $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($src))).Replace('-', '').Substring(0, 16)
    $sha.Dispose()

    $dll = Join-Path $cacheDir ("MouseMeter-" + $hash + ".dll")

    if ((Test-Path $dll) -and -not $Force) {
        return @{ Path = $dll; Compiled = $false; Chars = $src.Length }
    }

    $provider = New-Object Microsoft.CSharp.CSharpCodeProvider
    $cp = New-Object System.CodeDom.Compiler.CompilerParameters
    $cp.GenerateInMemory = $false
    $cp.OutputAssembly = $dll
    $cp.CompilerOptions = '/optimize /warn:0'
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

    # keep only the freshest few dlls around
    Get-ChildItem $cacheDir -Filter 'MouseMeter-*.dll' |
        Where-Object { $_.FullName -ne $dll } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip 1 |
        Remove-Item -Force -ErrorAction SilentlyContinue

    return @{ Path = $dll; Compiled = $true; Chars = $src.Length }
}
