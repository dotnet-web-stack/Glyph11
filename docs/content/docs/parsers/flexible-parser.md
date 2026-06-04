---
title: FlexibleParser
weight: 2
---

**Namespace:** `Glyph11.Parser.FlexibleParser`

A minimal-validation HTTP/1.1 header parser optimized for maximum throughput. Performs no RFC compliance checks, no character validation, and enforces no resource limits.

{{< callout type="warning" >}}
The `FlexibleParser` does not validate input. Use it only when parsing trusted or pre-validated data. For untrusted input, use [`UltraHardenedParser`](ultra-hardened-parser) instead.
{{< /callout >}}

## Usage

```csharp
using Glyph11.Parser.FlexibleParser;

// Entry point — auto-dispatches based on segment layout
bool ok = FlexibleParser.TryExtractFullHeader(
    ref buffer,       // ReadOnlySequence<byte>
    request,          // BinaryRequest
    out int bytesRead
);

// Direct ROM access (single contiguous buffer)
ReadOnlyMemory<byte> mem = ...;
bool ok = FlexibleParser.TryExtractFullHeaderReadOnlyMemory(
    ref mem, request, out int bytesRead
);
```

## Return Values

- Returns `false` if the header is incomplete (no `\r\n\r\n` terminator found). This is not an error — the caller should wait for more data.
- Returns `true` when a complete header has been parsed. `bytesReadCount` indicates how many bytes were consumed.
- Throws `HttpParseException` only for structurally unparseable input (missing request line spaces).

## Behavior

The FlexibleParser prioritizes speed over strictness:

- **No character validation** on method, header names, or header values
- **No resource limits** — no maximum header count, name length, value length, or URL length
- **No HTTP version validation** — accepts any version string
- **Malformed header lines** (missing colon) are silently skipped, not rejected
- **No bare LF rejection** — accepts both CRLF and bare LF line endings
- **No obs-fold rejection** — continuation lines are not detected

## Multi-Segment Handling

Same as UltraHardenedParser — when input arrives as multiple `ReadOnlySequence<byte>` segments, the entry point automatically linearizes the buffer before parsing. See [Multi-Segment Handling](../architecture/multi-segment) for details.

## Migrating to UltraHardenedParser

```csharp
// Before (FlexibleParser — no limits parameter)
FlexibleParser.TryExtractFullHeader(ref buffer, request, out bytesRead);

// After (UltraHardenedParser — add ParserLimits)
var limits = ParserLimits.Default;
UltraHardenedParser.TryExtractFullHeaderValidated(ref buffer, request, in limits, out bytesRead);
```
