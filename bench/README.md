# Cross-language parser benchmark

Measures `glyph11_parse_request` throughput (ns/op) the **same way across every
runtime** — warmup + timed loop over **identical payloads**
(`bench/gen_payloads.py` writes `small.bin` / `h4k.bin` / `h32k.bin`, read by all
benches):

Each parser is measured in **contiguous** (single buffer) and **multi-segment**
(3-segment) modes:

- **C# Ultra** — the standalone `UltraHardenedParser` (ROM + multi-segment).
- **Pure C** — `core/bench/bench.c`, the native core floor.
- **C# (FFI)** — the P/Invoke binding (`bindings/dotnet/Glyph11.Bench -- csv <dir>`).
- **Kotlin (FFI)** — the Panama/FFM binding (`bindings/kotlin … bench <dir>`).

The C core takes one contiguous buffer, so the native multi-segment columns
measure *linearize 3 segments into a reused buffer, then parse* — a memcpy, not
the per-call allocation the managed multi-segment path does.

## Run

```sh
./bench/run-all.sh          # builds all, writes bench/results/{results.md,results.json}
```

Requires gcc + CMake, .NET 10, and JDK 21 + Gradle. The CI workflow
`cross-bench.yml` runs the same script and publishes `results.json` to the live
benchmarks page.

## Latest local run (best of 5 trials; .NET 10, JDK 21, x86-64)

**Contiguous** (single buffer):

| Payload | C# Ultra | Pure C  | C# (FFI) | Kotlin (FFI) |
|---------|---------:|--------:|---------:|-------------:|
| ~95 B   | 118 ns   | 98 ns   | 97 ns    | 102 ns |
| 4 KB    | 730 ns   | 512 ns  | 556 ns   | 574 ns |
| 32 KB   | 5028 ns  | 3784 ns | 4254 ns  | 4167 ns |

**Multi-segment** (3 segments):

| Payload | C# Ultra | Pure C  | C# (FFI) | Kotlin (FFI) |
|---------|---------:|--------:|---------:|-------------:|
| ~95 B   | 257 ns   | 101 ns  | 106 ns   | 111 ns |
| 4 KB    | 1363 ns  | 545 ns  | 587 ns   | 603 ns |
| 32 KB   | 9262 ns  | 4256 ns | 4624 ns  | 4658 ns |

The FFI bindings track the pure-C floor (`[SuppressGCTransition]` for .NET,
reused off-heap buffers for Kotlin). Native multi-segment = contiguous + a
`memcpy`, so it stays close to contiguous and ~2× faster than the managed
multi-segment path (which allocates per call). Numbers vary run-to-run.
