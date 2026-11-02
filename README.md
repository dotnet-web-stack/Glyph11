# Glyph11

A zero-allocation, hardened HTTP/1.1 request parser for .NET — RFC 9110/9112 validation,
configurable resource limits, and request-smuggling / semantic checks fused into a single
zero-copy pass. Pure managed and dependency-free.

[![Glyph11](https://img.shields.io/nuget/v/Glyph11.svg?label=Glyph11)](https://www.nuget.org/packages/Glyph11/)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512bd4)
[![Docs](https://img.shields.io/badge/docs%20%26%20benchmarks-online-blue)](https://dotnet-web-stack.github.io/Glyph11/)

> **`Glyph11.Native` and `Glyph11.Pico` are discontinued.** Their published versions stay on
> NuGet, but they get no further releases — Glyph11 is the managed parser only.

## Usage

Dependency-free, runs on any .NET target. Parses a `ReadOnlySequence<byte>` (from a
`PipeReader`, socket, …) or a contiguous `ReadOnlyMemory<byte>`.

```csharp
using System.Buffers;
using System.Text;
using Glyph11.Protocol;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;

var request = new BinaryRequest();
var limits  = ParserLimits.Default;

ReadOnlySequence<byte> buffer = new(Encoding.ASCII.GetBytes(
    "GET /api/users?page=1 HTTP/1.1\r\nHost: example.com\r\nAccept: */*\r\n\r\n"));

if (UltraHardenedParser.TryExtractFullHeaderValidated(ref buffer, request, in limits, out int bytesRead))
{
    Console.WriteLine(Encoding.ASCII.GetString(request.Method.Span)); // GET
    Console.WriteLine(Encoding.ASCII.GetString(request.Path.Span));   // /api/users

    for (int i = 0; i < request.Headers.Count; i++)
    {
        var (name, value) = request.Headers[i];                       // zero-copy slices
        Console.WriteLine($"{Encoding.ASCII.GetString(name.Span)}: {Encoding.ASCII.GetString(value.Span)}");
    }
    // advance your reader by bytesRead; reuse `request` across calls (request.Clear()).
}
// throws HttpParseException on a protocol/semantic violation; returns false if incomplete.
```

`TryExtractFullHeaderROM(ref ReadOnlyMemory<byte>, …)` is the single-buffer fast path.

## Chunked bodies

`ChunkedBodyStream` decodes chunked transfer-encoding incrementally.

```csharp
using Glyph11.Parser;

var stream = new ChunkedBodyStream();
var r = stream.TryReadChunk(input, out int consumed, out int dataOffset, out int dataLength);
// r: Chunk | Completed | NeedMoreData | Error
```

## Benchmarks

`linux-x64`, best-of-5, ns/op (lower is better). The same tables and usage docs are at
**<https://dotnet-web-stack.github.io/Glyph11/>**; the harness is in
[`Benchmarks/`](Benchmarks).

**Request header parsing** — contiguous buffer (→ `BinaryRequest`):

| Payload | Glyph11 |
|---|---:|
| ~95 B   | 120 ns  |
| 4 KB    | 750 ns  |
| 32 KB   | 5251 ns |

**Multi-segment** — fragmented request (3 segments) linearized into a buffer per request, then parsed:

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
