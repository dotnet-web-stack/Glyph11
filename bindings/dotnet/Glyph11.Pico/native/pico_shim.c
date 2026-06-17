#include "pico_shim.h"
#include "picohttpparser.h"

/* Scratch for picohttpparser's pointer-based header array. 512 headers is well above
   any sane request; the caller's capacity is honoured for what we copy back out. */
#define PICO_SHIM_MAX_HEADERS 512

int pico_parse_request(const uint8_t* buf, size_t len,
                       pico_span* method, pico_span* target, int* minor_version,
                       pico_field* headers, size_t* num_headers)
{
    struct phr_header hdr[PICO_SHIM_MAX_HEADERS];
    const char* m;
    const char* p;
    size_t ml, pl;
    int minor;
    size_t nh = PICO_SHIM_MAX_HEADERS;

    int ret = phr_parse_request((const char*)buf, len, &m, &ml, &p, &pl, &minor,
                                hdr, &nh, 0);
    if (ret < 0) { *num_headers = 0; return ret; }   /* -1 error, -2 incomplete */

    const char* base = (const char*)buf;
    method->off = (uint32_t)(m - base); method->len = (uint32_t)ml;
    target->off = (uint32_t)(p - base); target->len = (uint32_t)pl;
    *minor_version = minor;

    size_t cap = *num_headers;
    size_t out = nh < cap ? nh : cap;
    for (size_t i = 0; i < out; i++) {
        /* obs-fold continuation lines have name == NULL; report a zero span. */
        headers[i].name.off  = hdr[i].name ? (uint32_t)(hdr[i].name - base) : 0u;
        headers[i].name.len  = (uint32_t)hdr[i].name_len;
        headers[i].value.off = hdr[i].value ? (uint32_t)(hdr[i].value - base) : 0u;
        headers[i].value.len = (uint32_t)hdr[i].value_len;
    }
    *num_headers = out;
    return ret;
}
