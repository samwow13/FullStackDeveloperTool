$ErrorActionPreference = 'Stop'
$launcherDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixtureParent = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) 'FullStackLauncher.PublishChecks'))
$fixtureName = [Guid]::NewGuid().ToString('N')
$fixtureDirectory = Join-Path $fixtureParent $fixtureName
$mockPublishCalls = [System.Collections.Generic.List[string]]::new()

function Assert-Equal($Expected, $Actual, [string]$Label) {
    if ($Expected -cne $Actual) { throw "$Label`: expected '$Expected', got '$Actual'." }
}

# Exercise the real publish script while replacing only its SDK command. The mock
# copies settings just as dotnet publish does, so retention is checked end to end.
function dotnet {
    if ($args[0] -ne 'publish') { throw 'This fixture only supports the publish command.' }
    $outputIndex = [Array]::IndexOf($args, '--output')
    if ($outputIndex -lt 0) { throw 'The publish output argument is missing.' }
    $outputFolder = [string]$args[$outputIndex + 1]
    $sourceFolder = Split-Path -Parent ([string]$args[1])
    New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceFolder 'launcher.settings.json') -Destination (Join-Path $outputFolder 'launcher.settings.json') -Force
    $mockPublishCalls.Add($outputFolder)
    $global:LASTEXITCODE = 0
}

try {
    New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $launcherDirectory 'publish.ps1') -Destination (Join-Path $fixtureDirectory 'publish.ps1')
    Set-Content -LiteralPath (Join-Path $fixtureDirectory 'README.md') -Value 'Isolated publishing fixture'
    $settingsPath = Join-Path $fixtureDirectory 'launcher.settings.json'
    $absoluteTarget = Join-Path $fixtureDirectory 'Installed Apps\Absolute.exe'
    $settings = [ordered]@{
        version = 1
        projects = @(@{ rootPath = '..\Project A' })
        developerTools = @(
            @{ id = 'relative-app'; kind = 'Application'; target = 'tools\Client App.exe' },
            @{ id = 'relative-pg'; kind = 'PgAdmin'; target = 'apps\pgAdmin4.exe' },
            @{ id = 'absolute-app'; kind = 'Application'; target = $absoluteTarget },
            @{ id = 'environment-app'; kind = 'Application'; target = '%LOCALAPPDATA%\Programs\Client App.exe' },
            @{ id = 'automatic-pg'; kind = 'PgAdmin'; target = '' },
            @{ id = 'website'; kind = 'Website'; target = 'https://pgadmin.example.test/login?next=%2Fbrowser' }
        )
    }
    $settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $settingsPath
    $sourceBefore = [System.IO.File]::ReadAllText($settingsPath)
    & (Join-Path $fixtureDirectory 'publish.ps1') -FrameworkDependent
    $portableSettingsPath = Join-Path $fixtureDirectory 'portable\launcher.settings.json'
    $published = Get-Content -LiteralPath $portableSettingsPath -Raw | ConvertFrom-Json
    Assert-Equal '..\..\Project A' $published.projects[0].rootPath 'Project root relocation'
    Assert-Equal '..\tools\Client App.exe' $published.developerTools[0].target 'Application relative path with spaces'
    Assert-Equal '..\apps\pgAdmin4.exe' $published.developerTools[1].target 'pgAdmin override relative path'
    Assert-Equal $absoluteTarget $published.developerTools[2].target 'Absolute target preservation'
    Assert-Equal '%LOCALAPPDATA%\Programs\Client App.exe' $published.developerTools[3].target 'Environment target preservation'
    Assert-Equal '' $published.developerTools[4].target 'Automatic pgAdmin target preservation'
    Assert-Equal 'https://pgadmin.example.test/login?next=%2Fbrowser' $published.developerTools[5].target 'Website preservation'
    Assert-Equal $sourceBefore ([System.IO.File]::ReadAllText($settingsPath)) 'Source settings preservation'
    if ($published.developerTools[0].target.EndsWith('\')) { throw 'An executable target acquired a trailing folder separator.' }
    Write-Host 'PASS first export relocates relative tool files and retains all other target types'

    # A pre-existing portable copy wins even when the simulated SDK overwrites it.
    $existingContent = "{`r`n  `"version`": 1, `"projects`": [], `"developerTools`": []`r`n}`r`n"
    [System.IO.File]::WriteAllText($portableSettingsPath, $existingContent)
    & (Join-Path $fixtureDirectory 'publish.ps1') -FrameworkDependent
    Assert-Equal $existingContent ([System.IO.File]::ReadAllText($portableSettingsPath)) 'Existing portable settings preservation'
    Write-Host 'PASS republishing retains existing portable settings exactly'

    # Legacy source configurations have no developerTools property.
    @{ version = 1; projects = @(@{ rootPath = '..\Legacy project' }) } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $settingsPath
    & (Join-Path $fixtureDirectory 'publish.ps1') -FrameworkDependent -OutputDirectory 'legacy portable'
    $legacy = Get-Content -LiteralPath (Join-Path $fixtureDirectory 'legacy portable\launcher.settings.json') -Raw | ConvertFrom-Json
    Assert-Equal '..\..\Legacy project' $legacy.projects[0].rootPath 'Legacy root relocation'
    if ($null -ne $legacy.PSObject.Properties['developerTools']) { throw 'Legacy settings unexpectedly gained developer tools.' }
    Write-Host 'PASS legacy settings without developerTools still publish'
    Assert-Equal 3 $mockPublishCalls.Count 'Mocked publish call count'
    Write-Host 'Publish settings checks: 3/3 passed. No SDK build or publish was run.'
}
finally {
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixtureDirectory)
    $allowedPrefix = $fixtureParent.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFixture.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedFixture) -ne $fixtureName) {
        throw 'Refusing to remove a fixture outside the intended publish-test directory.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
