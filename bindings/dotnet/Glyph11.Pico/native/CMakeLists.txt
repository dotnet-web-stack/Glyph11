cmake_minimum_required(VERSION 3.15)
project(glyph11pico LANGUAGES C VERSION 0.1.0)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

# x86-64: SSE4.2 enables picohttpparser's findchar_fast fast path. -march=x86-64-v3
# (the same level glyph11's linux-x64 package uses) includes it.
option(GLYPH11_PICO_X86_AVX2 "x86-64: build -march=x86-64-v3 (enables pico's SSE4.2)" OFF)
if(GLYPH11_PICO_X86_AVX2)
    include(CheckCCompilerFlag)
    check_c_compiler_flag("-march=x86-64-v3" GLYPH11_PICO_HAS_V3)
    if(GLYPH11_PICO_HAS_V3)
        add_compile_options(-march=x86-64-v3)
    endif()
endif()

# picohttpparser is vendored third-party (MIT/Perl) — don't impose -Werror on it.
add_library(glyph11pico SHARED picohttpparser.c pico_shim.c)
set_target_properties(glyph11pico PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    WINDOWS_EXPORT_ALL_SYMBOLS ON)

install(TARGETS glyph11pico
    RUNTIME DESTINATION native
    LIBRARY DESTINATION native)
