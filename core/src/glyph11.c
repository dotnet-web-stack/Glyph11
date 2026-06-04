/*
 * glyph11.c — the Glyph11 hardened HTTP/1.1 request parser.
 *
 * A faithful port of the C# UltraHardenedParser: full RFC 9110/9112 structural
 * validation, configurable resource limits, and every semantic / smuggling
 * check, fused into one pass. Scalar (correctness-first); SIMD is a follow-on.
 *
 * Discipline: no allocation; length-bounded scanning only (memchr, never
 * strlen/strchr); every index checked against `len`.
 */
#include "glyph11.h"
#include <string.h>   /* memchr, memcmp */
#if defined(__SSE2__)
#  include <emmintrin.h>
#endif

/* ======================================================================== */
/*  Versioning / limits / status                                            */
/* ======================================================================== */

uint32_t glyph11_abi_version(void)
{
    return ((uint32_t)GLYPH11_VERSION_MAJOR << 16)
         | ((uint32_t)GLYPH11_VERSION_MINOR << 8)
         |  (uint32_t)GLYPH11_VERSION_PATCH;
}

void glyph11_limits_default(glyph11_limits* out)
{
    if (!out) return;
    out->struct_size            = (uint32_t)sizeof(glyph11_limits);
    out->max_header_count       = 100;
    out->max_header_name_len    = 256;
    out->max_header_value_len   = 8192;
    out->max_url_len            = 8192;
    out->max_query_param_count  = 128;
    out->max_method_len         = 16;
    out->max_total_header_bytes = 1048576u;
}

int glyph11_status_http_code(glyph11_status s)
{
    if (s == GLYPH11_OK || s == GLYPH11_INCOMPLETE) return 0;
    if ((int)s >= 300) return 431;   /* resource-limit breaches */
    return 400;                      /* structural + semantic */
}

const char* glyph11_status_message(glyph11_status s)
{
    switch (s) {
    case GLYPH11_OK:                        return "ok";
    case GLYPH11_INCOMPLETE:                return "incomplete: need more data";
    case GLYPH11_ERR_REQUEST_LINE:          return "invalid request line";
    case GLYPH11_ERR_METHOD_TOKEN:          return "method contains invalid token characters";
    case GLYPH11_ERR_METHOD_LENGTH:         return "method length exceeds limit";
    case GLYPH11_ERR_VERSION:               return "invalid HTTP version";
    case GLYPH11_ERR_TARGET_CHAR:           return "request-target contains invalid characters";
    case GLYPH11_ERR_BARE_LF:               return "bare LF; only CRLF line endings are allowed";
    case GLYPH11_ERR_OBS_FOLD:              return "obsolete line folding (obs-fold) is not allowed";
    case GLYPH11_ERR_WS_BEFORE_COLON:       return "whitespace between header name and colon";
    case GLYPH11_ERR_MULTIPLE_SP:           return "multiple spaces in request line";
    case GLYPH11_ERR_HEADER_NAME:           return "header name contains invalid token characters";
    case GLYPH11_ERR_HEADER_VALUE:          return "header value contains invalid characters";
    case GLYPH11_ERR_EMPTY_NAME:            return "header name is empty";
    case GLYPH11_ERR_NO_COLON:              return "malformed header line: missing colon";
    case GLYPH11_ERR_TE_AND_CL:             return "both Transfer-Encoding and Content-Length present";
    case GLYPH11_ERR_CL_CONFLICT:           return "conflicting Content-Length values";
    case GLYPH11_ERR_CL_FORMAT:             return "invalid Content-Length format";
    case GLYPH11_ERR_TE_VALUE:              return "invalid Transfer-Encoding value; only 'chunked' is accepted";
    case GLYPH11_ERR_HOST_COUNT:            return "request must have exactly one Host header";
    case GLYPH11_ERR_HOST_FORMAT:           return "invalid Host header: contains '@' or '/'";
    case GLYPH11_ERR_DOT_SEGMENT:           return "dot segment in request path";
    case GLYPH11_ERR_BACKSLASH:             return "backslash in request path";
    case GLYPH11_ERR_DOUBLE_ENCODING:       return "double-encoded percent (%25) in path";
    case GLYPH11_ERR_NULL_BYTE:             return "encoded null byte (%00) in path";
    case GLYPH11_ERR_OVERLONG_UTF8:         return "overlong UTF-8 sequence in path";
    case GLYPH11_ERR_FRAGMENT:              return "fragment indicator (#) in request-target";
    case GLYPH11_ERR_CONNECT:               return "CONNECT method is not allowed on origin servers";
    case GLYPH11_ERR_ASTERISK_FORM:         return "asterisk-form request-target is only valid for OPTIONS";
    case GLYPH11_ERR_TOO_LARGE:             return "total header size exceeds limit";
    case GLYPH11_ERR_URL_TOO_LONG:          return "URL length exceeds limit";
    case GLYPH11_ERR_HEADER_NAME_TOO_LONG:  return "header name length exceeds limit";
    case GLYPH11_ERR_HEADER_VALUE_TOO_LONG: return "header value length exceeds limit";
    case GLYPH11_ERR_TOO_MANY_HEADERS:      return "header count exceeds limit";
    case GLYPH11_ERR_TOO_MANY_QUERY_PARAMS: return "query parameter count exceeds limit";
    default:                                return "unknown";
    }
}

/* ======================================================================== */
/*  Character classes (RFC 9110/9112)                                        */
/* ======================================================================== */

static int is_digit(uint8_t c) { return c >= '0' && c <= '9'; }
static int is_ows(uint8_t c)   { return c == ' ' || c == '\t'; }

/* token = ALPHA / DIGIT / "!#$%&'*+-.^_`|~"  (RFC 9110 §5.6.2) */
static int is_token(uint8_t c)
{
    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
        return 1;
    switch (c) {
    case '!': case '#': case '$': case '%': case '&': case '\'': case '*':
    case '+': case '-': case '.': case '^': case '_': case '`': case '|': case '~':
        return 1;
    default:
        return 0;
    }
}

/* field-value = HTAB / SP / VCHAR(0x21-0x7E) / obs-text(0x80-0xFF)  (RFC 9110 §5.5) */
static int is_fieldvalue(uint8_t c)
{
    return c == 0x09 || c == 0x20 || (c >= 0x21 && c <= 0x7E) || c >= 0x80;
}

/* request-target chars: SP(0x20) + VCHAR(0x21-0x7E), ASCII only (RFC 9112 §3.2) */
static int is_reqtarget(uint8_t c)
{
    return c >= 0x20 && c <= 0x7E;
}

/* index of first byte failing pred, or -1 if all pass (scalar; used for the
   short token scans — method and header names) */
static long first_invalid(const uint8_t* p, size_t len, int (*pred)(uint8_t))
{
    for (size_t i = 0; i < len; i++)
        if (!pred(p[i])) return (long)i;
    return -1;
}

/* Range-class scanners for the long, hot fields (header value, request-target).
   SSE2-accelerated; scalar fallback elsewhere. (NEON is a TODO pending ARM CI;
   the scalar fallback keeps non-x86 correct, just not vectorized.) */

/* index of first byte not a valid field-value char (RFC 9110 §5.5), or -1.
   valid = HTAB(0x09) | (b >= 0x20 & b != 0x7F)  [obs-text 0x80-0xFF allowed] */
static long scan_fieldvalue(const uint8_t* p, size_t len)
{
    size_t i = 0;
#if defined(__SSE2__)
    const __m128i c09 = _mm_set1_epi8(0x09);
    const __m128i c20 = _mm_set1_epi8(0x20);
    const __m128i c7f = _mm_set1_epi8(0x7F);
    for (; i + 16 <= len; i += 16) {
        __m128i v     = _mm_loadu_si128((const __m128i*)(p + i));
        __m128i ge20  = _mm_cmpeq_epi8(_mm_max_epu8(v, c20), v);   /* b >= 0x20 */
        __m128i htab  = _mm_cmpeq_epi8(v, c09);
        __m128i del   = _mm_cmpeq_epi8(v, c7f);
        __m128i valid = _mm_or_si128(htab, _mm_andnot_si128(del, ge20));
        unsigned mask = (unsigned)_mm_movemask_epi8(valid) & 0xFFFFu;
        if (mask != 0xFFFFu)
            return (long)(i + (size_t)__builtin_ctz((~mask) & 0xFFFFu));
    }
#endif
    for (; i < len; i++) if (!is_fieldvalue(p[i])) return (long)i;
    return -1;
}

/* index of first byte not a valid request-target char (SP + VCHAR), or -1.
   valid = (b >= 0x20 & b <= 0x7E) */
static long scan_reqtarget(const uint8_t* p, size_t len)
{
    size_t i = 0;
#if defined(__SSE2__)
    const __m128i c20 = _mm_set1_epi8(0x20);
    const __m128i c7e = _mm_set1_epi8(0x7E);
    for (; i + 16 <= len; i += 16) {
        __m128i v     = _mm_loadu_si128((const __m128i*)(p + i));
        __m128i ge20  = _mm_cmpeq_epi8(_mm_max_epu8(v, c20), v);   /* b >= 0x20 */
        __m128i le7e  = _mm_cmpeq_epi8(_mm_min_epu8(v, c7e), v);   /* b <= 0x7E */
        __m128i valid = _mm_and_si128(ge20, le7e);
        unsigned mask = (unsigned)_mm_movemask_epi8(valid) & 0xFFFFu;
        if (mask != 0xFFFFu)
            return (long)(i + (size_t)__builtin_ctz((~mask) & 0xFFFFu));
    }
#endif
    for (; i < len; i++) if (!is_reqtarget(p[i])) return (long)i;
    return -1;
}

/* case-insensitive ASCII compare of a span against a lowercase literal */
static int ci_eq(const uint8_t* a, size_t alen, const char* lower, size_t llen)
{
    if (alen != llen) return 0;
    for (size_t i = 0; i < alen; i++)
        if ((uint8_t)(a[i] | 0x20) != (uint8_t)lower[i]) return 0;
    return 1;
}

/* HTTP-version = "HTTP/" DIGIT "." DIGIT; accept only 1.0 / 1.1 (exactly 8 bytes) */
static int is_http_version(const uint8_t* p, size_t len)
{
    return len == 8
        && p[0]=='H' && p[1]=='T' && p[2]=='T' && p[3]=='P' && p[4]=='/'
        && p[5]=='1' && p[6]=='.' && (p[7]=='0' || p[7]=='1');
}

/* ======================================================================== */
/*  Delimiter search (length-bounded)                                        */
/* ======================================================================== */

/* index of the '\r' of the first CRLF in p[0..len), else -1 */
static long find_crlf(const uint8_t* p, size_t len)
{
    size_t i = 0;
    while (i < len) {
        const uint8_t* cr = (const uint8_t*)memchr(p + i, '\r', len - i);
        if (!cr) return -1;
        size_t idx = (size_t)(cr - p);
        if (idx + 1 >= len) return -1;       /* CR at end, no LF can follow */
        if (p[idx + 1] == '\n') return (long)idx;
        i = idx + 1;
    }
    return -1;
}

/* index of the first '\r' of CRLFCRLF in p[0..len), else -1 */
static long find_crlfcrlf(const uint8_t* p, size_t len)
{
    if (len < 4) return -1;
    size_t i = 0, limit = len - 3;           /* idx must satisfy idx+3 < len */
    while (i < limit) {
        const uint8_t* cr = (const uint8_t*)memchr(p + i, '\r', limit - i);
        if (!cr) return -1;
        size_t idx = (size_t)(cr - p);
        if (p[idx+1]=='\n' && p[idx+2]=='\r' && p[idx+3]=='\n') return (long)idx;
        i = idx + 1;
    }
    return -1;
}

/* ======================================================================== */
/*  Semantic helpers (ported from UltraHardenedParser)                       */
/* ======================================================================== */

/* trim leading + trailing OWS; reports the trimmed sub-range via out params */
static void trim_ows(const uint8_t* v, size_t len, size_t* out_off, size_t* out_len)
{
    size_t s = 0, e = len;
    while (s < e && is_ows(v[s]))   s++;
    while (e > s && is_ows(v[e-1])) e--;
    *out_off = s; *out_len = e - s;
}

/* Content-Length value syntax (RFC 9110 §8.6): 1*DIGIT, optional comma-separated
   duplicates. Rejects empty, non-digit, leading zeros, > uint64 max, trailing comma. */
static int cl_value_valid(const uint8_t* v, size_t len)
{
    static const char ULMAX[20] = {'1','8','4','4','6','7','4','4','0','7',
                                   '3','7','0','9','5','5','1','6','1','5'};
    const int MAXD = 20;
    if (len == 0) return 0;
    size_t pos = 0;
    while (pos < len) {
        while (pos < len && is_ows(v[pos])) pos++;
        if (pos >= len) return 0;
        if (!is_digit(v[pos])) return 0;
        size_t dstart = pos;
        if (v[pos] == '0') {
            pos++;
            if (pos < len && is_digit(v[pos])) return 0;   /* leading zero */
        } else {
            pos++;
            while (pos < len && is_digit(v[pos])) pos++;
        }
        size_t dcount = pos - dstart;
        if (dcount > (size_t)MAXD) return 0;
        if (dcount == (size_t)MAXD) {
            for (int j = 0; j < MAXD; j++) {
                if (v[dstart + (size_t)j] > (uint8_t)ULMAX[j]) return 0;
                if (v[dstart + (size_t)j] < (uint8_t)ULMAX[j]) break;
            }
        }
        while (pos < len && is_ows(v[pos])) pos++;
        if (pos >= len) return 1;
        if (v[pos] != ',') return 0;
        pos++;
    }
    return 0;   /* trailing comma with nothing after */
}

/* true if one CL value has comma-separated segments that are not all identical */
static int cl_comma_conflict(const uint8_t* v, size_t len)
{
    if (memchr(v, ',', len) == NULL) return 0;
    const uint8_t* fseg = NULL; size_t fseglen = 0;
    size_t start = 0;
    while (start < len) {
        while (start < len && is_ows(v[start])) start++;
        size_t end = start;
        while (end < len && v[end] != ',') end++;
        size_t te = end;
        while (te > start && is_ows(v[te-1])) te--;
        const uint8_t* seg = v + start; size_t seglen = te - start;
        if (fseg == NULL) { fseg = seg; fseglen = seglen; }
        else if (!(seglen == fseglen && memcmp(seg, fseg, seglen) == 0)) return 1;
        start = end + 1;
    }
    return 0;
}

/* single-pass path semantics: fragment, backslash, %25, %00, dot segments,
   overlong UTF-8. Returns GLYPH11_OK if clean, else the specific error. */
static glyph11_status validate_path(const uint8_t* p, size_t len)
{
    for (size_t i = 0; i < len; i++) {
        uint8_t b = p[i];
        if (b == '#')  return GLYPH11_ERR_FRAGMENT;
        if (b == '\\') return GLYPH11_ERR_BACKSLASH;
        if (b == '%' && i + 2 < len) {
            uint8_t h1 = p[i+1], h2 = p[i+2];
            if (h1 == '2' && h2 == '5') return GLYPH11_ERR_DOUBLE_ENCODING;
            if (h1 == '0' && h2 == '0') return GLYPH11_ERR_NULL_BYTE;
        }
        if (b == '/') {
            size_t rem = len - i - 1;
            if (rem >= 1 && p[i+1] == '.') {
                if (rem == 1 || p[i+2] == '/') return GLYPH11_ERR_DOT_SEGMENT;
                if (p[i+2] == '.' && (rem == 2 || p[i+3] == '/')) return GLYPH11_ERR_DOT_SEGMENT;
            }
        }
        /* Overlong UTF-8 (RFC 3629). Mostly unreachable — is_reqtarget already
           rejects bytes > 0x7E — but ported for completeness. */
        if (b == 0xC0 || b == 0xC1) return GLYPH11_ERR_OVERLONG_UTF8;
        if (b == 0xE0 && i+1 < len && p[i+1] < 0xA0) return GLYPH11_ERR_OVERLONG_UTF8;
        if (b == 0xF0 && i+1 < len && p[i+1] < 0x90) return GLYPH11_ERR_OVERLONG_UTF8;
    }
    return GLYPH11_OK;
}

/* ======================================================================== */
/*  Parse                                                                    */
/* ======================================================================== */

glyph11_status glyph11_parse_request(
    const uint8_t* buf, size_t len,
    const glyph11_limits* limits,
    glyph11_request* req, size_t* consumed)
{
    glyph11_limits L;
    if (limits) L = *limits; else glyph11_limits_default(&L);

    if (consumed) *consumed = 0;
    if (req) {
        req->method  = (glyph11_span){0, 0};
        req->target  = (glyph11_span){0, 0};
        req->path    = (glyph11_span){0, 0};
        req->version = (glyph11_span){0, 0};
        req->header_count = 0;
        req->query_count  = 0;
    }
    if (!buf || !req || !req->headers) return GLYPH11_INCOMPLETE;

    long he = find_crlfcrlf(buf, len);
    if (he < 0) return GLYPH11_INCOMPLETE;
    size_t header_end = (size_t)he;
    size_t total = header_end + 4;
    if (total > L.max_total_header_bytes) return GLYPH11_ERR_TOO_LARGE;

    /* ---- request line: buf[0 .. rl_end) ---- */
    long rl = find_crlf(buf, len);
    if (rl < 0) return GLYPH11_ERR_REQUEST_LINE;
    size_t rl_end = (size_t)rl;

    if (memchr(buf, '\n', rl_end) != NULL) return GLYPH11_ERR_BARE_LF;

    const uint8_t* sp1 = (const uint8_t*)memchr(buf, ' ', rl_end);
    if (!sp1) return GLYPH11_ERR_REQUEST_LINE;
    size_t first_space = (size_t)(sp1 - buf);

    const uint8_t* sp2 = (const uint8_t*)memchr(buf + first_space + 1, ' ',
                                                rl_end - (first_space + 1));
    if (!sp2) return GLYPH11_ERR_REQUEST_LINE;
    size_t second_space = (size_t)(sp2 - buf);

    /* reject multiple spaces (RFC 9112 §3) */
    if (second_space > first_space + 1 && buf[first_space + 1] == ' ')
        return GLYPH11_ERR_MULTIPLE_SP;
    if (second_space + 1 < rl_end && buf[second_space + 1] == ' ')
        return GLYPH11_ERR_MULTIPLE_SP;

    /* ---- method ---- */
    size_t method_len = first_space;
    if (method_len == 0 || method_len > L.max_method_len) return GLYPH11_ERR_METHOD_LENGTH;
    if (first_invalid(buf, method_len, is_token) >= 0) return GLYPH11_ERR_METHOD_TOKEN;
    req->method = (glyph11_span){0, (uint32_t)method_len};

    int method_is_connect = ci_eq(buf, method_len, "connect", 7);
    int method_is_options = ci_eq(buf, method_len, "options", 7);
    if (method_is_connect) return GLYPH11_ERR_CONNECT;

    /* ---- url / request-target ---- */
    size_t url_start = first_space + 1;
    size_t url_len   = second_space - url_start;
    if (url_len > L.max_url_len) return GLYPH11_ERR_URL_TOO_LONG;
    const uint8_t* url = buf + url_start;
    if (scan_reqtarget(url, url_len) >= 0) return GLYPH11_ERR_TARGET_CHAR;
    req->target = (glyph11_span){(uint32_t)url_start, (uint32_t)url_len};

    /* ---- version ---- */
    size_t ver_start = second_space + 1;
    size_t ver_len   = rl_end - ver_start;
    if (!is_http_version(buf + ver_start, ver_len)) return GLYPH11_ERR_VERSION;
    req->version = (glyph11_span){(uint32_t)ver_start, (uint32_t)ver_len};

    /* ---- path + query ---- */
    size_t path_off, path_len;
    const uint8_t* qmark = (const uint8_t*)memchr(url, '?', url_len);
    if (qmark) {
        size_t qrel = (size_t)(qmark - url);
        path_off = url_start; path_len = qrel;

        size_t qa_start = url_start + qrel + 1;
        size_t qlen     = url_len - (qrel + 1);
        const uint8_t* query = buf + qa_start;
        uint32_t pcount = 0;
        size_t cur = 0;
        while (cur < qlen) {
            size_t pair_abs = qa_start + cur;
            const uint8_t* amp = (const uint8_t*)memchr(query + cur, '&', qlen - cur);
            size_t pair_len = amp ? (size_t)(amp - (query + cur)) : (qlen - cur);
            const uint8_t* pair = query + cur;
            const uint8_t* eqp = (const uint8_t*)memchr(pair, '=', pair_len);
            size_t eq = eqp ? (size_t)(eqp - pair) : 0;
            int has_param = (eqp && eq > 0) || (!eqp && pair_len > 0);
            if (has_param) {
                if (pcount >= L.max_query_param_count) return GLYPH11_ERR_TOO_MANY_QUERY_PARAMS;
                pcount++;
                if (req->query) {
                    if (req->query_count >= req->query_cap) return GLYPH11_ERR_TOO_MANY_QUERY_PARAMS;
                    glyph11_field* f = &req->query[req->query_count];
                    if (eqp && eq > 0) {
                        f->name  = (glyph11_span){(uint32_t)pair_abs, (uint32_t)eq};
                        f->value = (glyph11_span){(uint32_t)(pair_abs + eq + 1),
                                                  (uint32_t)(pair_len - (eq + 1))};
                    } else {
                        f->name  = (glyph11_span){(uint32_t)pair_abs, (uint32_t)pair_len};
                        f->value = (glyph11_span){0, 0};
                    }
                    req->query_count++;
                }
            }
            cur += pair_len + (amp ? 1 : 0);
        }
    } else {
        path_off = url_start; path_len = url_len;
    }
    req->path = (glyph11_span){(uint32_t)path_off, (uint32_t)path_len};

    /* path semantics + asterisk-form */
    glyph11_status ps = validate_path(buf + path_off, path_len);
    if (ps != GLYPH11_OK) return ps;
    if (path_len == 1 && buf[path_off] == '*' && !method_is_options)
        return GLYPH11_ERR_ASTERISK_FORM;

    /* ---- headers ---- */
    size_t line_start = rl_end + 2;
    uint32_t hcount = 0;
    int has_cl = 0, has_te = 0, host_count = 0;
    const uint8_t* first_cl = NULL; size_t first_cl_len = 0;

    for (;;) {
        long ll = find_crlf(buf + line_start, len - line_start);
        if (ll < 0) return GLYPH11_ERR_REQUEST_LINE;   /* unreachable: block ends in CRLFCRLF */
        size_t line_len = (size_t)ll;
        if (line_len == 0) break;                       /* empty line → end of headers */

        const uint8_t* line = buf + line_start;
        if (memchr(line, '\n', line_len) != NULL) return GLYPH11_ERR_BARE_LF;
        if (line[0] == ' ' || line[0] == '\t') return GLYPH11_ERR_OBS_FOLD;

        const uint8_t* col = (const uint8_t*)memchr(line, ':', line_len);
        if (!col) return GLYPH11_ERR_NO_COLON;
        size_t colon = (size_t)(col - line);
        if (colon == 0) return GLYPH11_ERR_EMPTY_NAME;
        if (line[colon - 1] == ' ' || line[colon - 1] == '\t') return GLYPH11_ERR_WS_BEFORE_COLON;

        /* name */
        size_t name_len = colon;
        if (name_len > L.max_header_name_len) return GLYPH11_ERR_HEADER_NAME_TOO_LONG;
        if (first_invalid(line, name_len, is_token) >= 0) return GLYPH11_ERR_HEADER_NAME;

        /* value (trim leading OWS) */
        size_t line_end = line_start + line_len;
        size_t val_abs  = line_start + colon + 1;
        while (val_abs < line_end && (buf[val_abs] == ' ' || buf[val_abs] == '\t')) val_abs++;
        size_t val_len = line_end - val_abs;
        const uint8_t* val = buf + val_abs;
        if (val_len > L.max_header_value_len) return GLYPH11_ERR_HEADER_VALUE_TOO_LONG;
        if (scan_fieldvalue(val, val_len) >= 0) return GLYPH11_ERR_HEADER_VALUE;

        /* count + store */
        if (hcount >= L.max_header_count) return GLYPH11_ERR_TOO_MANY_HEADERS;
        if (hcount >= req->header_cap)    return GLYPH11_ERR_TOO_MANY_HEADERS;
        req->headers[hcount].name  = (glyph11_span){(uint32_t)line_start, (uint32_t)name_len};
        req->headers[hcount].value = (glyph11_span){(uint32_t)val_abs, (uint32_t)val_len};
        hcount++;

        /* inline semantic checks, keyed by name length then case-insensitive name */
        if (name_len == 14 && ci_eq(line, name_len, "content-length", 14)) {
            if (!cl_value_valid(val, val_len))  return GLYPH11_ERR_CL_FORMAT;
            if (cl_comma_conflict(val, val_len)) return GLYPH11_ERR_CL_CONFLICT;
            if (has_cl) {
                if (!(val_len == first_cl_len && memcmp(val, first_cl, val_len) == 0))
                    return GLYPH11_ERR_CL_CONFLICT;
            } else {
                first_cl = val; first_cl_len = val_len; has_cl = 1;
            }
        } else if (name_len == 17 && ci_eq(line, name_len, "transfer-encoding", 17)) {
            has_te = 1;
            size_t toff, tlen; trim_ows(val, val_len, &toff, &tlen);
            if (!ci_eq(val + toff, tlen, "chunked", 7)) return GLYPH11_ERR_TE_VALUE;
        } else if (name_len == 4 && ci_eq(line, name_len, "host", 4)) {
            host_count++;
            if (memchr(val, '@', val_len) || memchr(val, '/', val_len))
                return GLYPH11_ERR_HOST_FORMAT;
        }

        line_start = line_end + 2;
    }

    /* cross-header semantics */
    if (has_te && has_cl) return GLYPH11_ERR_TE_AND_CL;
    if (host_count != 1)  return GLYPH11_ERR_HOST_COUNT;

    req->header_count = hcount;
    if (consumed) *consumed = total;
    return GLYPH11_OK;
}
