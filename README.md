# Glyph11

A zero-allocation, hardened HTTP/1.1 parser for .NET. It parses requests and responses with
RFC 9110/9112 validation, request-smuggling defences and configurable resource limits, all in
a single zero-copy pass. Pure managed and dependency-free.

[![Glyph11](https://img.shields.io/nuget/v/Glyph11.svg?label=Glyph11)](https://www.nuget.org/packages/Glyph11/)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512bd4)
[![Docs](https://img.shields.io/badge/docs%20%26%20benchmarks-online-blue)](https://dotnet-web-stack.github.io/Glyph11/)

> **`Glyph11.Native` and `Glyph11.Pico` are discontinued.** Their published versions stay on
> NuGet but get no further releases. Glyph11 is the managed parser only.

```
dotnet add package Glyph11
```

## Parsing a request

The parser reads a `ReadOnlySequence<byte>` (from a `PipeReader`, a socket, …) or a contiguous
`ReadOnlyMemory<byte>`. Every field it fills is a zero-copy slice of your buffer.

```csharp
using System.Buffers;
using System.Text;
using Glyph11;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;

var request = new BinaryRequest();          // reuse it across requests; it holds pooled arrays
var limits  = ParserLimits.Default;

ReadOnlySequence<byte> buffer = new(Encoding.ASCII.GetBytes(
    "GET /api/users?page=1 HTTP/1.1\r\nHost: example.com\r\nAccept: */*\r\n\r\n"));

try
{
    if (UltraHardenedParser.TryParseRequestHeader(ref buffer, request, in limits, out int consumed))
    {
        Console.WriteLine(Encoding.ASCII.GetString(request.Method.Span)); // GET
        Console.WriteLine(Encoding.ASCII.GetString(request.Path.Span));   // /api/users

        for (int i = 0; i < request.Headers.Count; i++)
        {
            var (name, value) = request.Headers[i];
            Console.WriteLine($"{Encoding.ASCII.GetString(name.Span)}: {Encoding.ASCII.GetString(value.Span)}");
        }

        // The body, or the next pipelined request, starts at `consumed`.
    }
    else
    {
        // Incomplete: read more bytes and call again with the whole buffer.
    }
}
catch (HttpParseException ex)
{
    // A complete but invalid request: answer ex.StatusCode (400, 431 or 505) and close.
}
```

- `true`: the header block is complete and valid. `consumed` is its length, including the
  final CRLFCRLF.
- `false`: more bytes are needed. A message that can never become valid, such as one with a
  bare LF, is rejected right away rather than reported incomplete.
- `HttpParseException`: the request is invalid. `StatusCode` is 431 when a limit is exceeded
  (`IsLimitViolation`), 505 for a well-formed HTTP version other than 1.0/1.1, and 400 for
  everything else.

The parser clears `request` before filling it, so nothing carries over from the previous
message. Disposing the request returns its arrays to the `ArrayPool`.

## Parsing a response

`TryParseResponseHeader` takes the same arguments and fills a `BinaryResponse` (version,
status code, reason phrase, headers):

```csharp
var response = new BinaryResponse();
if (UltraHardenedParser.TryParseResponseHeader(ref buffer, response, in limits, out int consumed))
{
    int status = response.Status;   // 100-599; anything else throws
}
```

## Body framing

After the header, the framing headers decide how the body is read.

```csharp
using Glyph11.Validation;

var framing = BodyFramingDetector.DetectBodyFraming(request);
// framing.Framing: None | ContentLength (framing.ContentLength bytes) | Chunked

var responseFraming = BodyFramingDetector.DetectResponseBodyFraming(response, "GET"u8);
// also UntilClose: a response with no framing runs to the end of the connection
```

- `DetectResponseBodyFraming` needs the method of the request that produced the response. A
  response to HEAD, a 1xx/204/304, and a 2xx to CONNECT have no body, whatever their headers
  say.
- Transfer-Encoding: chunked takes priority over Content-Length (RFC 9112 §6.1).
- Content-Length fields and list elements must all state the same length, or the helpers throw
  400. The hardened parser already refuses such requests; this protects messages from other
  parsers too.
- A length too large for a `long` frames as `long.MaxValue`, never as "no body". Your body-size
  limit then refuses it.
- `ContentLengthBodyReader.ParseContentLength(request)` returns the same length directly (-1
  when absent). `HasCompleteBody(body, length)` tells you whether the whole body has arrived.

## Chunked bodies

`ChunkedBodyStream` decodes chunked transfer-encoding incrementally, from a span or a
`ReadOnlySequence<byte>`:

```csharp
var decoder = new ChunkedBodyStream(limits);   // or new ChunkedBodyStream() for ParserLimits.Default

switch (decoder.TryReadChunk(input, out int consumed, out int dataOffset, out int dataLength))
{
    case ChunkResult.Chunk:        // input[dataOffset..][..dataLength] is body data; advance by consumed
    case ChunkResult.Completed:    // the last chunk and trailers are done; advance by consumed
    case ChunkResult.NeedMoreData: // nothing consumed: present the same bytes plus more next time
        break;
}
// Malformed framing throws HttpParseException.
```

- Data is handed out as it arrives. A `Chunk` may be part of a chunk, so a peer's declared chunk
  size never decides how much you buffer.
- The trailer section is capped by `ParserLimits.MaxTotalTrailerBytes` (exceeding it throws 431).
- Chunk sizes of more than 16 hex digits, or of 2^63 and above, are refused.

`ChunkedBodyValidator.TryValidate(body, out int consumed)` checks a complete chunked body in one
call.

## Limits and options

`ParserLimits` is a record struct. Change any field with `with`:

```csharp
var limits = ParserLimits.Default with { MaxHeaderCount = 50, AllowBareLf = true };
```

| Field | Default | Exceeded |
|---|---:|---|
| `MaxHeaderCount` | 100 | 431 |
| `MaxHeaderNameLength` | 256 | 431 |
| `MaxHeaderValueLength` | 8192 | 431 |
| `MaxUrlLength` | 8192 | 431 |
| `MaxQueryParameterCount` | 128 | 431 |
| `MaxMethodLength` | 16 | 400 |
| `MaxReasonPhraseLength` (responses) | 512 | 431 |
| `MaxTotalHeaderBytes` | 1 MiB | 431 |
| `MaxTotalTrailerBytes` | 16 KiB | 431 |
| `AllowBareLf` | `false` | |

`AllowBareLf` accepts messages that use a bare LF as the line terminator **throughout**
(RFC 9112 §2.2). A message that mixes CRLF and bare LF is still rejected, because mixed endings
are where two parsers disagree about where a line ends.

## Two parsers

- **`UltraHardenedParser`** validates everything, in the same pass that parses.
  - **Grammar:** the request line, tokens, header names and values. It refuses CTLs, obs-fold,
    whitespace before a colon and bare LF.
  - **HTTP version:** malformed is 400; well-formed but unsupported is 505.
  - **Request smuggling:** Transfer-Encoding together with Content-Length, conflicting or
    malformed Content-Length, and obfuscated Transfer-Encoding.
  - **Host:** exactly one, matching the RFC 3986 host grammar (reg-name or IPv6 literal, with
    an optional port of at most 65535).
  - **Path:** traversal, double encoding, encoded NUL and overlong UTF-8.
  - **Responses:** a status code outside 100-599.
  - **Limits:** everything in the table above.

  [SECURITY.md](SECURITY.md) describes every check.
- **`FlexibleParser`** does minimal validation, for input you already trust. It has the same
  `TryParseRequestHeader` shape and no limits argument.

## Migrating to 0.4.0

- `TryExtractFullHeaderROM`, `TryExtractFullHeaderValidated`, the response equivalents and
  `FlexibleParser`'s are obsolete. They reported one less than the bytes consumed, which is why
  callers added 1. Switch to `TryParseRequestHeader` / `TryParseResponseHeader` and **drop the
  `+ 1`**.
- The parser clears its target. A `Clear()` before each call is no longer needed, and still
  harmless.
- HTTP version errors: a malformed version is now 400 (it was 505); 505 is for well-formed
  versions other than 1.0/1.1.
- `ChunkedBodyStream` may return a chunk in parts. Callers that append each `Chunk` and advance
  by `consumed` need no change.
- The framing helpers can throw on conflicting Content-Length fields.

## Tests

`dotnet test tests/Tests/Tests.csproj` runs the xUnit suite: parsing, limits, smuggling vectors,
chunked decoding (byte by byte and at random split points) and every entry point with contiguous
and multi-segment input. Property tests compare Content-Length saturation and chunk-size limits
with `BigInteger`, and IP-literals with a regular expression transcribed from the RFC 3986
grammar.

## Benchmarks

`linux-x64`, best-of-5, ns/op (lower is better). The same tables and usage docs are at
**<https://dotnet-web-stack.github.io/Glyph11/>**, and the harness is in
[`Benchmarks/`](Benchmarks).

**Request header parsing**, contiguous buffer (→ `BinaryRequest`):

| Payload | Glyph11 |
|---|---:|
| ~95 B   | 120 ns  |
| 4 KB    | 750 ns  |
| 32 KB   | 5251 ns |

**Multi-segment**, a fragmented request (3 segments); only the header block is linearized:

| Payload | Glyph11 |
|---|---:|
| ~95 B   | 245 ns  |
| 4 KB    | 1258 ns |
| 32 KB   | 8695 ns |

**Chunked body decoding** (decoded size):

| Decoded | Glyph11 |
|---|---:|
| 256 B  | 19 ns  |
| 4 KB   | 115 ns |
| 32 KB  | 826 ns |

MIT licensed.
