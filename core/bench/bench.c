/*
 * Pure-C micro-benchmark for the Glyph11 parser — no FFI, the native floor.
 * Reads the shared payload files (small.bin / h4k.bin / h32k.bin) and prints
 *   pure-c,<payload>,<ns_per_op>
 * one line per payload, for the cross-language aggregator.
 */
#include "glyph11.h"
#include <stdio.h>
#include <stdlib.h>
#include <time.h>

static unsigned char* read_file(const char* path, size_t* out_len)
{
    FILE* f = fopen(path, "rb");
    if (!f) { fprintf(stderr, "bench: cannot open %s\n", path); exit(2); }
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    unsigned char* b = (unsigned char*)malloc((size_t)n);
    if (!b || fread(b, 1, (size_t)n, f) != (size_t)n) { fprintf(stderr, "bench: read %s\n", path); exit(2); }
    fclose(f);
    *out_len = (size_t)n;
    return b;
}

static double bench(const unsigned char* buf, size_t len, const glyph11_limits* lim, long iters)
{
    static glyph11_field h[256], q[256];
    glyph11_request r;
    for (long i = 0; i < iters / 10 + 1; i++) {  /* warmup */
        r.headers = h; r.header_cap = 256; r.query = q; r.query_cap = 256;
        glyph11_parse_request(buf, len, lim, &r, NULL);
    }
    struct timespec t0, t1;
    clock_gettime(CLOCK_MONOTONIC, &t0);
    for (long i = 0; i < iters; i++) {
        r.headers = h; r.header_cap = 256; r.query = q; r.query_cap = 256;
        glyph11_parse_request(buf, len, lim, &r, NULL);
    }
    clock_gettime(CLOCK_MONOTONIC, &t1);
    double ns = (double)(t1.tv_sec - t0.tv_sec) * 1e9 + (double)(t1.tv_nsec - t0.tv_nsec);
    return ns / (double)iters;
}

int main(int argc, char** argv)
{
    const char* dir = argc > 1 ? argv[1] : ".";
    glyph11_limits lim;
    glyph11_limits_default(&lim);
    lim.max_header_count = 200;
    lim.max_total_header_bytes = 64 * 1024;

    struct { const char* name; const char* file; long iters; } cases[] = {
        { "small", "small.bin", 2000000 },
        { "4k",    "h4k.bin",    500000 },
        { "32k",   "h32k.bin",   100000 },
    };
    char path[1024];
    for (int i = 0; i < 3; i++) {
        size_t len;
        snprintf(path, sizeof path, "%s/%s", dir, cases[i].file);
        unsigned char* b = read_file(path, &len);
        double ns = bench(b, len, &lim, cases[i].iters);
        printf("pure-c,%s,%.1f\n", cases[i].name, ns);
        free(b);
    }
    return 0;
}
