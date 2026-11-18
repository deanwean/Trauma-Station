@echo off
dotnet build -c Release
if errorlevel 1 (
    pause
    exit /b 1
)
dotnet run --no-build --project Content.Trauma.Server -c Release
pause