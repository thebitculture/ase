
cmake_minimum_required(VERSION 3.15)

cmake_policy(SET CMP0091 NEW)
project(moira CXX)

add_library(moira SHARED
 Moira.cpp
 MoiraDebugger.cpp
 Moira_dotnet.cpp
)

target_compile_features(moira PUBLIC cxx_std_20)

set_target_properties(moira PROPERTIES
  CXX_STANDARD 20
  CXX_STANDARD_REQUIRED YES
  CXX_EXTENSIONS NO
)

if (NOT MSVC)
    target_compile_options(moira PUBLIC -Wno-unused-parameter)
    target_compile_options(moira PUBLIC -Wno-unused-but-set-parameter)
    target_compile_options(moira PUBLIC -Wno-unused-but-set-variable)
    target_compile_options(moira PUBLIC -Wno-missing-field-initializers)
endif()

if(MSVC)
    target_compile_options(moira PUBLIC /W4 /bigobj) # /WX disabled for now
    target_compile_options(moira PUBLIC /wd4100 /wd4201 /wd4324 /wd4458 /wd4127)

    # /MT (/MTd for Debug): no VCRUNTIME140*/MSVCP140 imports
    set_property(TARGET moira PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

if(MINGW)
    target_compile_options(moira PUBLIC -Wa,-mbig-obj)
    target_link_options(moira PRIVATE -static)
endif()

set_target_properties(moira PROPERTIES OUTPUT_NAME "moira")
set_target_properties(moira PROPERTIES PREFIX "")

# --- Resolve the .NET RID (<os>-<arch>) this build targets, so the library
#     lands in native/<rid>/ to match ASE.csproj's per-RID layout.
#     Override with -DMOIRA_RID=<os>-<arch> (e.g. cross-compiling). ---
if(NOT DEFINED MOIRA_RID OR MOIRA_RID STREQUAL "")
    if(WIN32)
        set(_moira_os "win")
    elseif(APPLE)
        set(_moira_os "osx")
    elseif(UNIX)
        set(_moira_os "linux")
    else()
        message(FATAL_ERROR "Moira: unsupported target OS; pass -DMOIRA_RID=<os>-<arch>")
    endif()

    # Prefer explicit cross-compile hints over the host processor.
    if(APPLE AND CMAKE_OSX_ARCHITECTURES)
        set(_moira_arch_raw "${CMAKE_OSX_ARCHITECTURES}")
    elseif(MSVC AND CMAKE_GENERATOR_PLATFORM)
        set(_moira_arch_raw "${CMAKE_GENERATOR_PLATFORM}")
    else()
        set(_moira_arch_raw "${CMAKE_SYSTEM_PROCESSOR}")
    endif()
    string(TOLOWER "${_moira_arch_raw}" _moira_arch_raw)

    if(_moira_arch_raw MATCHES ";")
        message(FATAL_ERROR "Moira: universal/multi-arch build ('${_moira_arch_raw}') has no single RID folder; build one arch at a time or pass -DMOIRA_RID=<os>-<arch>")
    elseif(_moira_arch_raw MATCHES "arm64|aarch64")
        set(_moira_arch "arm64")
    elseif(_moira_arch_raw MATCHES "x64|x86_64|amd64")
        set(_moira_arch "x64")
    else()
        message(FATAL_ERROR "Moira: cannot map architecture '${_moira_arch_raw}' to a RID; pass -DMOIRA_RID=<os>-<arch>")
    endif()

    set(MOIRA_RID "${_moira_os}-${_moira_arch}")
endif()

set(MOIRA_OUT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../ASE/native/${MOIRA_RID}" CACHE PATH "Output dir for native library")
message(STATUS "Moira: target RID = ${MOIRA_RID}  ->  ${MOIRA_OUT_DIR}")

file(MAKE_DIRECTORY "${MOIRA_OUT_DIR}")

set_target_properties(moira PROPERTIES
    ARCHIVE_OUTPUT_DIRECTORY "${MOIRA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${MOIRA_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${MOIRA_OUT_DIR}"

    ARCHIVE_OUTPUT_DIRECTORY_DEBUG "${MOIRA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_DEBUG "${MOIRA_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${MOIRA_OUT_DIR}"
    
    ARCHIVE_OUTPUT_DIRECTORY_RELEASE "${MOIRA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${MOIRA_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${MOIRA_OUT_DIR}"
    
    ARCHIVE_OUTPUT_DIRECTORY_RELWITHDEBINFO "${MOIRA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELWITHDEBINFO "${MOIRA_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO "${MOIRA_OUT_DIR}"
    
    ARCHIVE_OUTPUT_DIRECTORY_MINSIZEREL "${MOIRA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_MINSIZEREL "${MOIRA_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_MINSIZEREL "${MOIRA_OUT_DIR}"
)

