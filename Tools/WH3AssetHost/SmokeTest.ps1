<#
.SYNOPSIS
    Runs the published WH3AssetHost through its production named-pipe protocol.

.DESCRIPTION
    The smoke test starts a published host, sends hello/initialize/
    getAnimationCatalog/exportModel/shutdown requests, and verifies that a
    real GLB is written. By default it uses the vanilla ui.pack and the
    vanilla pack-file cache, then removes its temporary output.

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
$temporaryOutput = [string]::IsNullOrWhiteSpace($OutputRoot)
if ($temporaryOutput) {
    $OutputRoot = Join-Path ([IO.Path]::GetTempPath()) ('WH3AssetHost-smoke-' + [Guid]::NewGuid().ToString('N'))
}
$outputPath = Join-Path $OutputRoot 'smoke-ground-plane.glb'
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

    if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
        throw "The export output was not created: $outputPath"
    }
    $outputLength = (Get-Item -LiteralPath $outputPath).Length
    if ($outputLength -le 0) {
        throw "The export output is empty: $outputPath"
    }
    Write-Host "PASS export output ($outputLength bytes): $outputPath"

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
