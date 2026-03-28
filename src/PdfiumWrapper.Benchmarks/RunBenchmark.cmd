@echo off
setlocal

set SCRIPT_DIR=%~dp0
cd /d "%SCRIPT_DIR%"

echo Running benchmarks...
dotnet run -c Release
if %errorlevel% neq 0 (
    echo Benchmark run failed.
    exit /b %errorlevel%
)

echo Moving CSV results...
for %%f in ("%SCRIPT_DIR%BenchmarkDotNet.Artifacts\results\*.csv") do (
    move "%%f" "%SCRIPT_DIR%"
)

echo Done. CSV files are in: %SCRIPT_DIR%
dir /b "%SCRIPT_DIR%*.csv" 2>nul

endlocal
