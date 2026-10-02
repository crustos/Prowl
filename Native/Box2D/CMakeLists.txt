cmake_minimum_required(VERSION 3.22)
project(prowl_box2d C)

# Builds Box2D-Packed statically into ONE shared library, `prowl_box2d`, which exports only the pb2_* shim
# (see prowl_box2d.h). Prowl's C# side P/Invokes that library.
#
# Box2D is NOT part of this repository. Clone it beside Prowl:
#
#   parent/
#     Prowl/     this repository
#     box2d/     https://github.com/crustos/box2d
#
# `python3 build.py deps` does the cloning. To use a checkout somewhere else, pass -DPROWL_BOX2D_DIR=/path/to/box2d.
#
#   cmake -S Native/Box2D -B build/box2d -DCMAKE_BUILD_TYPE=Release [-DPROWL_B2_AVX2=ON]
#   cmake --build build/box2d

option(PROWL_B2_AVX2 "Compile Box2D with AVX2 (faster; requires an AVX2 CPU. Default is SSE2/NEON/portable)" OFF)

set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(C_VISIBILITY_PRESET hidden)

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(BOX2D_SAMPLES OFF CACHE BOOL "" FORCE)
set(BOX2D_UNIT_TESTS OFF CACHE BOOL "" FORCE)
set(BOX2D_BENCHMARKS OFF CACHE BOOL "" FORCE)
set(BOX2D_DOCS OFF CACHE BOOL "" FORCE)
set(BOX2D_VALIDATE OFF CACHE BOOL "" FORCE)
set(BOX2D_AVX2 ${PROWL_B2_AVX2} CACHE BOOL "" FORCE)

set(PROWL_BOX2D_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../../box2d" CACHE PATH "Box2D-Packed checkout (default: ../box2d beside the Prowl repository)")
get_filename_component(PROWL_BOX2D_DIR "${PROWL_BOX2D_DIR}" ABSOLUTE)
if(NOT EXISTS "${PROWL_BOX2D_DIR}/CMakeLists.txt")
  message(FATAL_ERROR
    "Box2D-Packed not found at ${PROWL_BOX2D_DIR}\n"
    "Clone it beside Prowl:   git clone https://github.com/crustos/box2d ${PROWL_BOX2D_DIR}\n"
    "(or run `python3 build.py deps`), or pass -DPROWL_BOX2D_DIR=/path/to/box2d")
endif()
add_subdirectory(${PROWL_BOX2D_DIR} ${CMAKE_BINARY_DIR}/box2d EXCLUDE_FROM_ALL)

add_library(prowl_box2d SHARED prowl_box2d.c)
target_link_libraries(prowl_box2d PRIVATE box2d)
target_include_directories(prowl_box2d PRIVATE ${PROWL_BOX2D_DIR}/include)
if(NOT MSVC)
  target_link_libraries(prowl_box2d PRIVATE m)
  target_compile_options(prowl_box2d PRIVATE -Wall -Wextra -ffp-contract=off)
endif()
set_target_properties(prowl_box2d PROPERTIES C_VISIBILITY_PRESET hidden)
