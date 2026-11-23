@echo off
rem AgOpen dev tablet: build the LABELS version of AgOpenGPS (branch agopen-labels)
rem in its own folder, Dev\AgOpenGPS-labels, and put a shortcut
rem "AgOpenGPS (labels)" on the desktop. The normal test version (Dev\AgOpenGPS,
rem built by check-and-build.bat) is not touched.
rem Both versions use the same fields and settings (Documents\AgOpenGPS);
rem only one of them can run at a time.
rem Results are written to labels-check-log.txt and labels-build-log.txt in the
rem Dev folder. Safe to run again any time: it gets the newest labels version.
setlocal

rem The Dev folder: where check-and-build.bat is (this file's folder, or two up
rem when it's run from Dev\AgOpenGPS\tablet-scripts).
for %%d in ("%~dp0.") do set "DEV=%%~fd"
if not exist "%DEV%\check-and-build.bat" for %%d in ("%~dp0..\..") do set "DEV=%%~fd"
set "LOG=%DEV%\labels-check-log.txt"
set "CODE=%DEV%\AgOpenGPS-labels"
set "EXE=%CODE%\SourceCode\GPS\bin\Debug\win-x64\AgOpenGPS.exe"
cd /d "%DEV%"

echo Building the labels version of AgOpenGPS. This can take a few minutes...
echo AgOpen labels version  %date% %time% > "%LOG%"
echo Dev folder: %DEV%>> "%LOG%"

rem The labels version can't be rebuilt while it's running.
powershell -NoProfile -Command "if (Get-Process AgOpenGPS -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\AgOpenGPS-labels\*' }) { exit 1 }"
if errorlevel 1 (
  echo The labels version of AgOpenGPS is running: close it and run this again.>> "%LOG%"
  echo.
  echo The labels version of AgOpenGPS is running. Close it, then run this again.
  goto done
)

echo.>> "%LOG%"
echo == Code (branch agopen-labels) ==>> "%LOG%"
if not exist "%CODE%\.git" (
  git clone --depth 50 --branch agopen-labels https://github.com/cocamousse/AgOpenGPS.git "%CODE%" >> "%LOG%" 2>&1
) else (
  rem This folder is only for building: always take the newest labels version on GitHub.
  git -C "%CODE%" fetch --depth 50 origin agopen-labels >> "%LOG%" 2>&1
  git -C "%CODE%" reset --hard FETCH_HEAD >> "%LOG%" 2>&1
)
git -C "%CODE%" log -1 --format="%%h %%s" >> "%LOG%" 2>&1

echo.>> "%LOG%"
echo == Build (Debug) ==>> "%LOG%"
set "MSBUILD="
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
if "%MSBUILD%"=="" (
  echo No MSBuild found, so no build.>> "%LOG%"
  goto done
)
"%MSBUILD%" "%CODE%\SourceCode\AgOpenGPS.sln" -restore -p:Configuration=Debug -m -nologo -v:minimal "-flp:logfile=%DEV%\labels-build-log.txt;verbosity=normal" >> "%LOG%" 2>&1
set "BUILT=%errorlevel%"
echo Build exit code: %BUILT%>> "%LOG%"
if not "%BUILT%"=="0" goto done
if not exist "%EXE%" (
  echo AgOpenGPS.exe wasn't found after the build: %EXE%>> "%LOG%"
  goto done
)

echo.>> "%LOG%"
echo == Desktop shortcut ==>> "%LOG%"
powershell -NoProfile -Command "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop') + '\AgOpenGPS (labels).lnk'); $s.TargetPath = $env:EXE; $s.WorkingDirectory = Split-Path $env:EXE; $s.IconLocation = $env:EXE + ',0'; $s.Description = 'AgOpenGPS with button labels (test version)'; $s.Save(); 'Shortcut: ' + $s.FullName" >> "%LOG%" 2>&1

:done
echo Finished %date% %time%>> "%LOG%"
echo.
echo Done. Results are in labels-check-log.txt in the Dev folder. This window closes by itself.
timeout /t 8 >nul
