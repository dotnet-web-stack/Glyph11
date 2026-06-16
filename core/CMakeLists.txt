cmake_minimum_required(VERSION 3.15)
project(glyph11 LANGUAGES C VERSION 0.1.0)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

option(GLYPH11_SANITIZE    "Build with AddressSanitizer + UBSan" OFF)
option(GLYPH11_BUILD_TESTS "Build the test suite"                ON)

if(GLYPH11_SANITIZE)
    add_compile_options(-fsanitize=address,undefined -fno-omit-frame-pointer)
    add_link_options(-fsanitize=address,undefined)
endif()

# GCC/Clang warning flags are not MSVC-compatible (cl treats -Wextra as /Wextra ->
# error D8021). Use /W4 on MSVC; no /WX there since the cross-platform packaging
# build just needs to compile (MSVC takes the scalar path — the SSE2 fast path is
# guarded by __SSE2__, which MSVC doesn't define).
if(MSVC)
    set(GLYPH11_WARNINGS /W4)
else()
    set(GLYPH11_WARNINGS -Wall -Wextra -Werror)
endif()

# Static + shared from the same source.
add_library(glyph11        STATIC src/glyph11.c)
add_library(glyph11_shared SHARED src/glyph11.c)
# OUTPUT_NAME glyph11 -> libglyph11.so / libglyph11.dylib / glyph11.dll, matching
# the [DllImport("glyph11")] name on every platform. WINDOWS_EXPORT_ALL_SYMBOLS makes
# MSVC export the public C API from the DLL (it exports nothing by default), so the
# P/Invoke binding can find the entry points without per-function __declspec annotations.
set_target_properties(glyph11_shared PROPERTIES
    OUTPUT_NAME glyph11
    WINDOWS_EXPORT_ALL_SYMBOLS ON)

foreach(t glyph11 glyph11_shared)
    target_include_directories(${t} PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
    target_compile_options(${t} PRIVATE ${GLYPH11_WARNINGS})
    set_target_properties(${t} PROPERTIES POSITION_INDEPENDENT_CODE ON)
endforeach()

# Install just the shared library into a `native/` subdir so `cmake --install` stages it
# to a predictable path on every platform (RUNTIME = glyph11.dll, LIBRARY = libglyph11.so
# /.dylib) — the package-native CI matrix installs each RID then drops it into the NuGet's
# runtimes/<rid>/native/. The import .lib (ARCHIVE) is intentionally not installed.
install(TARGETS glyph11_shared
    RUNTIME DESTINATION native
    LIBRARY DESTINATION native)

if(GLYPH11_BUILD_TESTS)
    enable_testing()
    add_executable(test_glyph11 tests/test_glyph11.c)
    target_link_libraries(test_glyph11 PRIVATE glyph11)
    target_compile_options(test_glyph11 PRIVATE ${GLYPH11_WARNINGS})
    add_test(NAME glyph11_unit COMMAND test_glyph11)

    add_executable(fuzz_smoke tests/fuzz_smoke.c)
    target_link_libraries(fuzz_smoke PRIVATE glyph11)
    target_compile_options(fuzz_smoke PRIVATE ${GLYPH11_WARNINGS})
    add_test(NAME glyph11_fuzz_smoke COMMAND fuzz_smoke)
endif()

option(GLYPH11_BUILD_BENCH "Build the pure-C micro-benchmark" OFF)
if(GLYPH11_BUILD_BENCH)
    add_executable(glyph11_bench bench/bench.c)
    target_link_libraries(glyph11_bench PRIVATE glyph11)
    target_compile_options(glyph11_bench PRIVATE ${GLYPH11_WARNINGS})
endif()
