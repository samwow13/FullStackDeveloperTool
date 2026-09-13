[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'publish\single-file'),
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$projectDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
if (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectDirectory $OutputDirectory
}
$publishDirectory = [System.IO.Path]::GetFullPath($OutputDirectory).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$allowedPrefix = $projectDirectory.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $publishDirectory.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose an output folder inside FullStackLauncher, such as publish\single-file. Copy the resulting EXE anywhere afterward.'
}

# Do not clear old portable folders: they can contain a user's only saved profiles.
# An existing single-file output can be published again without any cleanup.
if (Test-Path -LiteralPath $publishDirectory) {
    if (-not (Test-Path -LiteralPath $publishDirectory -PathType Container)) {
        throw "The output path is not a folder: $publishDirectory"
    }
    $unrelatedOutput = @(Get-ChildItem -LiteralPath $publishDirectory -Force | Where-Object {
        $_.PSIsContainer -or $_.Name -ne 'FullStackLauncher.exe'
    })
    if ($unrelatedOutput.Count -gt 0) {
        throw 'The output folder contains other files. Choose a new empty output folder; existing files and settings have been preserved.'
    }
}

# Public builds never read, copy, or embed any user's saved settings.
# The app creates an empty LocalAppData library on its first launch.
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$publishArguments = @(
    'publish', (Join-Path $projectDirectory 'FullStackLauncher.csproj'),
    '--configuration', 'Release', '--runtime', $Runtime,
    '--self-contained', $selfContained,
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false',
    '--output', $publishDirectory, '--nologo'
)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Force)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].PSIsContainer -or
    $publishedFiles[0].Name -ne 'FullStackLauncher.exe') {
    throw 'Publishing did not produce exactly one FullStackLauncher.exe. Review the output before copying it.'
}

Write-Host "Single-file launcher is ready: $(Join-Path $publishDirectory 'FullStackLauncher.exe')"
Write-Host 'Personal settings and credential stores were not included. New users start with an empty library.'
Write-Host 'Copy just the EXE. Saved projects live in %LOCALAPPDATA%\FullStackLauncher\launcher.settings.json.'
Write-Host 'Codex alerts run from the same EXE through the Alerts menu or --codex-monitor.'
