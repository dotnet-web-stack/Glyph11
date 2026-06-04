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

## Results (gcc 13 -O3, .NET 10, x86-64)

| Payload | Managed | Native FFI (scalar) | Native FFI (SSE2) | vs managed |
|---------|--------:|--------------------:|------------------:|:-----------|
| ~80 B   | 128 ns  |  94 ns              | **91 ns**         | **0.71×** (faster) |
| 4 KB    | 690 ns  | 1,244 ns            | **540 ns**        | **0.78×** (faster) |
| 32 KB   | 4.88 µs | 10.0 µs             | **4.00 µs**       | **0.82×** (faster) |

**Read:**

- P/Invoke overhead is *not* the bottleneck — with `[SuppressGCTransition]`,
  native wins even on tiny requests (the common case).
- With **scalar** character-class validation the C core lost on large payloads
  (1.8–2.1×): the managed parser validates with hardware SIMD
  (`SearchValues<byte>` / `IndexOfAnyExcept`).
- Adding **SSE2** scanning to the C core (header-value + request-target
  validation) flipped it: the native parser is now **faster than the managed
  parser at every size**, ~2.3–2.5× faster than its own scalar version on
  4 KB / 32 KB. Parity with C# is preserved (1M-input differential, ASan-clean).
