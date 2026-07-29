#!/bin/bash
set -e
rm -rf build
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release

# Core count, portably: getconf is available on both Linux and macOS, unlike the BSD-only
# `sysctl -n hw.ncpu` this used to call, which made the script abort on Linux before building
# anything. Falls back to a single job if the query is unavailable.
njobs=$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 1)
cmake --build build --config Release -j "$njobs"
