<#
.SYNOPSIS
    Builds Native/DirectXTexShim/bin/win-x64/DirectXTexShim.dll (x64 Release) from the pinned DirectXTex source.

.DESCRIPTION
    Needs Visual Studio 2022 with the "Desktop development with C++" workload (its bundled CMake is used).
    The DirectXTex source is fetched by commit hash (see directxtex.pin) into the build folder. The resulting
    DLL is committed to the repository so that .NET builds and CI need no C++ toolchain; rerun this script and
    commit the DLL only when directxtex.pin or src/shim.cpp changes.
#>
[CmdletBinding()]
param(
    [string]$VisualStudio = 'D:\Microsoft Visual Studio\2022\Community'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$cmake = Join-Path $VisualStudio 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (-not (Test-Path $cmake)) { $cmake = (Get-Command cmake -ErrorAction Stop).Source }
$build = Join-Path $here 'build'
& $cmake -S $here -B $build -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE -ne 0) { throw 'cmake configure failed' }
& $cmake --build $build --config Release --target DirectXTexShim
if ($LASTEXITCODE -ne 0) { throw 'cmake build failed' }
$dll = Join-Path $here 'bin\win-x64\DirectXTexShim.dll'
Write-Host ('Built {0} ({1:N0} bytes)' -f $dll, (Get-Item $dll).Length)
