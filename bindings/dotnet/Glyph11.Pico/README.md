# Glyph11.Pico

A **fast** HTTP/1.x request-header parser. [picohttpparser](https://github.com/h2o/picohttpparser)
(native, SSE4.2) does the tokenizing; minimal managed post-processing fills the same
[`BinaryRequest`](https://www.nuget.org/packages/Glyph11) that
[**Glyph11**](https://github.com/MDA2AV/Glyph11) produces — method, path, version, query
parameters, headers, body offset.

The trade vs the hardened `Glyph11` parser: **no extra compliance checking** beyond what
picohttpparser does (no token / field-value validation, no path normalization, no limit
enforcement, no smuggling checks). You get raw speed and the same request shape.

| vs. glyph11 managed parser (same `BinaryRequest`) | ~95 B | 4 KB | 32 KB |
|---|---|---|---|
| speedup | ~1.2× | ~1.8× | ~2.0× |

## Install

```sh
dotnet add package Glyph11.Pico
```

Depends on `Glyph11` (for `BinaryRequest`, `KeyValueList`, and chunked decoding) and
bundles the native `libglyph11pico` per platform.

## Parse a request

```csharp
using Glyph11.Pico;
using Glyph11.Protocol;

var request = new BinaryRequest();
var input = "GET /api/users?a=1&b=2 HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray();

if (PicoParser.TryParse(input, request, out int consumed))
{
    // request.Method / .Path / .Version / .Headers / .QueryParameters — same shape as glyph11.
    // request.Body is the remainder of the buffer; `consumed` follows glyph11's convention.
}
```

## Chunked bodies

Reuses glyph11's decoder (this package depends on `Glyph11`):

```csharp
using Glyph11.Parser;

var stream = new ChunkedBodyStream();
var r = stream.TryReadChunk(input, out int consumed, out int dataOffset, out int dataLength);
// r: Chunk | Completed | NeedMoreData | Error
```

## When to use which

- **`Glyph11.Pico`** — you want the fastest path to a `BinaryRequest` and will validate
  elsewhere (or trust the source).
- **`Glyph11`** — you want the hardened, validating parser (token/field-value checks,
  limits, smuggling defenses) in pure managed code.
- **`Glyph11.Native`** — the hardened parser via the C core (raw field spans, not `BinaryRequest`).

MIT licensed; bundles picohttpparser (MIT/Perl, © Kazuho Oku et al.).
Source: <https://github.com/MDA2AV/Glyph11>.
