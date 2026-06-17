# Benchmarks

Reproduces the benchmark tables on the [docs site](https://dotnet-web-stack.github.io/Glyph11/benchmarks.html):
the three packages — **Glyph11** (managed), **Glyph11.Native**, **Glyph11.Pico** — on

- **request header parsing** (contiguous and multi-segment), and
- **chunked body decoding**

across `~95 B` / `4 KB` / `32 KB` payloads, best-of-5, ns/op. The payload bytes are built
in-process and are identical to what the site reports.

## Run

`Glyph11.Native` and `Glyph11.Pico` need their native libraries on the load path (the NuGet
packages bundle them; in-repo, build the cores and point at them):

```sh
cmake -S core -B core/build -DGLYPH11_BUILD_TESTS=OFF && cmake --build core/build
cmake -S bindings/dotnet/Glyph11.Pico/native -B pico-build && cmake --build pico-build

GLYPH11_NATIVE_PATH="$PWD/core/build/libglyph11.so" \
GLYPH11_PICO_NATIVE_PATH="$PWD/pico-build/libglyph11pico.so" \
  dotnet run -c Release --project Benchmarks
```

(`Glyph11` — pure managed — needs no native library; its columns work with no env vars.)

Numbers vary run-to-run and by hardware; treat them as relative. For the lowest `linux-x64`
native numbers, build the cores with AVX2/SSE4.2 (`-DGLYPH11_X86_AVX2=ON` /
`-DGLYPH11_PICO_X86_AVX2=ON`) as the shipped packages do.
