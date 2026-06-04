# Glyph11

A zero-allocation, hardened HTTP/1.1 request parser — a pure-C# library and a C core
(`libglyph11`) reachable from .NET and the JVM. RFC 9110/9112 validation, configurable
resource limits, and request-smuggling / semantic checks fused into a single zero-copy pass.

[![NuGet](https://img.shields.io/nuget/v/Glyph11.svg)](https://www.nuget.org/packages/Glyph11/)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512bd4)
[![Benchmarks](https://img.shields.io/badge/benchmarks-live-blue)](https://dotnet-web-stack.github.io/Glyph11/)

Three ways to use the same hardened parser:

| | What | Header storage |
|---|---|---|
| **C# library** | pure managed `UltraHardenedParser` | pooled, internal |
| **.NET binding** | the C core via P/Invoke | caller-provided (zero-alloc) |
| **Kotlin binding** | the C core via Panama FFM | per-call, returned as a list |

## C# library (managed)

```csharp
using System.Buffers;
using System.Text;
using Glyph11.Protocol;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;

var request = new BinaryRequest();
var limits  = ParserLimits.Default;

// From any source that yields a ReadOnlySequence<byte> (PipeReader, Socket, NetworkStream, …)
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

`TryExtractFullHeaderROM(ref ReadOnlyMemory<byte>, …)` is the single-buffer (contiguous) fast path.
`FlexibleParser` is a minimal-validation variant for trusted, pre-validated input.

## .NET binding (C core via P/Invoke)

Calls `libglyph11` directly — same validation, native speed, **zero allocation** (you provide the
header/query storage).

```csharp
using System.Text;
using Glyph11.Native;

byte[] request = Encoding.ASCII.GetBytes(
    "GET /api/users?page=1 HTTP/1.1\r\nHost: example.com\r\nAccept: */*\r\n\r\n");

// Storage for the parsed fields — size it to the limits, so any request the policy
// accepts fits. A request with more headers is rejected (HTTP 431), never an
// overflow: the core bounds-checks every write.
var limits = Glyph11Limits.Default;                                 // MaxHeaderCount = 100
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
// status: 0 = OK, 1 = incomplete (read more), otherwise a protocol/limit error (→ HTTP 400 / 431).
```

Keep the header/query arrays at least `MaxHeaderCount` / `MaxQueryParameterCount` — a smaller
array silently lowers your effective limit (the parser returns `TOO_MANY_HEADERS` /
`TOO_MANY_QUERY_PARAMS` once it fills, never an overflow). For large limits, rent from
`ArrayPool<Glyph11Field>` instead of `stackalloc`.

Resolve the native library with the `GLYPH11_NATIVE_PATH` environment variable, or put
`libglyph11.{so,dll,dylib}` on the OS load path.

## Kotlin / JVM binding (C core via Panama FFM)

```kotlin
import io.glyph11.Glyph11
import io.glyph11.Glyph11Span

val request = "GET /api/users?page=1 HTTP/1.1\r\nHost: example.com\r\nAccept: */*\r\n\r\n"
    .toByteArray(Charsets.ISO_8859_1)

val r = Glyph11.parse(request)
when {
    r.isOk -> {
        fun slice(s: Glyph11Span) = String(request, s.offset, s.length, Charsets.ISO_8859_1)
        println(slice(r.method))                     // GET
        println(slice(r.path))                       // /api/users
        for (h in r.headers)
            println("${slice(h.name)}: ${slice(h.value)}")
    }
    r.isIncomplete -> { /* read more bytes */ }
    else -> println("rejected → HTTP ${Glyph11.httpCode(r.status)}")  // 400 / 431
}
```

Requires JDK 21+ (FFM). Point at the library with `-Dglyph11.lib=/path/to/libglyph11.so`.

## Benchmarks

Live cross-language numbers — managed vs. the C core and its .NET / JVM bindings, contiguous and
multi-segment: **<https://dotnet-web-stack.github.io/Glyph11/>**

## Build the native core (for the bindings)

```sh
cmake -S core -B core/build-rel -DGLYPH11_BUILD_TESTS=OFF
cmake --build core/build-rel     # → core/build-rel/libglyph11.{so,dll,dylib}
```
