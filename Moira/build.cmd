@echo off
rem  Uso:
rem    build.cmd            compila para el equipo actual (RID autodetectado -> win-x64)
rem    build.cmd ARM64      cross-compila para win-arm64 (pasa -A a CMake)
rem  La carpeta de destino la resuelve CMakeLists.txt (native\<rid>\).
rmdir /s /q build 2>nul
set "CMAKE_ARGS="
if not "%~1"=="" set "CMAKE_ARGS=-A %~1"
cmake -S . -B build %CMAKE_ARGS%
cmake --build build --config Release
