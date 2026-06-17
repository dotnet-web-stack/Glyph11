/*
 * Thin offset-based wrapper over picohttpparser's phr_parse_request, so the .NET
 * binding marshals plain (offset,len) spans instead of raw pointers. The heavy
 * lifting is picohttpparser's; this only converts its returned pointers to offsets
 * relative to the input buffer.
 */
#ifndef GLYPH11_PICO_SHIM_H
#define GLYPH11_PICO_SHIM_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct { uint32_t off; uint32_t len; } pico_span;
typedef struct { pico_span name; pico_span value; } pico_field;

/*
 * Parse one HTTP/1.x request header block. All offsets are relative to `buf`.
 *   method  - out: request method span
 *   target  - out: full request-target span (path + query; NOT split)
 *   minor_version - out: HTTP minor version (0 or 1; major is 1)
 *   headers - out: header name/value spans; caller-provided storage
 *   num_headers - in: capacity of headers[]; out: number written
 * Returns: >= 0 header-block length in bytes (consumed); -1 parse error; -2 incomplete.
 */
int pico_parse_request(const uint8_t* buf, size_t len,
                       pico_span* method, pico_span* target, int* minor_version,
                       pico_field* headers, size_t* num_headers);

#ifdef __cplusplus
}
#endif

#endif /* GLYPH11_PICO_SHIM_H */
