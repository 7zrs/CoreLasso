@echo off
setlocal
rem Builds CoreLasso with the C# compiler that ships with Windows itself.
rem No .NET SDK, no NuGet, no internet connection required.

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo ERROR: %CSC% not found.
  echo The .NET Framework 4.x compiler is missing from this Windows install.
  exit /b 1
)

cd /d "%~dp0"
if not exist bin mkdir bin

"%CSC%" -nologo -target:winexe -platform:x64 -optimize+ -warn:3 ^
  -out:bin\CoreLasso.exe ^
  -win32manifest:src\app.manifest ^
  -reference:System.dll ^
  -reference:System.Core.dll ^
  -reference:System.Drawing.dll ^
  -reference:System.Windows.Forms.dll ^
  src\Native.cs src\Topology.cs src\Safety.cs src\ProcessOps.cs ^
  src\Rules.cs src\Watcher.cs src\MainForm.cs src\Program.cs

if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)

echo.
echo Built bin\CoreLasso.exe
echo Running self-test...
echo.
bin\CoreLasso.exe --selftest
