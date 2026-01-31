
cmake_minimum_required(VERSION 3.15)

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
endif()

if(MINGW)
    target_compile_options(moira PUBLIC -Wa,-mbig-obj)
endif()

set_target_properties(moira PROPERTIES OUTPUT_NAME "moira")
set_target_properties(moira PROPERTIES PREFIX "")

set(MOIRA_OUT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../ASE/native" CACHE PATH "Output dir for native library")

file(MAKE_DIRECTORY "${MOIRA_OUT_DIR}")

set_target_properties(moira PROPERTIES
    # --- 1. Propiedades GENÉRICAS (Importante para Makefiles/Ninja sin Build Type) ---
    ARCHIVE_OUTPUT_DIRECTORY "${MOIRA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${MOIRA_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${MOIRA_OUT_DIR}"

    # --- 2. Propiedades por CONFIGURACIÓN (Importante para Xcode/Multi-config) ---
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

