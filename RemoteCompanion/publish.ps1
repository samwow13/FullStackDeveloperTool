[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'publish\single-file'),
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$sourceFolder = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$destination = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $sourceFolder $OutputDirectory))
}
if (-not $destination.StartsWith($sourceFolder + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Companion publish output must be inside the companion source folder.'
}
if (Test-Path -LiteralPath $destination) {
    $existingItems = @(Get-ChildItem -LiteralPath $destination -Force)
    if (@($existingItems | Where-Object { $_.PSIsContainer -or $_.Name -ne 'FullStackRemoteCompanion.exe' }).Count -gt 0) {
        throw 'Companion publish folder contains unrelated files. Choose a fresh output folder.'
    }
}
& dotnet publish (Join-Path $PSScriptRoot 'RemoteCompanion.csproj') -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $destination
if ($LASTEXITCODE -ne 0) { throw "Companion publish failed with exit code $LASTEXITCODE." }
$executable = Join-Path $destination 'FullStackRemoteCompanion.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Published companion executable is missing.' }
$publishedItems = @(Get-ChildItem -LiteralPath $destination -Force)
if ($publishedItems.Count -ne 1 -or $publishedItems[0].PSIsContainer -or $publishedItems[0].Name -ne 'FullStackRemoteCompanion.exe') {
    throw 'Companion publish did not produce exactly one companion EXE.'
}
Write-Output $executable
