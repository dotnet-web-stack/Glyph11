# Examples

Runnable, fully-commented examples for each Glyph11 NuGet package. Each project mirrors what a
consumer writes (the same `using`s and APIs) — in-repo they use a `ProjectReference`; a real app
uses `dotnet add package <name>`.

| Project | Package | Native lib needed to run? |
|---|---|---|
| [`Glyph11.Example`](Glyph11.Example) | [`Glyph11`](https://www.nuget.org/packages/Glyph11/) | No — pure managed |
| [`Glyph11.Native.Example`](Glyph11.Native.Example) | [`Glyph11.Native`](https://www.nuget.org/packages/Glyph11.Native/) | Yes — `libglyph11` |
| [`Glyph11.Pico.Example`](Glyph11.Pico.Example) | [`Glyph11.Pico`](https://www.nuget.org/packages/Glyph11.Pico/) | Yes — `libglyph11pico` |

Each `Program.cs` walks through **every option**: contiguous parse, `ReadOnlySequence` parse,
custom limits, reading fields/headers/query, chunked decoding, status/error handling, and reuse.

## Run

The managed example needs nothing extra:

```sh
dotnet run --project Examples/Glyph11.Example
```

The **native** examples need the matching native library on the load path. With the NuGet packages
it's bundled automatically; running in-repo, build the core and point at it with an env var:

```sh
# Glyph11.Native — build libglyph11 and run
cmake -S core -B core/build -DGLYPH11_BUILD_TESTS=OFF && cmake --build core/build
GLYPH11_NATIVE_PATH="$PWD/core/build/libglyph11.so" \
  dotnet run --project Examples/Glyph11.Native.Example

# Glyph11.Pico — build libglyph11pico and run
cmake -S bindings/dotnet/Glyph11.Pico/native -B pico-build && cmake --build pico-build
GLYPH11_PICO_NATIVE_PATH="$PWD/pico-build/libglyph11pico.so" \
  dotnet run --project Examples/Glyph11.Pico.Example
```

(On Windows/macOS the library is `glyph11.dll` / `libglyph11.dylib`, etc.)

## Which package?

- **Glyph11** — hardened, dependency-free, runs anywhere. Returns a `BinaryRequest`.
- **Glyph11.Native** — the same hardening via the C core, native speed, zero allocation. Raw spans.
- **Glyph11.Pico** — fastest to a `BinaryRequest`, picohttpparser-level validation only.

See the [docs site](https://dotnet-web-stack.github.io/Glyph11/) for the full reference.
