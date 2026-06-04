# Glyph11 Security Model

Glyph11's security is delivered by **`UltraHardenedParser`**, which enforces RFC 9110/9112 syntax rules, configurable resource limits, and semantic attack detection in a single parse pass. Any violation throws `HttpParseException`.

`FlexibleParser` performs **no** validation and is intended only for trusted, pre-validated input (for example, behind a hardened reverse proxy). Pick one:

- **Untrusted / internet-facing input** → `UltraHardenedParser`
- **Trusted / pre-validated input** → `FlexibleParser`

---

## Syntactic Protections

These checks are enforced during `TryExtractFullHeaderValidated` / `TryExtractFullHeaderROM`. Violations throw `HttpParseException`.

### Bare LF Rejection

**Attack:** HTTP request smuggling via bare line feed (0x0A without preceding 0x0D). Some parsers accept bare LF as a line terminator while others require CRLF, creating parsing discrepancies.

**RFC:** 9112 Section 2.2 — "A recipient of such a bare CR MUST consider that element to be invalid."

**Protection:** The parser checks each parsed line for bare LF (0x0A) using vectorized `IndexOf`. Any bare LF causes immediate rejection.

**CVEs prevented:** CVE-2023-30589 (Node.js), CVE-2025-58056 (Netty), CVE-2019-16785 (Waitress).

### Obsolete Line Folding (obs-fold) Rejection

**Attack:** Header value continuation lines (starting with SP or HTAB) can obfuscate Transfer-Encoding headers to create smuggling vectors.

```
Transfer-Encoding: chunked\r\n
 Garbage\r\n
```

**RFC:** 9112 Section 5.2 — "A server that receives an obs-fold in a request message MUST either reject the message... or replace each received obs-fold."

**Protection:** Any header line starting with SP (0x20) or HTAB (0x09) is rejected.

### Whitespace Before Colon Rejection

**Attack:** `Content-Length : 0` (space before colon) may be accepted by some parsers but rejected by others, enabling TE.TE smuggling.

**RFC:** 9112 Section 5.1 — "No whitespace is allowed between the field name and colon."

**Protection:** The parser checks the byte immediately before the colon in each header line and rejects if it is SP or HTAB.

### Multiple Spaces in Request Line

**Attack:** Multiple spaces between request-line components (`GET  /path HTTP/1.1`) create parsing ambiguity that can lead to request smuggling.

**RFC:** 9112 Section 3 — `request-line = method SP request-target SP HTTP-version`

**Protection:** After finding the space delimiters, the parser verifies no additional spaces follow immediately.

### Request-Target Control Character Rejection

**Attack:** Control characters (0x00-0x1F, 0x7F) in the URL enable null-byte path truncation and injection.

**RFC:** 9112 Section 3.2 — request-target must contain only valid URI characters.

**Protection:** The request-target is validated using SIMD-accelerated `SearchValues<byte>`; any control character causes rejection.

### Method Token Validation

**Attack:** Invalid characters in HTTP methods can confuse downstream processing.

**RFC:** 9110 Section 5.6.2 — method is a `token` (`!#$%&'*+-.^_`|~ DIGIT ALPHA`).

**Protection:** The method is validated against the RFC 9110 token character set using SIMD-accelerated `SearchValues<byte>`.

### Header Name Token Validation

**Attack:** Control characters or invalid bytes in header names create parsing discrepancies between systems.

**RFC:** 9110 Section 5.1 — field-name is a `token`.

**Protection:** Each header name is validated against the token character set using SIMD-accelerated `SearchValues<byte>`.

### Header Value Character Validation

**Attack:** CRLF injection, null byte injection, and control character injection in header values.

**RFC:** 9110 Section 5.5 — field-value allows only HTAB (0x09), SP (0x20), VCHAR (0x21-0x7E), and obs-text (0x80-0xFF).

**Protection:** Each header value is validated against the allowed character set using SIMD-accelerated `SearchValues<byte>`. CR, LF, NUL, and all other control characters are rejected.

**CVEs prevented:** CVE-2024-52875, CVE-2024-20337.

### Empty Header Name Rejection

**Attack:** A header line starting with `:` (empty name) causes undefined behavior in different parsers.

**RFC:** 9110 Section 5.1 — field-name requires at least one token character.

**Protection:** Header lines where the colon is at position 0 are rejected.

### Missing Colon Rejection

**Attack:** Header lines without a colon separator may be silently dropped by lenient parsers, creating discrepancies.

**Protection:** Any header line that does not contain a colon is rejected with an exception (not silently skipped).

### HTTP Version Validation

**Attack:** Invalid version strings enable protocol downgrade attacks and undefined behavior.

**RFC:** 9112 Section 2.6 — `HTTP-version = "HTTP/" DIGIT "." DIGIT`

**Protection:** The version string must be exactly 8 bytes matching `HTTP/X.Y` where X and Y are ASCII digits.

### Resource Limits (DoS Prevention)

All limits are configurable via `ParserLimits`:

| Limit | Default | Attack Prevented |
|-------|---------|------------------|
| `MaxHeaderCount` | 100 | Header flooding |
| `MaxHeaderNameLength` | 256 | Oversized header names |
| `MaxHeaderValueLength` | 8192 | Oversized header values |
| `MaxUrlLength` | 8192 | URL buffer overflow |
| `MaxQueryParameterCount` | 128 | Query parameter flooding |
| `MaxMethodLength` | 16 | Oversized method strings |
| `MaxTotalHeaderBytes` | 1048576 | Total header section DoS |

---

## Semantic Protections

`UltraHardenedParser` also detects semantically dangerous patterns inline during the same pass — no separate post-parse step is required. Each violation throws `HttpParseException`.

### Request Smuggling

#### Transfer-Encoding + Content-Length

**Attack:** CL.TE / TE.CL request smuggling. When both `Transfer-Encoding` and `Content-Length` are present, front-end and back-end may disagree on message body boundaries.

**RFC:** 9112 Section 6.1

#### Conflicting Content-Length Headers

**Attack:** Multiple `Content-Length` headers with different values. One system uses the first value, another uses the last.

**RFC:** 9110 Section 8.6

#### Conflicting Comma-Separated Content-Length

**Attack:** A single `Content-Length` header with comma-separated values that differ (e.g. `Content-Length: 42, 0`).

**RFC:** 9112 Section 6.2

#### Invalid Content-Length Format

**Attack:** Non-digit characters in Content-Length values (`Content-Length: abc`, `Content-Length: 1 2`, `Content-Length: 1e5`). Different parsers interpret these differently.

**RFC:** 9110 Section 8.6 — Content-Length is `1*DIGIT`.

**CVEs prevented:** CVE-2018-7159 (Node.js).

#### Content-Length Leading Zeros

**Attack:** `Content-Length: 0200` may be interpreted as decimal 200 by one parser but octal 128 by another.

**RFC:** 9110 Section 8.6

#### Obfuscated Transfer-Encoding

**Attack:** TE.TE smuggling via obfuscated Transfer-Encoding values (`xchunked`, `"chunked"`, `chunked-thing`). One system recognizes it as chunked, another does not.

**RFC:** 9112 Section 6.1

### Host Header Attacks

#### Host Header Count

**Attack:** Missing Host header (routing confusion) or multiple Host headers (routing disagreement between front-end and back-end, SSRF).

**RFC:** 9112 Section 3.2 — exactly one Host header required for HTTP/1.1.

#### Host Header Format

**Attack:** A `Host` value containing userinfo (`@`) or a path (`/`) can redirect routing or poison caches.

**RFC:** 9110 Section 7.2 — Host is the host (and optional port) only.

### Path Traversal

#### Dot-Segment Traversal

**Attack:** `/../` and `/./` sequences in the path allow directory traversal to access files outside the intended root.

**RFC:** 3986 Section 5.2.4

#### Backslash Traversal

**Attack:** Backslash characters (`\`) are treated as path separators on Windows, enabling traversal via `\..\`.

#### Double Encoding

**Attack:** `%252e%252e` decodes to `%2e%2e` after one pass, then `..` after a second. Bypasses single-decode security filters.

#### Encoded Null Byte

**Attack:** `%00` in the path causes C-based file systems to truncate the path at the null byte. `file.txt%00.jpg` passes extension checks but opens `file.txt`.

#### Overlong UTF-8

**Attack:** Overlong UTF-8 sequences encode ASCII characters (like `/` as `0xC0 0xAF`) to bypass ASCII-only path checks.

**RFC:** 3629 Section 3 — overlong sequences are forbidden.

### Method & Target

#### Fragment in Request-Target

**Attack:** Fragment identifiers (`#`) must not appear in HTTP request-targets. Their presence indicates injection or malformed input.

**RFC:** 9112 Section 3.2

#### CONNECT Method

**Attack:** `CONNECT` establishes a tunnel and is meant for proxies; an origin server that honors it can be abused for SSRF.

**RFC:** 9110 Section 9.3.6 — origin servers should reject CONNECT.

#### Asterisk-Form Target

**Attack:** The asterisk-form request-target (`*`) is only valid for `OPTIONS`; anywhere else it signals malformed or injected input.

**RFC:** 9112 Section 3.2.4

---

## Usage

```csharp
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;

var limits = ParserLimits.Default;

// Structural + semantic validation in a single pass.
// Throws HttpParseException on any protocol or semantic violation.
if (UltraHardenedParser.TryExtractFullHeaderValidated(ref buffer, request, in limits, out var bytesRead))
{
    // Safe to process request — fully validated.
}
```

---

## Attack Categories Covered

All categories below are enforced inline by `UltraHardenedParser` during parsing.

| Category | Type |
|----------|:----:|
| HTTP Request Smuggling (CL.TE, TE.CL, TE.TE) | Semantic |
| CRLF / Header Injection | Syntactic |
| Bare LF Smuggling | Syntactic |
| Obs-fold Smuggling | Syntactic |
| Header Name Injection | Syntactic |
| Header Value Injection | Syntactic |
| Content-Length Manipulation | Semantic |
| Transfer-Encoding Obfuscation | Semantic |
| Host Header Attacks | Semantic |
| Path Traversal | Semantic |
| Null Byte Injection | Syntactic + Semantic |
| Double Encoding Bypass | Semantic |
| Overlong UTF-8 Bypass | Semantic |
| Backslash Traversal | Semantic |
| Fragment Injection | Semantic |
| Resource Exhaustion (DoS) | Limits |
| HTTP Version Manipulation | Syntactic |
| Request Line Injection | Syntactic |

---

## References

- [RFC 9110 — HTTP Semantics](https://httpwg.org/specs/rfc9110.html)
- [RFC 9112 — HTTP/1.1](https://httpwg.org/specs/rfc9112.html)
- [RFC 3986 — URI Syntax](https://www.rfc-editor.org/rfc/rfc3986)
- [RFC 3629 — UTF-8](https://www.rfc-editor.org/rfc/rfc3629)
- [PortSwigger — HTTP Request Smuggling](https://portswigger.net/web-security/request-smuggling)
- [CWE-113 — HTTP Request/Response Splitting](https://cwe.mitre.org/data/definitions/113.html)
- [CWE-444 — Inconsistent Interpretation of HTTP Requests](https://cwe.mitre.org/data/definitions/444.html)
