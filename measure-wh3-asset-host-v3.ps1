[CmdletBinding()]
param(
    [string]$Project = "Tools\WH3AssetHost\WH3AssetHost.csproj",
    [string]$OutputRoot = "publish-measure",
    [string]$RuntimeIdentifier = "win-x64",
    [int]$TopFiles = 50,
    [switch]$KeepExisting
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Write-Section {
    param([string]$Text)
    Write-Host ""
    Write-Host "=== $Text ==="
}

function Format-MB {
    param([long]$Bytes)
    [math]::Round($Bytes / 1MB, 2)
}

function Get-DirectorySizeBytes {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return 0
    }

    $sum = (Get-ChildItem $Path -File -Recurse -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum

    if ($null -eq $sum) {
        return 0
    }

    [long]$sum
}

function Get-TopFileRows {
    param(
        [string]$Path,
        [int]$Count
    )

    if (-not (Test-Path $Path)) {
        return @()
    }

    $basePath = (Resolve-Path $Path).Path

    Get-ChildItem $Path -File -Recurse |
        Sort-Object Length -Descending |
        Select-Object -First $Count |
        ForEach-Object {
            $relative = $_.FullName.Substring($basePath.Length).TrimStart('\', '/')
            [PSCustomObject]@{
                File  = $relative
                MB    = [math]::Round($_.Length / 1MB, 2)
                Bytes = $_.Length
            }
        }
}

function Invoke-DotNet {
    param(
        [string[]]$Arguments,
        [string]$LogPath,
        [switch]$AllowFailure
    )

    Write-Host ("dotnet " + ($Arguments -join " "))

    $output = & dotnet @Arguments 2>&1
    $exitCode = $LASTEXITCODE

    $output | Out-File -FilePath $LogPath -Encoding utf8

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        $output | ForEach-Object { Write-Host $_ }
        throw "dotnet command failed with exit code $exitCode. See: $LogPath"
    }

    [PSCustomObject]@{
        ExitCode = $exitCode
        Output   = $output
    }
}

function Add-MarkdownFileTable {
    param(
        [System.Text.StringBuilder]$Builder,
        [string]$Title,
        [object[]]$Rows
    )

    [void]$Builder.AppendLine("## $Title")
    [void]$Builder.AppendLine("")
    [void]$Builder.AppendLine("| File | MB |")
    [void]$Builder.AppendLine("|---|---:|")

    foreach ($row in $Rows) {
        $safeName = $row.File.Replace("|", "\|")
        [void]$Builder.AppendLine("| $safeName | $($row.MB) |")
    }

    [void]$Builder.AppendLine("")
}

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Get-Location).Path
}
$RepoRoot = (Resolve-Path $RepoRoot).Path
Set-Location $RepoRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet was not found on PATH. Install the .NET 10 SDK and retry."
}

$ProjectPath = Join-Path $RepoRoot $Project
if (-not (Test-Path $ProjectPath)) {
    throw "Project not found: $ProjectPath"
}
$ProjectPath = (Resolve-Path $ProjectPath).Path

$MeasureRoot = Join-Path $RepoRoot $OutputRoot

if ((Test-Path $MeasureRoot) -and -not $KeepExisting) {
    Remove-Item $MeasureRoot -Recurse -Force
}

New-Item $MeasureRoot -ItemType Directory -Force | Out-Null

$LogRoot = Join-Path $MeasureRoot "logs"
$WhyRoot = Join-Path $MeasureRoot "nuget-why"
New-Item $LogRoot -ItemType Directory -Force | Out-Null
New-Item $WhyRoot -ItemType Directory -Force | Out-Null

$SingleDir = Join-Path $MeasureRoot "single"
$ExpandedDir = Join-Path $MeasureRoot "expanded"
$FrameworkDir = Join-Path $MeasureRoot "framework"

Write-Section "Environment"

$dotnetInfo = & dotnet --info 2>&1
$dotnetInfo | Out-File (Join-Path $MeasureRoot "dotnet-info.txt") -Encoding utf8
$dotnetVersion = (& dotnet --version).Trim()

Write-Host "Repo:    $RepoRoot"
Write-Host "Project: $ProjectPath"
Write-Host ".NET:    $dotnetVersion"

Write-Section "Restore"

Invoke-DotNet `
    -Arguments @("restore", $ProjectPath, "-r", $RuntimeIdentifier) `
    -LogPath (Join-Path $LogRoot "restore.txt") | Out-Null

Write-Section "1/3 Shipping single-file publish"

Invoke-DotNet `
    -Arguments @(
        "publish", $ProjectPath,
        "-c", "Release",
        "-r", $RuntimeIdentifier,
        "--self-contained", "true",
        "--no-restore",
        "-o", $SingleDir
    ) `
    -LogPath (Join-Path $LogRoot "publish-single.txt") | Out-Null

Write-Section "2/3 Expanded self-contained publish"

Invoke-DotNet `
    -Arguments @(
        "publish", $ProjectPath,
        "-c", "Release",
        "-r", $RuntimeIdentifier,
        "--self-contained", "true",
        "--no-restore",
        "-p:PublishSingleFile=false",
        "-p:IncludeNativeLibrariesForSelfExtract=false",
        "-p:IncludeAllContentForSelfExtract=false",
        "-p:EnableCompressionInSingleFile=false",
        "-o", $ExpandedDir
    ) `
    -LogPath (Join-Path $LogRoot "publish-expanded.txt") | Out-Null

Write-Section "3/3 Framework-dependent publish"

Invoke-DotNet `
    -Arguments @(
        "publish", $ProjectPath,
        "-c", "Release",
        "-r", $RuntimeIdentifier,
        "--self-contained", "false",
        "--no-restore",
        "-p:PublishSingleFile=false",
        "-p:IncludeNativeLibrariesForSelfExtract=false",
        "-p:IncludeAllContentForSelfExtract=false",
        "-o", $FrameworkDir
    ) `
    -LogPath (Join-Path $LogRoot "publish-framework.txt") | Out-Null

Write-Section "Package graph"

Invoke-DotNet `
    -Arguments @(
        "list", $ProjectPath,
        "package",
        "--include-transitive"
    ) `
    -LogPath (Join-Path $MeasureRoot "packages.txt") | Out-Null

Write-Section "Project references"

Invoke-DotNet `
    -Arguments @(
        "list", $ProjectPath,
        "reference"
    ) `
    -LogPath (Join-Path $MeasureRoot "references.txt") | Out-Null

Write-Section "Why suspicious packages are present"

$suspiciousPackages = @(
    "MonoGame.Framework.WindowsDX",
    "Libuv",
    "CommunityToolkit.Mvvm",
    "CommunityToolkit.Diagnostics",
    "Newtonsoft.Json",
    "NVorbis",
    "OggVorbisEncoder",
    "Pfim",
    "System.Drawing.Common",
    "SharpGLTF.Core",
    "SharpGLTF.Toolkit"
)

$whyResults = @()

foreach ($package in $suspiciousPackages) {
    $safeName = $package.Replace(".", "_")
    $logPath = Join-Path $WhyRoot "$safeName.txt"

    $result = Invoke-DotNet `
        -Arguments @("nuget", "why", $ProjectPath, $package) `
        -LogPath $logPath `
        -AllowFailure

    $whyResults += [PSCustomObject]@{
        Package  = $package
        ExitCode = $result.ExitCode
        Log      = "nuget-why/$safeName.txt"
    }
}

Write-Section "Collecting size data"

$singleBytes = Get-DirectorySizeBytes $SingleDir
$expandedBytes = Get-DirectorySizeBytes $ExpandedDir
$frameworkBytes = Get-DirectorySizeBytes $FrameworkDir

$singleRows = @(Get-TopFileRows $SingleDir $TopFiles)
$expandedRows = @(Get-TopFileRows $ExpandedDir $TopFiles)
$frameworkRows = @(Get-TopFileRows $FrameworkDir $TopFiles)

$singleRows | Export-Csv (Join-Path $MeasureRoot "single-files.csv") -NoTypeInformation -Encoding utf8
$expandedRows | Export-Csv (Join-Path $MeasureRoot "expanded-files.csv") -NoTypeInformation -Encoding utf8
$frameworkRows | Export-Csv (Join-Path $MeasureRoot "framework-files.csv") -NoTypeInformation -Encoding utf8

$runtimeApproxBytes = [math]::Max(0, $expandedBytes - $frameworkBytes)

$report = New-Object System.Text.StringBuilder

[void]$report.AppendLine("# WH3AssetHost Publish Measurement")
[void]$report.AppendLine("")
[void]$report.AppendLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
[void]$report.AppendLine("")
[void]$report.AppendLine(("Project: ``{0}``" -f $Project))
[void]$report.AppendLine("")
[void]$report.AppendLine((".NET SDK: ``{0}``" -f $dotnetVersion))
[void]$report.AppendLine("")

[void]$report.AppendLine("## Summary")
[void]$report.AppendLine("")
[void]$report.AppendLine("| Build | Total MB |")
[void]$report.AppendLine("|---|---:|")
[void]$report.AppendLine("| Shipping single-file | $(Format-MB $singleBytes) |")
[void]$report.AppendLine("| Expanded self-contained | $(Format-MB $expandedBytes) |")
[void]$report.AppendLine("| Framework-dependent | $(Format-MB $frameworkBytes) |")
[void]$report.AppendLine("| Approx. bundled runtime delta | $(Format-MB $runtimeApproxBytes) |")
[void]$report.AppendLine("")
[void]$report.AppendLine("> The runtime delta is only an approximation: self-contained and framework-dependent publish layouts can differ in more ways than just the .NET runtime.")
[void]$report.AppendLine("")

Add-MarkdownFileTable -Builder $report -Title "Largest files - shipping single-file" -Rows $singleRows
Add-MarkdownFileTable -Builder $report -Title "Largest files - expanded self-contained" -Rows $expandedRows
Add-MarkdownFileTable -Builder $report -Title "Largest files - framework-dependent" -Rows $frameworkRows

[void]$report.AppendLine("## Dependency diagnostics")
[void]$report.AppendLine("")
[void]$report.AppendLine('- Full package graph: `packages.txt`')
[void]$report.AppendLine('- Direct project references: `references.txt`')
[void]$report.AppendLine('- dotnet environment: `dotnet-info.txt`')
[void]$report.AppendLine('- Publish logs: `logs/`')
[void]$report.AppendLine("")

[void]$report.AppendLine('### `dotnet nuget why`')
[void]$report.AppendLine("")
[void]$report.AppendLine("| Package | Exit code | Log |")
[void]$report.AppendLine("|---|---:|---|")

foreach ($why in $whyResults) {
    [void]$report.AppendLine(('| {0} | {1} | `{2}` |' -f $why.Package, $why.ExitCode, $why.Log))
}

[void]$report.AppendLine("")
[void]$report.AppendLine("## What to send back for analysis")
[void]$report.AppendLine("")
[void]$report.AppendLine("The most useful files are:")
[void]$report.AppendLine("")
[void]$report.AppendLine('1. `report.md`')
[void]$report.AppendLine('2. `packages.txt`')
[void]$report.AppendLine('3. the relevant files under `nuget-why/`')
[void]$report.AppendLine("")
[void]$report.AppendLine("If the whole publish-measure folder is small enough, zip the entire folder instead.")

$ReportPath = Join-Path $MeasureRoot "report.md"
$report.ToString() | Out-File $ReportPath -Encoding utf8

Write-Section "Result"

Write-Host "Shipping single-file:      $(Format-MB $singleBytes) MB"
Write-Host "Expanded self-contained:   $(Format-MB $expandedBytes) MB"
Write-Host "Framework-dependent:       $(Format-MB $frameworkBytes) MB"
Write-Host "Approx. runtime delta:      $(Format-MB $runtimeApproxBytes) MB"
Write-Host ""
Write-Host "Report:"
Write-Host "  $ReportPath"
Write-Host ""
Write-Host "Send me report.md + packages.txt, or zip the whole '$OutputRoot' folder."
