@echo off
setlocal
pushd "%~dp0"
dotnet build "FullStackLauncher.csproj" --configuration Release --nologo
if errorlevel 1 (
    echo.
    echo The launcher could not be built. Install the .NET 10 SDK or newer and check the errors above.
    pause
    popd
    exit /b 1
)
start "" "%~dp0bin\Release\net10.0-windows\FullStackLauncher.exe" %*
popd
endlocal
