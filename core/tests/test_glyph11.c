/*
 * Conformance tests for the Glyph11 C parser.
 *
 * These encode the same accept/reject decisions as the C# reference suite. The
 * live C-vs-C# differential/fuzz harness is a follow-on; this verifies the port
 * against known cases and runs clean under ASan/UBSan.
 */
#include "glyph11.h"
#include <stdio.h>
#include <string.h>

static int g_total = 0, g_fail = 0;

static glyph11_status run(const void* data, size_t len,
                          const glyph11_limits* L, glyph11_request* out)
{
    static glyph11_field hdrs[256], qry[256];
    memset(out, 0, sizeof *out);
    out->headers = hdrs; out->header_cap = 256;
    out->query   = qry;  out->query_cap  = 256;
    return glyph11_parse_request((const uint8_t*)data, len, L, out, NULL);
}

static void expect(const char* name, const void* data, size_t len,
                   const glyph11_limits* L, glyph11_status want)
{
    glyph11_request r;
    glyph11_status got = run(data, len, L, &r);
    g_total++;
    if (got != want) {
        g_fail++;
        printf("  FAIL %-26s got=%d (%s)  want=%d (%s)\n",
               name, got, glyph11_status_message(got),
               want, glyph11_status_message(want));
    }
}

/* string-literal helper: sizeof-1 includes embedded NULs, excludes the trailing \0 */
#define E(name, lit, want)        expect(name, lit, sizeof(lit) - 1, NULL, want)
#define EL(name, lit, L, want)    expect(name, lit, sizeof(lit) - 1, L,    want)

static void test_valid_spans(void)
{
    const char* in = "GET /api/users?a=1&b=2 HTTP/1.1\r\nHost: example.com\r\nAccept: */*\r\n\r\n";
    size_t len = strlen(in);
    static glyph11_field hdrs[16], qry[16];
    glyph11_request r;
    memset(&r, 0, sizeof r);
    r.headers = hdrs; r.header_cap = 16;
    r.query   = qry;  r.query_cap  = 16;
    size_t consumed = 0;
    glyph11_status s = glyph11_parse_request((const uint8_t*)in, len, NULL, &r, &consumed);

    g_total++;
    int ok = (s == GLYPH11_OK)
        && r.method.len == 3  && memcmp(in + r.method.off,  "GET", 3) == 0
        && r.path.len   == 10 && memcmp(in + r.path.off,    "/api/users", 10) == 0
        && r.target.len == 18 && memcmp(in + r.target.off,  "/api/users?a=1&b=2", 18) == 0
        && r.version.len == 8 && memcmp(in + r.version.off, "HTTP/1.1", 8) == 0
        && r.query_count == 2
        && r.query[0].name.len == 1  && in[r.query[0].name.off]  == 'a'
        && r.query[0].value.len == 1 && in[r.query[0].value.off] == '1'
        && r.query[1].name.len == 1  && in[r.query[1].name.off]  == 'b'
        && r.query[1].value.len == 1 && in[r.query[1].value.off] == '2'
        && r.header_count == 2
        && r.headers[0].name.len == 4  && memcmp(in + r.headers[0].name.off,  "Host", 4) == 0
        && r.headers[0].value.len == 11 && memcmp(in + r.headers[0].value.off, "example.com", 11) == 0
        && r.headers[1].name.len == 6  && memcmp(in + r.headers[1].name.off,  "Accept", 6) == 0
        && r.headers[1].value.len == 3 && memcmp(in + r.headers[1].value.off, "*/*", 3) == 0
        && consumed == len;
    if (!ok) {
        g_fail++;
        printf("  FAIL valid-spans (status=%d %s, hc=%u qc=%u consumed=%zu)\n",
               s, glyph11_status_message(s), r.header_count, r.query_count, consumed);
    }
}

int main(void)
{
    printf("glyph11 v%u.%u.%u (abi 0x%06x)\n",
           GLYPH11_VERSION_MAJOR, GLYPH11_VERSION_MINOR, GLYPH11_VERSION_PATCH,
           glyph11_abi_version());

    /* ---- valid ---- */
    test_valid_spans();
    E("valid-minimal",   "GET / HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_OK);
    E("valid-cl",        "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\n", GLYPH11_OK);
    E("valid-cl-zero",   "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\n", GLYPH11_OK);
    E("valid-te-chunked","POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n", GLYPH11_OK);
    E("valid-options-*", "OPTIONS * HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_OK);
    E("valid-http10",    "GET / HTTP/1.0\r\nHost: x\r\n\r\n", GLYPH11_OK);
    E("valid-flagparam", "GET /?flag HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_OK);

    /* ---- incomplete ---- */
    E("incomplete",      "GET / HTTP/1.1\r\nHost: x\r\n", GLYPH11_INCOMPLETE);
    E("incomplete-empty","", GLYPH11_INCOMPLETE);

    /* ---- request line ---- */
    E("multiple-sp",     "GET /  HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_ERR_MULTIPLE_SP);
    E("bad-version",     "GET / HTTP/2.0\r\nHost: x\r\n\r\n", GLYPH11_ERR_VERSION);
    E("method-token",    "G(T / HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_ERR_METHOD_TOKEN);
    E("bare-lf-reqline", "GET /\n/ HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_ERR_BARE_LF);
    E("target-control",  "GET /\x01 HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_ERR_TARGET_CHAR);

    /* ---- headers (structural) ---- */
    E("ws-before-colon", "GET / HTTP/1.1\r\nHost : x\r\n\r\n", GLYPH11_ERR_WS_BEFORE_COLON);
    E("obs-fold",        "GET / HTTP/1.1\r\nHost: x\r\n more\r\n\r\n", GLYPH11_ERR_OBS_FOLD);
    E("empty-name",      "GET / HTTP/1.1\r\n: x\r\nHost: x\r\n\r\n", GLYPH11_ERR_EMPTY_NAME);
    E("no-colon",        "GET / HTTP/1.1\r\nHostx\r\n\r\n", GLYPH11_ERR_NO_COLON);
    E("header-name-tok", "GET / HTTP/1.1\r\nHost: x\r\nBa d: y\r\n\r\n", GLYPH11_ERR_HEADER_NAME);
    E("header-value-nul","GET / HTTP/1.1\r\nHost: x\r\nA: b\x00" "c\r\n\r\n", GLYPH11_ERR_HEADER_VALUE);
    E("bare-lf-header",  "GET / HTTP/1.1\r\nHost: x\r\nA: b\nc\r\n\r\n", GLYPH11_ERR_BARE_LF);

    /* ---- semantic: smuggling ---- */
    E("te-and-cl",       "POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n", GLYPH11_ERR_TE_AND_CL);
    E("cl-conflict",     "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\n", GLYPH11_ERR_CL_CONFLICT);
    E("cl-comma-confl",  "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5, 6\r\n\r\n", GLYPH11_ERR_CL_CONFLICT);
    E("cl-leading-zero", "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 05\r\n\r\n", GLYPH11_ERR_CL_FORMAT);
    E("cl-nondigit",     "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: abc\r\n\r\n", GLYPH11_ERR_CL_FORMAT);
    E("te-bad-value",    "POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: gzip\r\n\r\n", GLYPH11_ERR_TE_VALUE);

    /* ---- semantic: host / method / target ---- */
    E("no-host",         "GET / HTTP/1.1\r\n\r\n", GLYPH11_ERR_HOST_COUNT);
    E("two-host",        "GET / HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n", GLYPH11_ERR_HOST_COUNT);
    E("host-format",     "GET / HTTP/1.1\r\nHost: a/b\r\n\r\n", GLYPH11_ERR_HOST_FORMAT);
    E("connect",         "CONNECT x:443 HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_ERR_CONNECT);
    E("asterisk-nonopt", "GET * HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_ERR_ASTERISK_FORM);

    /* ---- semantic: path traversal ---- */
    E("dot-segment",     "GET /a/../b HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_ERR_DOT_SEGMENT);
    E("dot-trailing",    "GET /a/.. HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_ERR_DOT_SEGMENT);
    E("backslash",       "GET /a\\b HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_ERR_BACKSLASH);
    E("double-encoding", "GET /%25 HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_ERR_DOUBLE_ENCODING);
    E("encoded-null",    "GET /%00 HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_ERR_NULL_BYTE);
    E("fragment",        "GET /a#b HTTP/1.1\r\nHost: x\r\n\r\n", GLYPH11_ERR_FRAGMENT);

    /* ---- limits (custom) ---- */
    {
        glyph11_limits L; glyph11_limits_default(&L); L.max_total_header_bytes = 10;
        EL("too-large", "GET / HTTP/1.1\r\nHost: x\r\n\r\n", &L, GLYPH11_ERR_TOO_LARGE);
    }
    {
        glyph11_limits L; glyph11_limits_default(&L); L.max_method_len = 2;
        EL("method-length", "GET / HTTP/1.1\r\nHost: x\r\n\r\n", &L, GLYPH11_ERR_METHOD_LENGTH);
    }
    {
        glyph11_limits L; glyph11_limits_default(&L); L.max_url_len = 3;
        EL("url-too-long", "GET /toolong HTTP/1.1\r\nHost: x\r\n\r\n", &L, GLYPH11_ERR_URL_TOO_LONG);
    }
    {
        glyph11_limits L; glyph11_limits_default(&L); L.max_header_name_len = 3;
        EL("name-too-long", "GET / HTTP/1.1\r\nHost: x\r\n\r\n", &L, GLYPH11_ERR_HEADER_NAME_TOO_LONG);
    }
    {
        glyph11_limits L; glyph11_limits_default(&L); L.max_header_value_len = 2;
        EL("value-too-long", "GET / HTTP/1.1\r\nHost: example\r\n\r\n", &L, GLYPH11_ERR_HEADER_VALUE_TOO_LONG);
    }
    {
        glyph11_limits L; glyph11_limits_default(&L); L.max_header_count = 1;
        EL("too-many-headers", "GET / HTTP/1.1\r\nHost: x\r\nA: b\r\n\r\n", &L, GLYPH11_ERR_TOO_MANY_HEADERS);
    }
    {
        glyph11_limits L; glyph11_limits_default(&L); L.max_query_param_count = 1;
        EL("too-many-query", "GET /?a=1&b=2 HTTP/1.1\r\nHost: x\r\n\r\n", &L, GLYPH11_ERR_TOO_MANY_QUERY_PARAMS);
    }

    printf("%d/%d passed%s\n", g_total - g_fail, g_total, g_fail ? "  *** FAILURES ***" : "");
    return g_fail ? 1 : 0;
}
