# Cross-language parser benchmark

Measures `glyph11_parse_request` throughput (ns/op) the **same way across every
runtime** — warmup + timed loop over **identical payloads**
(`bench/gen_payloads.py` writes `small.bin` / `h4k.bin` / `h32k.bin`, read by all
benches):

- **Pure C** — `core/bench/bench.c` (the native floor, no FFI).
- **C# (FFI)** — the P/Invoke binding (`bindings/dotnet/Glyph11.Bench -- csv <dir>`).
- **Kotlin (FFI)** — the Panama/FFM binding (`bindings/kotlin … bench <dir>`).
- **C# managed (ref)** — the standalone `UltraHardenedParser`, for reference.

## Run

```sh
./bench/run-all.sh          # builds all, writes bench/results/{results.md,results.json}
```

Requires gcc + CMake, .NET 10, and JDK 21 + Gradle. The CI workflow
`cross-bench.yml` runs the same script and publishes `results.json` to the live
benchmarks page.

## Latest local run (gcc 13 -O3, .NET 10, JDK 21, x86-64)

| Payload | Pure C | C# (FFI) | Kotlin (FFI) | C# managed (ref) |
|---------|-------:|---------:|-------------:|-----------------:|
| ~95 B   | 94 ns  | 95 ns    | 100 ns       | 143 ns |
| 4 KB    | 530 ns | 542 ns   | 564 ns       | 696 ns |
| 32 KB   | 3.9 µs | 4.1 µs   | 3.1 µs       | 4.9 µs |

Both FFI bindings land within a few percent of the pure-C floor
(`[SuppressGCTransition]` for .NET, reused off-heap buffers for Kotlin), and
all native paths beat the managed reference. Numbers vary run-to-run with CPU
turbo / JIT warmup.
