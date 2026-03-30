@echo off
setlocal

set SCRIPT_DIR=%~dp0
for %%I in ("%SCRIPT_DIR%..\..") do set REPO_ROOT=%%~fI
cd /d "%SCRIPT_DIR%"

echo Running benchmarks...
dotnet run -c Release
if %errorlevel% neq 0 (
    echo Benchmark run failed.
    exit /b %errorlevel%
)

where py >nul 2>nul
if %errorlevel% equ 0 (
    set PYTHON_CMD=py -3
) else (
    where python >nul 2>nul
    if %errorlevel% neq 0 (
        echo Python is required to import benchmark results into benchmark.db.
        exit /b 1
    )
    set PYTHON_CMD=python
)

echo Importing CSV results into benchmark.db...
%PYTHON_CMD% "%SCRIPT_DIR%ImportBenchmarkResults.py" --db "%REPO_ROOT%\benchmark.db" --run-label runbenchmark "%SCRIPT_DIR%BenchmarkDotNet.Artifacts\results"
if %errorlevel% neq 0 (
    echo Benchmark import failed.
    exit /b %errorlevel%
)

echo Done. Results imported into: %REPO_ROOT%\benchmark.db

endlocal
