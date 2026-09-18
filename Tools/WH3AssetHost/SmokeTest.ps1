<#
.SYNOPSIS
    Runs the published WH3AssetHost through its production named-pipe protocol.

.DESCRIPTION
    The smoke test starts a published host, sends hello/initialize/
    getAnimationCatalog/exportModel/shutdown requests, and verifies that
    real GLBs are written. It first checks the lightweight ui.pack path, then
    resolves and exports one vanilla VariantMeshDefinition with its skeleton,
    material textures, and one catalogued animation. Temporary output is
    removed by default.

.EXAMPLE
    pwsh -File .\Tools\WH3AssetHost\SmokeTest.ps1

.EXAMPLE
    pwsh -File .\Tools\WH3AssetHost\SmokeTest.ps1 `
        -HostPath K:\projects\whmm\tools\WH3AssetHost\WH3AssetHost.exe `
        -GameDataPath 'K:\SteamLibrary\steamapps\common\Total War WARHAMMER III\data'
#>

[CmdletBinding()]
param(
    [string]$HostPath,
    [string]$GameDataPath,
    [string]$VanillaPackFilesCachePath,
    [string]$OutputRoot,
    [string]$AssetPath = 'ui\3dui\models\default\ground_plane_1m.rigid_model_v2',
    [string]$RichAssetPath = 'variantmeshes\variantmeshdefinitions\emp_ch_karl.variantmeshdefinition',
    [string[]]$RichPackPaths,
    [switch]$KeepOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($HostPath)) {
    $HostPath = Join-Path $repoRoot 'publish-trim-partial-final-safe\WH3AssetHost.exe'
}

if ([string]::IsNullOrWhiteSpace($GameDataPath)) {
    if (-not [string]::IsNullOrWhiteSpace($env:WH3_GAME_DATA)) {
        $GameDataPath = $env:WH3_GAME_DATA
    } else {
        $GameDataPath = 'K:\SteamLibrary\steamapps\common\Total War WARHAMMER III\data'
    }
}

if ([string]::IsNullOrWhiteSpace($VanillaPackFilesCachePath)) {
    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
        $VanillaPackFilesCachePath = ''
    } else {
        $VanillaPackFilesCachePath = Join-Path $env:APPDATA 'wh3mm\vanilla-pack-files-cache.bin'
    }
}

$packPath = Join-Path $GameDataPath 'ui.pack'
if ($null -eq $RichPackPaths -or $RichPackPaths.Count -eq 0) {
    $RichPackPaths = @(
        (Join-Path $GameDataPath 'variants.pack'),
        (Join-Path $GameDataPath 'variants4.pack'),
        (Join-Path $GameDataPath 'variants_dds4.pack'),
        (Join-Path $GameDataPath 'anim.pack'),
        (Join-Path $GameDataPath 'anim2.pack'),
        (Join-Path $GameDataPath 'anim_3.pack')
    )
}

$temporaryOutput = [string]::IsNullOrWhiteSpace($OutputRoot)
if ($temporaryOutput) {
    $OutputRoot = Join-Path ([IO.Path]::GetTempPath()) ('WH3AssetHost-smoke-' + [Guid]::NewGuid().ToString('N'))
}
$groundPlaneOutputPath = Join-Path $OutputRoot 'smoke-ground-plane.glb'
$richOutputPath = Join-Path $OutputRoot 'smoke-vmd-skeleton-material-animation.glb'
$pipeName = 'wh3asset-smoke-' + $PID + '-' + [Guid]::NewGuid().ToString('N')

$utf8 = [Text.UTF8Encoding]::new($false, $true)
$pipe = $null
$hostProcess = $null
$shutdownSent = $false
$requestNumber = 0
$exitCode = 0

function Read-ExactBytes {
    param(
        [Parameter(Mandatory = $true)] [IO.Stream]$Stream,
        [Parameter(Mandatory = $true)] [int]$Count
    )

    $buffer = New-Object byte[] $Count
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($read -le 0) {
            throw 'The host closed the pipe before a complete frame was received.'
        }
        $offset += $read
    }
    return ,$buffer
}

function Write-JsonFrame {
    param(
        [Parameter(Mandatory = $true)] [IO.Stream]$Stream,
        [Parameter(Mandatory = $true)] [object]$Value
    )

    $json = $Value | ConvertTo-Json -Compress -Depth 20
    $payload = $utf8.GetBytes($json)
    if ($payload.Length -eq 0 -or $payload.Length -gt 1MB) {
        throw "The request frame has an invalid payload length: $($payload.Length)."
    }

    $header = [BitConverter]::GetBytes([uint32]$payload.Length)
    $Stream.Write($header, 0, $header.Length)
    $Stream.Write($payload, 0, $payload.Length)
    $Stream.Flush()
}

function Read-JsonFrame {
    param([Parameter(Mandatory = $true)] [IO.Stream]$Stream)

    $header = Read-ExactBytes -Stream $Stream -Count 4
    $payloadLength = [BitConverter]::ToUInt32($header, 0)
    if ($payloadLength -eq 0 -or $payloadLength -gt 1MB) {
        throw "The host returned an invalid frame length: $payloadLength."
    }

    $payload = Read-ExactBytes -Stream $Stream -Count ([int]$payloadLength)
    return $utf8.GetString($payload) | ConvertFrom-Json
}

function Invoke-HostRequest {
    param(
        [Parameter(Mandatory = $true)] [string]$Command,
        [hashtable]$Fields = @{}
    )

    $script:requestNumber++
    $request = [ordered]@{
        protocolVersion = 1
        requestId = "smoke-$script:requestNumber"
        command = $Command
    }
    foreach ($key in $Fields.Keys) {
        $request[$key] = $Fields[$key]
    }

    Write-JsonFrame -Stream $pipe -Value $request
    $response = Read-JsonFrame -Stream $pipe
    if ($response.success -ne $true) {
        throw "$Command failed: $($response | ConvertTo-Json -Compress -Depth 20)"
    }
    Write-Host "PASS $Command"
    return $response
}

try {
    if (-not (Test-Path -LiteralPath $HostPath -PathType Leaf)) {
        throw "Host executable was not found: $HostPath"
    }
    if (-not (Test-Path -LiteralPath $packPath -PathType Leaf)) {
        throw "Vanilla ui.pack was not found: $packPath"
    }
    foreach ($richPackPath in $RichPackPaths) {
        if (-not (Test-Path -LiteralPath $richPackPath -PathType Leaf)) {
            throw "Rich smoke-test pack was not found: $richPackPath"
        }
    }
    if ((-not [string]::IsNullOrWhiteSpace($VanillaPackFilesCachePath)) -and (-not (Test-Path -LiteralPath $VanillaPackFilesCachePath -PathType Leaf))) {
        throw "Vanilla pack-file cache was not found: $VanillaPackFilesCachePath"
    }

    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $HostPath
    $startInfo.Arguments = 'serve --pipe "{0}" --parent-pid {1}' -f $pipeName, $PID
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardError = $true

    $hostProcess = [Diagnostics.Process]::new()
    $hostProcess.StartInfo = $startInfo
    if (-not $hostProcess.Start()) {
        throw "Could not start host executable: $HostPath"
    }

    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $pipe.Connect(30000)

    $hello = Invoke-HostRequest -Command 'hello'
    if ($hello.result.protocolVersion -ne 1) {
        throw "Unexpected host protocol version: $($hello.result.protocolVersion)"
    }

    Invoke-HostRequest -Command 'initialize' -Fields @{
        packPaths = @($packPath)
        outputRoot = $OutputRoot
        vanillaPackFilesCachePath = $VanillaPackFilesCachePath
    } | Out-Null

    $catalog = Invoke-HostRequest -Command 'getAnimationCatalog' -Fields @{
        assetPath = $AssetPath
    }
    if ($catalog.result.success -ne $true) {
        throw "The smoke asset was not found: $AssetPath"
    }

    $export = Invoke-HostRequest -Command 'exportModel' -Fields @{
        assetPath = $AssetPath
        outputPath = 'smoke-ground-plane.glb'
        animationPaths = @()
        exportMaterials = $false
        includeSkeleton = $false
        mirrorMesh = $false
    }
    if ($export.result.success -ne $true) {
        throw 'The host reported an unsuccessful export.'
    }

    if (-not (Test-Path -LiteralPath $groundPlaneOutputPath -PathType Leaf)) {
        throw "The export output was not created: $groundPlaneOutputPath"
    }
    $outputLength = (Get-Item -LiteralPath $groundPlaneOutputPath).Length
    if ($outputLength -le 0) {
        throw "The export output is empty: $groundPlaneOutputPath"
    }
    Write-Host "PASS export output ($outputLength bytes): $groundPlaneOutputPath"

    Invoke-HostRequest -Command 'initialize' -Fields @{
        packPaths = @($RichPackPaths)
        outputRoot = $OutputRoot
        vanillaPackFilesCachePath = $VanillaPackFilesCachePath
    } | Out-Null

    $richCatalog = Invoke-HostRequest -Command 'getAnimationCatalog' -Fields @{
        assetPath = $RichAssetPath
    }
    if ($richCatalog.result.success -ne $true) {
        throw "The rich smoke asset was not found: $RichAssetPath"
    }
    if ($richCatalog.result.hasSkeletonFile -ne $true) {
        throw "The rich smoke asset has no resolved skeleton: $($richCatalog.result.skeletonName)"
    }
    $richAnimations = @($richCatalog.result.animations)
    if ($richAnimations.Count -eq 0) {
        throw "The rich smoke asset has no catalogued animations: $RichAssetPath"
    }
    $richAnimationPath = [string]$richAnimations[0].path
    if ([string]::IsNullOrWhiteSpace($richAnimationPath)) {
        throw "The rich smoke catalog returned an empty animation path."
    }
    Write-Host "PASS rich catalog (VMD, skeleton '$($richCatalog.result.skeletonName)', animation '$richAnimationPath')"

    $richExport = Invoke-HostRequest -Command 'exportModel' -Fields @{
        assetPath = $RichAssetPath
        outputPath = 'smoke-vmd-skeleton-material-animation.glb'
        animationPaths = @($richAnimationPath)
        exportMaterials = $true
        includeSkeleton = $true
        mirrorMesh = $false
    }
    if ($richExport.result.success -ne $true) {
        throw "The rich smoke export was unsuccessful: $RichAssetPath"
    }
    if (-not (Test-Path -LiteralPath $richOutputPath -PathType Leaf)) {
        throw "The rich export output was not created: $richOutputPath"
    }
    $richOutputLength = (Get-Item -LiteralPath $richOutputPath).Length
    if ($richOutputLength -le 0) {
        throw "The rich export output is empty: $richOutputPath"
    }
    $richGltfText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($richOutputPath))
    foreach ($requiredGltfSection in @('"materials"', '"images"', '"skins"', '"animations"')) {
        if (-not $richGltfText.Contains($requiredGltfSection)) {
            throw "The rich GLB is missing the expected glTF section $requiredGltfSection."
        }
    }
    Write-Host "PASS rich export ($richOutputLength bytes; material textures, skeleton, animation): $richOutputPath"

    Invoke-HostRequest -Command 'shutdown' | Out-Null
    $shutdownSent = $true
    Write-Host 'Smoke test passed.'
} catch {
    $exitCode = 1
    Write-Error $_
} finally {
    if ($null -ne $pipe -and $pipe.IsConnected -and -not $shutdownSent) {
        try {
            Invoke-HostRequest -Command 'shutdown' | Out-Null
        } catch {
        }
    }

    if ($null -ne $pipe) {
        $pipe.Dispose()
    }

    if ($null -ne $hostProcess) {
        if (-not $hostProcess.HasExited) {
            $null = $hostProcess.WaitForExit(5000)
        }
        if (-not $hostProcess.HasExited) {
            $hostProcess.Kill()
            $null = $hostProcess.WaitForExit()
        }
        $stderr = $hostProcess.StandardError.ReadToEnd()
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            Write-Host "Host stderr:`n$stderr"
        }
        $hostProcess.Dispose()
    }

    if ($temporaryOutput -and -not $KeepOutput -and (Test-Path -LiteralPath $OutputRoot)) {
        Remove-Item -LiteralPath $OutputRoot -Recurse -Force
    }
}

exit $exitCode
