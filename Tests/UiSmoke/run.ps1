$ErrorActionPreference = 'Stop'
$launcherDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$engineProject = Join-Path $launcherDirectory 'Tests\ProcessEngine\ProcessEngine.Tests.csproj'
& dotnet build $engineProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Could not build the isolated HTTP test fixture.' }
$fixtureExecutable = Join-Path $launcherDirectory 'Tests\ProcessEngine\bin\Release\net10.0-windows\ProcessEngine.Tests.exe'
$fixtureRoot = Join-Path $launcherDirectory 'artifacts\ui-fixture'
New-Item -ItemType Directory -Force -Path (Join-Path $fixtureRoot 'api'), (Join-Path $fixtureRoot 'frontend') | Out-Null
$ports = @(1..2 | ForEach-Object {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $number = $listener.LocalEndpoint.Port
    $listener.Stop()
    $number
})
$settings = @{
    version = 1
    selectedProjectId = 'ui-fixture'
    projects = @(@{
        id = 'ui-fixture'; name = 'UI integration fixture'; rootPath = $fixtureRoot
        services = @(
            @{ id = 'fixture-api'; name = 'Fixture API'; kind = '.NET'; workingDirectory = 'api';
               startCommand = '"' + $fixtureExecutable + '" server ' + $ports[0];
               setupCommand = 'ping -t 127.0.0.1'; cleanCommand = 'echo clean';
               url = 'http://localhost:' + $ports[0]; uiPath = '/swagger' },
            @{ id = 'fixture-frontend'; name = 'Fixture Frontend'; kind = 'Custom'; workingDirectory = 'frontend';
               startCommand = '"' + $fixtureExecutable + '" server ' + $ports[1];
               setupCommand = 'echo setup'; cleanCommand = 'echo clean';
               url = 'http://localhost:' + $ports[1]; uiPath = '/' }
        )
    })
}
$settingsPath = Join-Path $fixtureRoot 'launcher.settings.json'
$settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsPath
Push-Location $launcherDirectory
try {
    & dotnet run --project (Join-Path $PSScriptRoot 'UiSmoke.csproj') -c Release -- --exercise --settings $settingsPath
    if ($LASTEXITCODE -ne 0) { throw 'UI integration checks failed.' }
}
finally { Pop-Location }
