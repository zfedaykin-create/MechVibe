# Builds MechVibeBridge.exe with nothing but the MSVC toolchain.
# No CMake, no UE4SS, no Unreal headers, no Epic account.
#
# PowerShell locates vcvars64.bat (cmd chokes on the parentheses in
# %ProgramFiles(x86)%), then hands a generated batch file to cmd so the
# vcvars environment and cl.exe run in the same shell.

$ErrorActionPreference = "Stop"

$root   = $PSScriptRoot
$outDir = Join-Path $root "bin"
$src    = Join-Path $root "src\main.cpp"

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found - is Visual Studio / Build Tools installed?" }

$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "No VS install with the C++ toolchain found." }

$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars" }

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

Write-Host "Building with: $vcvars"

# /MT so the exe is standalone (no VC redist needed on the target machine).
$tool = Join-Path $root "tools\pipetest.cpp"

# _CRT_SECURE_NO_WARNINGS: pipetest uses fopen on purpose - it exists to prove the
# plain-CRT path Lua's io.open takes actually works against the pipe.
$batch = @"
@echo off
call "$vcvars" >nul || exit /b 1
cl.exe /nologo /std:c++20 /W4 /EHsc /O2 /MT /Fe:"$outDir\MechVibeBridge.exe" /Fo:"$outDir\\" "$src" || exit /b 1
cl.exe /nologo /std:c++20 /W4 /EHsc /O2 /MT /D_CRT_SECURE_NO_WARNINGS /Fe:"$outDir\pipetest.exe" /Fo:"$outDir\\" "$tool" || exit /b 1
"@

$batchPath = Join-Path $env:TEMP "mechvibe_build_$PID.bat"
Set-Content -Path $batchPath -Value $batch -Encoding ascii

try {
    & cmd.exe /c $batchPath
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
} finally {
    Remove-Item $batchPath -Force -ErrorAction SilentlyContinue
}

Write-Host "`nBuilt: $outDir\MechVibeBridge.exe"
