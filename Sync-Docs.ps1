# Sync-Docs.ps1 - regenerate the numbers and the file tree inside the markdown docs.
#
#   powershell -ExecutionPolicy Bypass -File .\Sync-Docs.ps1
#   powershell -ExecutionPolicy Bypass -File .\Sync-Docs.ps1 -Check   (verify only, no writes)
#
# Why: "88 项断言" lived in README.md and START-HERE.md by hand. It went 66 -> 71 -> 88 and one
# of the three places was missed every time. The counts are now derived from the tree, so the
# docs cannot drift — if they would drift, this script says so and -Check fails the commit.
#
# How it works: each generated region is fenced by markers.
#
#     <!-- AUTO:assertions -->88 项<!-- /AUTO -->
#
# Everything between the markers is replaced. Anything outside them is left exactly as written,
# so hand-written prose and these generated facts coexist in the same file.

param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# ---------- facts ----------

function Get-Facts {
    $f = @{}

    # Assertions: taken from the last line the test runner printed, not from a source scan.
    # A regex over TestRunner.cs undercounts (generic arguments contain commas) and would happily
    # report a number no test ever executed. Running the suite is the only honest source, so we
    # ask Verify.ps1 to leave its summary behind and read it back here.
    $stamp = Join-Path $root '.verify-last'
    $count = $null
    if (Test-Path $stamp) {
        $match = Select-String -Path $stamp -Pattern 'passed:\s*(\d+)\s+failed:\s*0' | Select-Object -First 1
        if ($match) { $count = $match.Matches[0].Groups[1].Value }
        else {
            # Suite was red: do not publish a stale green number.
            $m2 = Select-String -Path $stamp -Pattern '(\d+)\s*项' | Select-Object -First 1
            $count = if ($m2) { $m2.Matches[0].Groups[1].Value + '（含失败）' } else { '?' }
        }
    }
if ($count) { $f['assertions'] = $count + ' 项' }
    else { $f.Remove('assertions') }   # unknown -> leave the marker untouched rather than write "?"

    # Source size
    $srcFiles = @(Get-ChildItem (Join-Path $root 'src') -Filter *.cs)
    $lines = 0
    $largest = $null; $largestLines = 0
    foreach ($file in $srcFiles) {
        $n = ([System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8)).Count
        $lines += $n
        if ($n -gt $largestLines) { $largestLines = $n; $largest = $file.Name }
    }
    $f['srclines'] = $lines.ToString()
    $f['srcfiles'] = $srcFiles.Count.ToString()
    $f['largest'] = $largest + ' ' + $largestLines + ' 行'

    # Roadmap progress: a P-item is "done" when its heading says so.
    $roadmap = Join-Path $root 'docs\ROADMAP.md'
    $done = 0; $total = 0
    if (Test-Path $roadmap) {
        foreach ($line in [System.IO.File]::ReadAllLines($roadmap, [System.Text.Encoding]::UTF8)) {
            if ($line -match '^\s*###\s+P\d-\d') {
                $total++
                if ($line -match '已完成|已实现') { $done++ }
            }
        }
    }
    $f['roadmap'] = ($done.ToString() + ' / ' + $total.ToString())

    return $f
}

# ---------- rewriting ----------

function Update-Markers([string]$path, [hashtable]$facts) {
    if (-not (Test-Path $path)) { return 0 }

    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $original = $text
    $replaced = 0

    # <!-- AUTO:key --> ... <!-- /AUTO -->
    $pattern = '<!--\s*AUTO:(\w+)\s*-->.*?<!--\s*/AUTO\s*-->'
    $evaluator = [System.Text.RegularExpressions.MatchEvaluator]{
        param($m)
        $key = $m.Groups[1].Value
        if (-not $facts.ContainsKey($key)) {
            # Unknown key: leave it alone rather than blanking the reader's content, but say so.
            # A silently skipped marker is how "88 项" turns into a stale "?" nobody notices.
            Write-Host ('  ! unknown marker ' + $key) -ForegroundColor Yellow
            $script:unknown++
            return $m.Value
        }
        return '<!-- AUTO:' + $key + ' -->' + $facts[$key] + '<!-- /AUTO -->'
    }
    $text = [regex]::Replace($text, $pattern, $evaluator)

    if ($text -ne $original) {
        $replaced = ([regex]::Matches($text, '<!--\s*AUTO:\w+\s*-->')).Count
        if (-not $Check) {
            [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))
        }
        $rel = $path.Substring($root.Length).TrimStart('\', '/')
        Write-Host ('  ' + $rel + ': ' + $replaced + ' marker(s)')
    }
    return $replaced
}

# ---------- main ----------

$facts = Get-Facts

Write-Host ''
Write-Host 'facts' -ForegroundColor Cyan
foreach ($k in ($facts.Keys | Sort-Object)) {
    Write-Host ('  ' + $k.PadRight(12) + ' = ' + $facts[$k]) -ForegroundColor Gray
}
Write-Host ''

Write-Host 'documents' -ForegroundColor Cyan

$targets = @(
    (Join-Path $root 'README.md'),
    (Join-Path $root 'START-HERE.md')
) + @(Get-ChildItem (Join-Path $root 'docs') -Filter *.md | ForEach-Object { $_.FullName })

$script:unknown = 0
$changed = 0
foreach ($t in $targets) { $changed += (Update-Markers $t $facts) }

Write-Host ''
if ($script:unknown -gt 0) {
    Write-Host ('  ' + $script:unknown + ' marker(s) could not be resolved - is .verify-last missing?') -ForegroundColor Yellow
    Write-Host '  run Verify.ps1 first' -ForegroundColor Gray
    exit 1
}

if ($Check) {
    if ($changed -gt 0) {
        Write-Host '  docs are stale - run Sync-Docs.ps1 to fix' -ForegroundColor Yellow
        exit 1
    }
    Write-Host '  docs are in sync' -ForegroundColor Green
    exit 0
}

if ($changed -eq 0) { Write-Host '  nothing to update' -ForegroundColor Green }
else { Write-Host ('  updated ' + $changed + ' marker(s)') -ForegroundColor Green }
Write-Host ''
