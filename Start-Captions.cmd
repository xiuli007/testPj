@echo off
setlocal
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "ROOT=%~dp0"
if exist "%ROOT%artifacts\app-verified\JapaneseTranscriptionPrototype.exe" (
  start "" "%ROOT%artifacts\app-verified\JapaneseTranscriptionPrototype.exe"
  exit /b 0
)
if exist "%ROOT%artifacts\app\JapaneseTranscriptionPrototype.exe" (
  start "" "%ROOT%artifacts\app\JapaneseTranscriptionPrototype.exe"
  exit /b 0
)
if exist "%ROOT%.sdk-temp\dotnet.exe" (
  "%ROOT%.sdk-temp\dotnet.exe" run --project "%ROOT%JapaneseTranscriptionPrototype.csproj"
  if errorlevel 1 pause
  exit /b
)
echo Build output is missing. Install .NET 10 SDK and run: dotnet run --project "%ROOT%JapaneseTranscriptionPrototype.csproj"
pause
