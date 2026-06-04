---
title: UltraHardenedParser
weight: 1
---

**Namespace:** `Glyph11.Parser.UltraHardened`

A security-hardened HTTP/1.1 header parser with RFC 9110/9112 validation, configurable resource limits, and full semantic validation — all fused into a single pass. Recommended for internet-facing applications.

## Usage

```csharp
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;

var limits = ParserLimits.Default;

// Entry point — auto-dispatches based on segment layout
bool ok = UltraHardenedParser.TryExtractFullHeaderValidated(
    ref buffer,       // ReadOnlySequence<byte>
    request,          // BinaryRequest
    in limits,        // ParserLimits
    out int bytesRead
);

// Direct ROM access (single contiguous buffer)
ReadOnlyMemory<byte> mem = ...;
bool ok = UltraHardenedParser.TryExtractFullHeaderROM(
    ref mem, request, in limits, out int bytesRead
);
```

## Return Values

- Returns `false` if the header is incomplete (no `\r\n\r\n` terminator found). This is not an error — the caller should wait for more data.
- Returns `true` when a complete header has been parsed and fully validated. `bytesReadCount` indicates how many bytes were consumed.
- Throws `HttpParseException` with a descriptive message for any protocol **or semantic** violation.

## Structural Validation

`UltraHardenedParser` enforces every rule below on the request line and headers.

### Request Line

- Method must contain only valid RFC 9110 Section 5.6.2 token characters (`A-Z`, `a-z`, `0-9`, `` !#$%&'*+-.^_`|~ ``)
- Method length must not exceed `MaxMethodLength`
- Multiple spaces between request-line components are rejected — RFC 9112 Section 3
- Request-target must not contain control characters (0x00-0x1F, 0x7F) — RFC 9112 Section 3.2
- URL length must not exceed `MaxUrlLength`
- HTTP version must match the format `HTTP/X.Y` (exactly 8 bytes, digits at positions 5 and 7)
- Query parameter count must not exceed `MaxQueryParameterCount`

### Line Endings

- Bare LF (0x0A without preceding 0x0D) is rejected — RFC 9112 Section 2.2
- Obsolete line folding (header lines starting with SP or HTAB) is rejected — RFC 9112 Section 5.2
- Whitespace between header name and colon is rejected — RFC 9112 Section 5.1

### Headers

- Header name must contain only valid token characters, be non-empty, and not exceed `MaxHeaderNameLength`
- Header value must contain only valid field-value characters (RFC 9110 Section 5.5: HTAB, SP, VCHAR, obs-text) and not exceed `MaxHeaderValueLength`
- Total header count must not exceed `MaxHeaderCount`
- Total header bytes (request line + all headers + terminators) must not exceed `MaxTotalHeaderBytes`
- Lines without a colon separator are rejected (throws, not silently skipped)

## Semantic Validation

Beyond syntax, `UltraHardenedParser` enforces the following semantic checks inline during the same pass — no separate post-parse step is required:

- **Request smuggling:** rejects `Transfer-Encoding` + `Content-Length` together; conflicting or comma-separated `Content-Length` values; invalid `Content-Length` format and leading zeros; `Transfer-Encoding` values other than `chunked`
- **Host:** requires exactly one `Host` header; rejects `@` or `/` in the Host value
- **Path traversal:** rejects `/../` and `/./` dot-segments, backslashes, double-encoding (`%25`), encoded null bytes (`%00`), and overlong UTF-8
- **Method / target:** rejects `CONNECT` on origin servers; rejects asterisk-form (`*`) for non-`OPTIONS` methods; rejects fragments (`#`) in the request-target

{{< callout type="info" >}}
Because these checks run during parsing, `UltraHardenedParser` is **stricter** than a syntax-only parser — for example, a request with no `Host` header is rejected. For trusted, pre-validated input where you don't need these checks, use [`FlexibleParser`](flexible-parser).
{{< /callout >}}

## Performance

Validation uses SIMD-accelerated `SearchValues<byte>` with `IndexOfAnyExcept` for token and field-value character checks, and vectorized `IndexOf` for bare-LF detection. The semantic checks are fused into the header and path passes for cache locality.

See the [Benchmarks](/Glyph11/benchmarks/) page for detailed numbers and trend charts.

## Multi-Segment Handling

When input arrives as multiple `ReadOnlySequence<byte>` segments (common with `PipeReader`), the entry point automatically linearizes the buffer before parsing:

1. Checks for `\r\n\r\n` presence using `SequenceReader` — returns `false` with zero allocation if incomplete
2. Calls `ToArray()` to produce a single contiguous byte array
3. Parses using the ROM path for maximum speed

See [Multi-Segment Handling](../architecture/multi-segment) for details.
