# Managed vs native (FFI) benchmark

Compares the standalone C# `UltraHardenedParser` against the same C core called
via P/Invoke (`[SuppressGCTransition]`). Same inputs, both zero-allocation. The
C# library is referenced read-only and is **not** modified.

## Run

```sh
# build the release .so first
cmake -S core -B core/build-rel -DGLYPH11_SANITIZE=OFF -DGLYPH11_BUILD_TESTS=OFF
cmake --build core/build-rel

GLYPH11_NATIVE_PATH="$PWD/core/build-rel/libglyph11.so" \
  dotnet run -c Release --project bindings/dotnet/Glyph11.Bench
```

## Results — scalar C core (gcc 13 -O3, .NET 10, x86-64)

| Payload | Managed | Native (FFI) | Ratio |
|---------|--------:|-------------:|:------|
| ~80 B   | 129 ns  | **94 ns**    | native **0.73×** (faster) |
| 4 KB    | 697 ns  | 1,244 ns     | native 1.78× (slower) |
| 32 KB   | 4.80 µs | 10.0 µs      | native 2.08× (slower) |

**Read:** the P/Invoke overhead is *not* the bottleneck — native wins on small
requests (the common case). The managed parser pulls ahead on large payloads
because it validates character classes with hardware SIMD
(`SearchValues<byte>` / `IndexOfAnyExcept`), while the C core validates scalar,
byte-by-byte. SIMD in the C core is the lever to close that gap — see the SIMD
work in `core/`.
