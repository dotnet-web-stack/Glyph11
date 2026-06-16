# Glyph11.Native

Native (P/Invoke) binding for [**Glyph11**](https://github.com/MDA2AV/Glyph11) — a
zero-allocation, hardened HTTP/1.1 request parser. This package wraps the C core
(`libglyph11`) and **bundles the native binary for every supported platform**, so a
plain `<PackageReference>` works with no extra setup:

| OS | RIDs |
|----|------|
| Linux   | `linux-x64`, `linux-arm64` |
| Windows | `win-x64`, `win-arm64` |
| macOS   | `osx-x64`, `osx-arm64` |

The right binary is resolved from `runtimes/<rid>/native/` automatically at build/publish.

> Looking for the dependency-free, pure-managed parser? Use the
> [**`Glyph11`**](https://www.nuget.org/packages/Glyph11) package instead. This one
> trades portability for the native core (and a per-platform binary).

## Install

```sh
dotnet add package Glyph11.Native
```

## Parse a request

```csharp
using Glyph11.Native;

var request = "GET /api/users?a=1 HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray();
var headers = new Glyph11Field[64];
var query   = new Glyph11Field[16];

int status = Glyph11Parser.Parse(request, headers, query, Glyph11Limits.Default, out Glyph11Result result);
// status 0 == OK. result.HeaderCount / .Consumed; every span (result.Path,
// headers[i].Name, ...) is an Offset/Length into `request` — zero-copy.
```

## Decode a chunked body

A streaming decoder — feed each network read; it strips the chunk framing and writes the
decoded payload into your buffer, carrying partial-chunk state across calls (one call per
read, not per chunk, no allocation):

```csharp
Glyph11Chunked.Init(out var decoder);
var result = Glyph11Chunked.Decode(ref decoder, input, output, out int inConsumed, out int outWritten);
// result: Ok (need more input) | Done (terminal chunk seen) | Error (malformed framing)
```

## Notes

- Zero-allocation: the parser writes into caller-provided arrays/spans; the C core takes a
  single contiguous slab. Multi-segment input must be linearized by the caller first.
- `GLYPH11_NATIVE_PATH` overrides native resolution with an explicit path to
  `libglyph11.{so,dll,dylib}` — handy for tests and benchmarks against a fresh local build.

MIT licensed. Source and issues: <https://github.com/MDA2AV/Glyph11>.
