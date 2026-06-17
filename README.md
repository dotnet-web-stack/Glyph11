# Glyph11

A zero-allocation, hardened HTTP/1.1 request parser for .NET — RFC 9110/9112 validation,
configurable resource limits, and request-smuggling / semantic checks fused into a single
zero-copy pass. Available as three NuGet packages that trade portability, native speed, and
raw throughput.

[![Glyph11](https://img.shields.io/nuget/v/Glyph11.svg?label=Glyph11)](https://www.nuget.org/packages/Glyph11/)
[![Glyph11.Native](https://img.shields.io/nuget/v/Glyph11.Native.svg?label=Glyph11.Native)](https://www.nuget.org/packages/Glyph11.Native/)
[![Glyph11.Pico](https://img.shields.io/nuget/v/Glyph11.Pico.svg?label=Glyph11.Pico)](https://www.nuget.org/packages/Glyph11.Pico/)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512bd4)
[![Docs](https://img.shields.io/badge/docs%20%26%20benchmarks-online-blue)](https://dotnet-web-stack.github.io/Glyph11/)

## Three packages

| Package | What it is | Output | Validation | Dependencies | Best for |
|---|---|---|---|---|---|
| **[Glyph11](https://www.nuget.org/packages/Glyph11/)** | pure-managed hardened parser | `BinaryRequest` | full (RFC + smuggling) | none — runs anywhere | portability, zero deps |
| **[Glyph11.Native](https://www.nuget.org/packages/Glyph11.Native/)** | the C core via P/Invoke | raw field spans | full (same as managed) | native `libglyph11` per RID | native speed, zero-alloc |
| **[Glyph11.Pico](https://www.nuget.org/packages/Glyph11.Pico/)** | picohttpparser + managed glue | `BinaryRequest` | picohttpparser only | native `libglyph11pico` + `Glyph11` | maximum speed, validate elsewhere |

All three produce the same data; `Glyph11` and `Glyph11.Pico` fill the identical
`BinaryRequest`, so swapping between them is a one-line change.

## Glyph11 — pure managed

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

## Glyph11.Native — C core via P/Invoke

Same validation, native speed, **zero allocation** (you provide the field storage). Bundles
`libglyph11` for `linux-x64/arm64`, `win-x64/arm64`, `osx-x64/arm64`.

```csharp
using System.Text;
using Glyph11.Native;

byte[] request = Encoding.ASCII.GetBytes(
    "GET /api/users?page=1 HTTP/1.1\r\nHost: example.com\r\n\r\n");

var limits = Glyph11Limits.Default;
Span<Glyph11Field> headers = stackalloc Glyph11Field[(int)limits.MaxHeaderCount];
Span<Glyph11Field> query   = stackalloc Glyph11Field[(int)limits.MaxQueryParameterCount];

int status = Glyph11Parser.Parse(request, headers, query, limits, out var r);
if (status == Glyph11Parser.Ok)
{
    string Slice(Glyph11Span s) => Encoding.ASCII.GetString(request, (int)s.Offset, (int)s.Length);
    Console.WriteLine(Slice(r.Method)); // GET
    Console.WriteLine(Slice(r.Path));   // /api/users
    for (int i = 0; i < r.HeaderCount; i++)
        Console.WriteLine($"{Slice(headers[i].Name)}: {Slice(headers[i].Value)}");
}
// status: 0 = OK, 1 = incomplete, otherwise a protocol/limit error (→ HTTP 400 / 431).
```

A `ReadOnlySequence<byte>` overload handles fragmented input: single-segment is parsed in
place (zero-copy), multi-segment is linearized into a caller-provided scratch buffer (the C
core needs one contiguous slab). It returns the contiguous span the result's offsets index
into.

> **`linux-x64` requires AVX2** (Haswell / 2013+ — universal on modern servers): the SIMD
> scanners inline into the parse loop for ~15% on large headers. Other RIDs use the portable
> baseline.

## Glyph11.Pico — fastest path to a `BinaryRequest`

[picohttpparser](https://github.com/h2o/picohttpparser) (native, SSE4.2) tokenizes; minimal
managed glue fills the same `BinaryRequest` as `Glyph11`. **No validation beyond
picohttpparser's** — the trade is raw speed.

```csharp
using System.Text;
using Glyph11.Pico;
using Glyph11.Protocol;

var request = new BinaryRequest();
byte[] input = "GET /api/users?page=1 HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray();

if (PicoParser.TryParse(input, request, out int consumed))
{
    Console.WriteLine(Encoding.ASCII.GetString(request.Method.Span)); // GET
    Console.WriteLine(Encoding.ASCII.GetString(request.Path.Span));   // /api/users
    // request.Version / .Headers / .QueryParameters — same shape as Glyph11.
}
```

A `ReadOnlySequence<byte>` overload is also available — single-segment is zero-copy,
multi-segment is linearized into a fresh array (the `BinaryRequest` slices keep it alive).

Use it when you want the fastest path to a `BinaryRequest` and validate elsewhere (or trust
the source). For the hardened parser, use `Glyph11`.

## Chunked bodies

Both `Glyph11` and `Glyph11.Pico` (which depends on `Glyph11`) decode chunked
transfer-encoding via `ChunkedBodyStream`; `Glyph11.Native` exposes the C decoder.

```csharp
using Glyph11.Parser;

var stream = new ChunkedBodyStream();
var r = stream.TryReadChunk(input, out int consumed, out int dataOffset, out int dataLength);
// r: Chunk | Completed | NeedMoreData | Error
```

## Benchmarks

`linux-x64`, best-of-5, ns/op (lower is better). `Glyph11.Native` / `Glyph11.Pico` are the
AVX2 / SSE4.2 native builds. The same tables and usage docs are at
**<https://dotnet-web-stack.github.io/Glyph11/>**; the harnesses are in
[`bench/`](bench).

**Request header parsing** — contiguous buffer (→ `BinaryRequest` for Glyph11 / Pico; raw spans for Native):

| Payload | Glyph11 | Glyph11.Native | Glyph11.Pico |
|---|---:|---:|---:|
| ~95 B   | 120 ns  | 99 ns   | **80 ns** |
| 4 KB    | 750 ns  | 502 ns  | **487 ns** |
| 32 KB   | 5251 ns | 3752 ns | **3370 ns** |

**Multi-segment** — fragmented request (3 segments) linearized into a buffer per request, then parsed:

| Payload | Glyph11 | Glyph11.Native | Glyph11.Pico |
|---|---:|---:|---:|
| ~95 B   | 245 ns  | **116 ns** | 118 ns |
| 4 KB    | 1258 ns | 721 ns  | **674 ns** |
| 32 KB   | 8695 ns | 4969 ns | **4627 ns** |

**Chunked body decoding** (decoded size; `Glyph11.Pico` reuses `Glyph11`'s decoder):

| Decoded | Glyph11 | Glyph11.Native |
|---|---:|---:|
| 256 B  | 19 ns  | 21 ns  |
| 4 KB   | 115 ns | **70 ns** |
| 32 KB  | 826 ns | **808 ns** |

`Glyph11.Pico` is the fastest to a full `BinaryRequest` — it skips glyph11's hardening, so
pick it only when you validate elsewhere.

## Kotlin / JVM

The C core is also reachable from the JVM via Panama FFM (JDK 21+) — see
[`bindings/kotlin`](bindings/kotlin). Not a NuGet package.

## Build the native cores

```sh
cmake -S core -B core/build-rel -DGLYPH11_BUILD_TESTS=OFF     # libglyph11
cmake --build core/build-rel
cmake -S bindings/dotnet/Glyph11.Pico/native -B pico-build    # libglyph11pico
cmake --build pico-build
```

MIT licensed. `Glyph11.Pico` bundles picohttpparser (MIT/Perl, © Kazuho Oku et al.).
