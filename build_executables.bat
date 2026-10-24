@echo off
setlocal
rem ---------------------------------------------------------------------------
rem build_executables.bat - one-click build for nvidia_app
rem
rem   1) Compiles the whole solution (nvidia_app.sln, Release).
rem   2) Publishes the runnable executables into the git-ignored bin\ folder:
rem        bin\NvmlTest\NvmlTest.exe             (headless console CLI)
rem        bin\NvidiaMonitor\NvidiaMonitor.exe   (WPF dashboard)
rem
rem   The published executables are framework-dependent: the machine that runs
rem   them needs the .NET 8 *runtime* (not the full SDK).
rem
rem   NOTE: if the dashboard (NvidiaMonitor.exe) is currently running, this
rem   script stops with an error - the running app locks its own DLLs.
rem   Close the dashboard window first, then re-run.
rem ---------------------------------------------------------------------------
cd /d "%~dp0"

echo.
echo [1/3] Building nvidia_app.sln (Release) ...
dotnet build nvidia_app.sln -c Release --nologo
if errorlevel 1 (
    echo.
    echo BUILD FAILED - see output above.
    exit /b 1
)

tasklist /fi "imagename eq NvidiaMonitor.exe" | findstr /i "NvidiaMonitor" >nul
if not errorlevel 1 (
    echo.
    echo [!] NvidiaMonitor.exe is still running.
    echo     It locks bin\NvidiaMonitor\NvidiaMonitor.dll, so publishing would
    echo     fail. Close the dashboard window first, then re-run this script.
    exit /b 1
)

echo.
echo [2/3] Publishing NvmlTest      -> bin\NvmlTest ...
dotnet publish src\NvmlTest -c Release -o bin\NvmlTest --nologo
if errorlevel 1 (
    echo.
    echo PUBLISH NvmlTest FAILED - see output above.
    exit /b 1
)

echo.
echo [3/3] Publishing NvidiaMonitor -> bin\NvidiaMonitor ...
dotnet publish src\NvidiaMonitor -c Release -o bin\NvidiaMonitor --nologo
if errorlevel 1 (
    echo.
    echo PUBLISH NvidiaMonitor FAILED - see output above.
    exit /b 1
)

echo.
echo Done. Runnable executables:
echo   bin\NvmlTest\NvmlTest.exe
echo   bin\NvidiaMonitor\NvidiaMonitor.exe
endlocal