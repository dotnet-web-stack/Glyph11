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

set(GLYPH11_WARNINGS -Wall -Wextra -Werror)

# Static + shared from the same source.
add_library(glyph11        STATIC src/glyph11.c)
add_library(glyph11_shared SHARED src/glyph11.c)
set_target_properties(glyph11_shared PROPERTIES OUTPUT_NAME glyph11)

foreach(t glyph11 glyph11_shared)
    target_include_directories(${t} PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
    target_compile_options(${t} PRIVATE ${GLYPH11_WARNINGS})
    set_target_properties(${t} PROPERTIES POSITION_INDEPENDENT_CODE ON)
endforeach()

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
