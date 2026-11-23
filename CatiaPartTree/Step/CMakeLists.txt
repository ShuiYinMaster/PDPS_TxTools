cmake_minimum_required(VERSION 3.21)
project(TxToolsStepReader LANGUAGES CXX)

find_package(OpenCASCADE 7.9.2 REQUIRED COMPONENTS
    FoundationClasses ModelingData ModelingAlgorithms Visualization ApplicationFramework DataExchange)

add_executable(TxTools.StepReader StepReader.cxx)
target_compile_features(TxTools.StepReader PRIVATE cxx_std_17)
target_include_directories(TxTools.StepReader SYSTEM PRIVATE "${OpenCASCADE_INCLUDE_DIR}")
target_link_libraries(TxTools.StepReader PRIVATE
    TKDESTEP TKXCAF TKLCAF TKCDF TKMesh TKTopAlgo TKBRep TKG3d TKMath TKernel)

set(STEP_RUNTIME_DIRS "" CACHE STRING "Directories of the OCCT third-party runtime DLLs")
install(TARGETS TxTools.StepReader
    RUNTIME_DEPENDENCIES
        DIRECTORIES "${OpenCASCADE_BINARY_DIR}" ${STEP_RUNTIME_DIRS}
        PRE_EXCLUDE_REGEXES "^(api|ext)-ms-win-"
        POST_EXCLUDE_REGEXES "[/\\\\][Ss][Yy][Ss][Tt][Ee][Mm]32[/\\\\]"
    RUNTIME DESTINATION .
)
install(FILES "${OpenCASCADE_INCLUDE_DIR}/../LICENSE_LGPL_21.txt"
              "${OpenCASCADE_INCLUDE_DIR}/../OCCT_LGPL_EXCEPTION.txt" DESTINATION licenses)
