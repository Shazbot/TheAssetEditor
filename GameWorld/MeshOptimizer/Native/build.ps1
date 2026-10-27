param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tag = 'v1.3'
$expectedCommit = '9e1f07b159d3cb777f1c67ed31fc11fd117986f4'
$repository = 'https://github.com/zeux/meshoptimizer.git'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('assedfork-meshoptimizer-v1.3-' + [Guid]::NewGuid().ToString('N'))
$sourcePath = Join-Path $temporaryRoot 'source'
$buildPath = Join-Path $temporaryRoot 'build'
$outputPath = Join-Path $PSScriptRoot 'x64'
$gitPath = 'C:\Program Files\Git\cmd\git.exe'
$cmakePath = 'C:\Program Files\cmake\bin\cmake.exe'
$vswherePath = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'

foreach ($toolPath in @($gitPath, $cmakePath, $vswherePath)) {
    if (-not (Test-Path -LiteralPath $toolPath)) {
        throw "Required build tool was not found: $toolPath"
    }
}

$vsInstallPath = (& $vswherePath -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($vsInstallPath)) {
    throw 'MSVC x64 tools were not found by vswhere.exe.'
}
$vsDevCmdPath = Join-Path $vsInstallPath 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $vsDevCmdPath)) {
    throw "Visual Studio developer command script was not found: $vsDevCmdPath"
}

$quotedCmakePath = '"' + $cmakePath + '"'
$quotedSourcePath = '"' + $sourcePath + '"'
$quotedBuildPath = '"' + $buildPath + '"'
$configureCommand = "$quotedCmakePath -S $quotedSourcePath -B $quotedBuildPath -G `"NMake Makefiles`" -DCMAKE_BUILD_TYPE=Release -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded `"-DCMAKE_CXX_FLAGS_RELEASE=/O2 /Ob2 /DNDEBUG`" -DMESHOPT_BUILD_SHARED_LIBS=ON -DMESHOPT_BUILD_DEMO=OFF -DMESHOPT_BUILD_GLTFPACK=OFF -DMESHOPT_STABLE_EXPORTS=OFF -DMESHOPT_INSTALL=OFF"
$buildCommand = "$quotedCmakePath --build $quotedBuildPath --config Release"
$developerEnvironmentCommand = "call `"$vsDevCmdPath`" -arch=x64 -host_arch=x64"

try {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }

    & $gitPath clone --depth 1 --single-branch --branch $tag $repository $sourcePath
    if ($LASTEXITCODE -ne 0) {
        throw "git clone failed with exit code $LASTEXITCODE."
    }

    $actualCommit = (& $gitPath -C $sourcePath rev-parse HEAD | Select-Object -First 1).Trim()
    if ($actualCommit -ne $expectedCommit) {
        throw "Tag $tag resolved to $actualCommit; expected pinned commit $expectedCommit."
    }

    & $env:ComSpec /d /c "$developerEnvironmentCommand && $configureCommand"
    if ($LASTEXITCODE -ne 0) {
        throw "CMake configure failed with exit code $LASTEXITCODE."
    }

    & $env:ComSpec /d /c "$developerEnvironmentCommand && $buildCommand"
    if ($LASTEXITCODE -ne 0) {
        throw "CMake build failed with exit code $LASTEXITCODE."
    }

    $dllCandidates = @(Get-ChildItem -LiteralPath $buildPath -Filter 'meshoptimizer.dll' -File -Recurse)
    if ($dllCandidates.Count -ne 1) {
        throw "Expected one meshoptimizer.dll from the build; found $($dllCandidates.Count)."
    }

    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    $dllPath = Join-Path $outputPath 'meshoptimizer.dll'
    $headerPath = Join-Path $outputPath 'meshoptimizer.h'
    Copy-Item -LiteralPath $dllCandidates[0].FullName -Destination $dllPath -Force
    Copy-Item -LiteralPath (Join-Path $sourcePath 'src\meshoptimizer.h') -Destination $headerPath -Force

    $msvcToolsRoot = Join-Path $vsInstallPath 'VC\Tools\MSVC'
    $msvcToolset = Get-ChildItem -LiteralPath $msvcToolsRoot -Directory | Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1
    if ($null -eq $msvcToolset) {
        throw "MSVC toolset was not found under $msvcToolsRoot."
    }
    $dumpbinPath = Join-Path $msvcToolset.FullName 'bin\Hostx64\x64\dumpbin.exe'
    if (-not (Test-Path -LiteralPath $dumpbinPath)) {
        throw "dumpbin.exe was not found: $dumpbinPath"
    }

    $dependencies = (& $dumpbinPath /dependents $dllPath | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "dumpbin /dependents failed with exit code $LASTEXITCODE."
    }
    if ($dependencies -match '(?i)\b(?:MSVCP|VCRUNTIME)[^\r\n]*\.dll') {
        throw "The DLL imports the dynamic MSVC runtime despite the static CRT setting:`n$dependencies"
    }

    $exports = (& $dumpbinPath /exports $dllPath | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "dumpbin /exports failed with exit code $LASTEXITCODE."
    }
    foreach ($symbol in @('meshopt_simplifyWithAttributes', 'meshopt_simplifyScale', 'meshopt_optimizeVertexCache', 'meshopt_optimizeVertexFetchRemap')) {
        if ($exports -notmatch "\b$symbol\b") {
            throw "Expected exported function was missing: $symbol"
        }
    }

    $buildDate = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $versionLines = @(
        "Tag: $tag",
        "Commit SHA: $actualCommit",
        "Build date (UTC): $buildDate",
        "MSVC environment: $developerEnvironmentCommand",
        'CMake configure command:',
        $configureCommand,
        'CMake build command:',
        $buildCommand
    )
    Set-Content -LiteralPath (Join-Path $PSScriptRoot 'VERSION.txt') -Value $versionLines -Encoding UTF8

    Write-Output "Built $dllPath"
    Write-Output "Copied $headerPath"
    Write-Output "DLL bytes: $((Get-Item -LiteralPath $dllPath).Length)"
    Write-Output 'Dynamic MSVC runtime imports: none'
    Write-Output 'Verified exports: meshopt_simplifyWithAttributes, meshopt_simplifyScale, meshopt_optimizeVertexCache, meshopt_optimizeVertexFetchRemap'
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
