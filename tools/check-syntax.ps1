# check-syntax.ps1 - compile every .cs outside Windows, as a syntax/API gate.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-syntax.ps1
#
# Why this exists: the shipping build runs through CodeDOM, which needs a Windows
# PowerShell. When you are editing on another machine (or in CI) you still want the
# compiler to tell you "line N is wrong" instead of finding out when you get home.
#
# What it proves: the whole tree compiles at C# 5 language level against the
# .NET reference assemblies, tests included. What it does NOT prove: that the
# CodeDOM/win32icon/winexe packaging step works — run Build.ps1 on Windows for that.
#
# Requires: dotnet SDK 6+, internet on first run (pulls the reference packs).

param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [switch]$RunTests
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK not found. Install .NET SDK 6 or newer, or just run Build.ps1 on Windows.'
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) 'mousemeter-syntax'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null

$proj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <!-- The shipping compiler is CodeDOM on .NET Framework 4.8: C# 5 only. Pin it here so a
         successful check actually means something for the real build. -->
    <LangVersion>5</LangVersion>
    <OutputType>Exe</OutputType>
    <UseAppHost>false</UseAppHost>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
    <NoWarn>CS1701;CS1702;CS0618;CA1416</NoWarn>
    <StartupObject>MouseMeter.TestRunner</StartupObject>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$(SrcDir)/*.cs" Exclude="$(SrcDir)/AssemblyInfo.cs" />
    <Compile Include="$(RootDir)/tests/TestRunner.cs" />
    <Reference Include="$(RefDir)/core/*.dll" />
    <Reference Include="$(RefDir)/wd/*.dll" />
  </ItemGroup>
</Project>
'@

$projPath = Join-Path $work 'check.csproj'
$content = $proj.Replace('$(SrcDir)', ((Join-Path $Root 'src') -replace '\\', '/')).
                   Replace('$(RootDir)', ($Root -replace '\\', '/')).
                   Replace('$(RefDir)', ((Join-Path $work 'ref') -replace '\\', '/'))
[System.IO.File]::WriteAllText($projPath, $content, (New-Object System.Text.UTF8Encoding $false))

# The WinForms / System.Drawing reference assemblies are not installed on Linux or macOS.
# Ask NuGet for them once and unpack into $work\ref.
Write-Host 'fetching reference assemblies...' -ForegroundColor Gray
$refDir = Join-Path $work 'ref'
New-Item -ItemType Directory -Force -Path (Join-Path $refDir 'core') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $refDir 'wd') | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($pair in @(
    @{ Id = 'microsoft.netcore.app.ref'; Ver = '6.0.6'; To = 'core' },
    @{ Id = 'microsoft.windowsdesktop.app.ref'; Ver = '6.0.6'; To = 'wd' })) {
    $nupkg = Join-Path $work ($pair.Id + '.nupkg')
    $url = 'https://www.nuget.org/api/v2/package/' + $pair.Id + '/' + $pair.Ver
    Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing
    $tmp = Join-Path $work ('x-' + $pair.To)
    [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $tmp)
    $src = Join-Path $tmp ('ref/net6.0')
    Copy-Item (Join-Path $src '*.dll') (Join-Path $refDir $pair.To) -Force
}

Write-Host 'compiling...' -ForegroundColor Gray
& dotnet build $projPath -v q --nologo 2>&1 | ForEach-Object { if ($_ -match 'error') { Write-Host $_ -ForegroundColor Red } }
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host '  syntax gate passed (C# 5)' -ForegroundColor Green

if ($RunTests) {
    # dotnet writes a self-contained runtimeconfig when implicit framework references are off;
    # patch it so the test host can find the shared runtime.
    $outDir = Join-Path $work 'bin/Debug/net6.0'
    $cfg = Join-Path $outDir 'check.runtimeconfig.json'
    $json = '{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"}}}'
    [System.IO.File]::WriteAllText($cfg, $json, (New-Object System.Text.UTF8Encoding $false))
    & dotnet (Join-Path $outDir 'check.dll')
    exit $LASTEXITCODE
}
