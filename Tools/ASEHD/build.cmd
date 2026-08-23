@echo off
rem ============================================================================
rem Builds the ASEHD driver with vasm and drops the binaries into ASE/Resources,
rem where ASE.csproj embeds them. Run from this directory.
rem
rem   BOOT.S  -> ASEHD.BOT  (boot sector, must come out exactly 512 bytes)
rem   ASEHD.S -> ASEHD.SYS  (resident driver, at most 31 sectors)
rem
rem -no-opt is not optional: the installer and the bootstrap rely on the entry
rem BRA.W and the header offsets staying exactly where the sources put them.
rem ============================================================================
setlocal
pushd "%~dp0"

set VASM=vasmm68k_mot
where %VASM% >nul 2>nul
if errorlevel 1 set "VASM=C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\Tools\vasmm68k_mot.exe"

"%VASM%" -Fbin -no-opt -o ASEHD.BOT BOOT.S
if errorlevel 1 exit /b 1

"%VASM%" -Fbin -no-opt -o ASEHD.SYS ASEHD.S
if errorlevel 1 exit /b 1

for %%F in (ASEHD.BOT) do if not %%~zF==512 (
    echo ERROR: ASEHD.BOT is %%~zF bytes, not 512 - BOOT.S no longer pads itself.
    exit /b 1
)

for %%F in (ASEHD.SYS) do if %%~zF GTR 15872 (
    echo ERROR: ASEHD.SYS is %%~zF bytes and does not fit the 31 reserved sectors.
    exit /b 1
)

copy /y ASEHD.BOT ..\..\ASE\Resources\ >nul
copy /y ASEHD.SYS ..\..\ASE\Resources\ >nul

for %%F in (ASEHD.SYS) do echo ASEHD built: boot 512 bytes, driver %%~zF bytes. Copied to ASE/Resources.
popd
endlocal
