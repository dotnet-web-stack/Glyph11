/*
 * Deterministic fuzz smoke. Mutates and truncates seed requests and runs them
 * through the parser; the win condition is "never reads out of bounds", which
 * is enforced by building this under ASan/UBSan. Reproducible (fixed LCG seed)
 * so any failure is debuggable. A coverage-guided libFuzzer/AFL target is a
 * follow-on (needs clang); this catches the obvious OOB classes with gcc.
 */
#include "glyph11.h"
#include <string.h>

static unsigned int rng = 0x9e3779b9u;
static unsigned int nxt(void) { rng = rng * 1103515245u + 12345u; return rng; }

int main(void)
{
    static const char* seeds[] = {
        "GET /api/users?a=1&b=2 HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\n\r\n",
        "POST /x HTTP/1.1\r\nHost: y\r\nTransfer-Encoding: chunked\r\n\r\n",
        "OPTIONS * HTTP/1.1\r\nHost: z\r\nAccept: */*\r\n\r\n",
        "GET /a/../b%2e%2e\\c?p=1 HTTP/1.1\r\nHost: h\r\nContent-Length: 0, 1\r\n\r\n",
    };
    enum { NSEED = (int)(sizeof seeds / sizeof seeds[0]) };
    static glyph11_field h[256], q[256];
    glyph11_request r;
    unsigned char buf[1024];

    /* 1. every prefix of every seed (exercises the incomplete paths) */
    for (int s = 0; s < NSEED; s++) {
        size_t n = strlen(seeds[s]);
        for (size_t L = 0; L <= n; L++) {
            memset(&r, 0, sizeof r);
            r.headers = h; r.header_cap = 256; r.query = q; r.query_cap = 256;
            glyph11_parse_request((const uint8_t*)seeds[s], L, NULL, &r, NULL);
        }
    }

    /* 2. mutated + randomly-truncated copies */
    for (int it = 0; it < 500000; it++) {
        int s = (int)(nxt() % (unsigned)NSEED);
        size_t n = strlen(seeds[s]);
        if (n > sizeof buf) n = sizeof buf;
        memcpy(buf, seeds[s], n);
        int flips = (int)(nxt() % 6);
        for (int k = 0; k < flips && n; k++)
            buf[nxt() % n] = (unsigned char)(nxt() >> 13);
        size_t len = nxt() % (n + 1);
        memset(&r, 0, sizeof r);
        r.headers = h; r.header_cap = 256; r.query = q; r.query_cap = 256;
        glyph11_parse_request(buf, len, NULL, &r, NULL);
    }
    return 0;
}
