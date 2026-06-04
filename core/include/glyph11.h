/*
 * glyph11.h — C ABI for the Glyph11 HTTP/1.1 request parser core.
 *
 * A single hardened parser: full RFC 9110/9112 validation, configurable resource
 * limits, and every semantic / smuggling check, fused into one pass. (The C#
 * library also ships a no-validation FlexibleParser; that fast path is intentionally
 * NOT ported — the C core exists for the security-critical validation.)
 *
 * Zero-copy, zero-allocation, dependency-free. All results are byte ranges
 * (offset + length) into the caller's input buffer; the parser never copies
 * payload bytes and never allocates. The caller owns the buffer and must keep
 * it alive for as long as it reads the parsed spans.
 *
 * This is the single source of truth shared by every language binding. It is
 * a faithful port of the Glyph11 C# reference implementation; the two are kept
 * in lock-step via differential testing.
 */
#ifndef GLYPH11_H
#define GLYPH11_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ---- Versioning -------------------------------------------------------- */

#define GLYPH11_VERSION_MAJOR 0
#define GLYPH11_VERSION_MINOR 1
#define GLYPH11_VERSION_PATCH 0

/* Packed ABI version: (major << 16) | (minor << 8) | patch.
 * Bindings can compare this against the macros they compiled against. */
uint32_t glyph11_abi_version(void);

/* ---- Resource limits --------------------------------------------------- */

/* Zero-initialize and set fields, or call glyph11_limits_default(). The
 * struct_size field must be set to sizeof(glyph11_limits) by the caller so the
 * ABI can grow without breaking older callers. */
typedef struct {
    uint32_t struct_size;            /* = sizeof(glyph11_limits) */
    uint32_t max_header_count;       /* default 100 */
    uint32_t max_header_name_len;    /* default 256 */
    uint32_t max_header_value_len;   /* default 8192 */
    uint32_t max_url_len;            /* default 8192 */
    uint32_t max_query_param_count;  /* default 128 */
    uint32_t max_method_len;         /* default 16 */
    uint32_t max_total_header_bytes; /* default 1048576 */
} glyph11_limits;

/* Fills *out with production defaults (and sets struct_size). */
void glyph11_limits_default(glyph11_limits* out);

/* ---- Result types ------------------------------------------------------ */

/* A byte range into the original input buffer (zero-copy). */
typedef struct { uint32_t off; uint32_t len; } glyph11_span;

typedef struct {
    glyph11_span name;
    glyph11_span value;
} glyph11_field;

/* Parse target. The caller provides the headers (and optional query) arrays
 * and their capacities; the parser writes spans and the *_count fields. If an
 * array is too small the parse fails with GLYPH11_ERR_TOO_MANY_HEADERS /
 * _TOO_MANY_QUERY_PARAMS (nothing is allocated or grown). */
typedef struct {
    glyph11_span method;
    glyph11_span target;   /* full request-target, as received */
    glyph11_span path;     /* request-target with the query string removed */
    glyph11_span version;  /* e.g. "HTTP/1.1" */

    glyph11_field* headers;       /* caller-provided storage (required) */
    uint32_t       header_cap;    /* in:  capacity of headers[] */
    uint32_t       header_count;  /* out: number of headers written */

    glyph11_field* query;         /* caller-provided storage (may be NULL) */
    uint32_t       query_cap;     /* in:  capacity of query[] */
    uint32_t       query_count;   /* out: number of query params written */
} glyph11_request;

/* ---- Status codes ------------------------------------------------------ */

typedef enum {
    GLYPH11_OK          = 0,   /* a complete, valid header block was parsed */
    GLYPH11_INCOMPLETE  = 1,   /* no CRLFCRLF yet — read more (not an error) */

    /* Structural / syntax violations (HTTP 400). */
    GLYPH11_ERR_REQUEST_LINE = 100,
    GLYPH11_ERR_METHOD_TOKEN,
    GLYPH11_ERR_METHOD_LENGTH,   /* method longer than max_method_len (HTTP 400, matches C#) */
    GLYPH11_ERR_VERSION,
    GLYPH11_ERR_TARGET_CHAR,
    GLYPH11_ERR_BARE_LF,
    GLYPH11_ERR_OBS_FOLD,
    GLYPH11_ERR_WS_BEFORE_COLON,
    GLYPH11_ERR_MULTIPLE_SP,
    GLYPH11_ERR_HEADER_NAME,
    GLYPH11_ERR_HEADER_VALUE,
    GLYPH11_ERR_EMPTY_NAME,
    GLYPH11_ERR_NO_COLON,

    /* Semantic violations (HTTP 400). */
    GLYPH11_ERR_TE_AND_CL = 200,
    GLYPH11_ERR_CL_CONFLICT,
    GLYPH11_ERR_CL_FORMAT,
    GLYPH11_ERR_TE_VALUE,
    GLYPH11_ERR_HOST_COUNT,
    GLYPH11_ERR_HOST_FORMAT,
    GLYPH11_ERR_DOT_SEGMENT,
    GLYPH11_ERR_BACKSLASH,
    GLYPH11_ERR_DOUBLE_ENCODING,
    GLYPH11_ERR_NULL_BYTE,
    GLYPH11_ERR_OVERLONG_UTF8,
    GLYPH11_ERR_FRAGMENT,
    GLYPH11_ERR_CONNECT,
    GLYPH11_ERR_ASTERISK_FORM,

    /* Resource-limit breaches (HTTP 431). */
    GLYPH11_ERR_TOO_LARGE = 300,         /* total header bytes over max_total_header_bytes */
    GLYPH11_ERR_URL_TOO_LONG,
    GLYPH11_ERR_HEADER_NAME_TOO_LONG,
    GLYPH11_ERR_HEADER_VALUE_TOO_LONG,
    GLYPH11_ERR_TOO_MANY_HEADERS,
    GLYPH11_ERR_TOO_MANY_QUERY_PARAMS
} glyph11_status;

/* The HTTP response code a server should return for a status: 400, 431, or 0
 * for GLYPH11_OK / GLYPH11_INCOMPLETE. */
int glyph11_status_http_code(glyph11_status status);

/* A short, static, human-readable description. Never NULL; never freed. */
const char* glyph11_status_message(glyph11_status status);

/* ---- Parse ------------------------------------------------------------- */

/*
 * Parse one complete HTTP/1.1 request header block from a single contiguous
 * buffer.
 *
 *   buf, len  : bytes received so far. NOT required to be NUL-terminated and
 *               may contain NUL bytes.
 *   limits    : resource limits to enforce; may be NULL to use defaults.
 *   req       : caller sets headers/header_cap (and optionally query/query_cap);
 *               the parser fills the spans and *_count fields.
 *   consumed  : optional out-param; on GLYPH11_OK, set to the number of bytes
 *               up to and including the terminating CRLFCRLF.
 *
 * Returns GLYPH11_OK, GLYPH11_INCOMPLETE, or a specific error. Performs no
 * allocation and writes nothing outside the caller-provided arrays.
 */
glyph11_status glyph11_parse_request(
    const uint8_t* buf, size_t len,
    const glyph11_limits* limits,
    glyph11_request* req, size_t* consumed);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* GLYPH11_H */
