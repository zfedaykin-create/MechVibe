# Builds the SimHub plugin with csc.exe only - no .NET SDK, no MSBuild project.
#
# The settings page is a WPF control built in code rather than XAML, which is what
# keeps this possible: plain assembly references are enough, with no XAML compile
# pass to run.

$ErrorActionPreference = "Stop"

$root   = $PSScriptRoot
$outDir = Join-Path $root "bin"
# SimHub identifies user plugins by a "User." prefix - the bundled SDK demo is
# User.PluginSdkDemo, and without it the assembly is never even scanned.
$outDll = Join-Path $outDir "User.MechVibe.dll"

$sources = Get-ChildItem (Join-Path $root "src") -Filter *.cs | Select-Object -ExpandProperty FullName
if (-not $sources) { throw "No sources found in src\" }

$simhub = "C:\Program Files (x86)\SimHub"
if (-not (Test-Path $simhub)) { throw "SimHub not found at $simhub" }

# Prefer the Roslyn compiler from VS Build Tools (modern C#); fall back to the
# in-box .NET Framework csc, which is much older and stricter about syntax.
$csc = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -products * -property installationPath
    if ($vsPath) {
        $candidate = Join-Path $vsPath "MSBuild\Current\Bin\Roslyn\csc.exe"
        if (Test-Path $candidate) { $csc = $candidate }
    }
}
if (-not $csc) {
    $candidate = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (Test-Path $candidate) { $csc = $candidate }
}
if (-not $csc) { throw "No C# compiler found." }

Write-Host "Compiler: $csc"
Write-Host "Sources : $($sources.Count) file(s)"

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# Only the assemblies the plugin actually touches - SimHub bundles these already.
# log4net is needed even though the plugin never names it: SimHub.Logging's Current
# property is typed as log4net's ILog, so the compiler has to see it.
$refs = @(
    (Join-Path $simhub "SimHub.Plugins.dll"),
    (Join-Path $simhub "SimHub.Logging.dll"),
    (Join-Path $simhub "GameReaderCommon.dll"),
    (Join-Path $simhub "log4net.dll")
)
foreach ($r in $refs) {
    if (-not (Test-Path $r)) { throw "Missing reference: $r" }
}

# WPF, for the settings page. Reference assemblies aren't necessarily installed, and
# the DLLs are split across directories: the WPF ones live in a WPF subfolder,
# System.Xaml does not.
$searchDirs = @(
    "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8",
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF",
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
)
foreach ($w in @("PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll", "System.Xaml.dll")) {
    $hit = $null
    foreach ($d in $searchDirs) {
        $p = Join-Path $d $w
        if (Test-Path $p) { $hit = $p; break }
    }
    if (-not $hit) { throw "Could not locate $w - needed for the settings page." }
    $refs += $hit
}

$cscArgs = @(
    "/target:library",
    "/platform:AnyCPU",
    "/nologo",
    "/optimize+",
    "/out:$outDll"
)
foreach ($r in $refs)    { $cscArgs += "/reference:$r" }
foreach ($s in $sources) { $cscArgs += $s }

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

Write-Host "`nBuilt: $outDll"
Write-Host "Install with: install.ps1  (copies the DLL into the SimHub folder)"
