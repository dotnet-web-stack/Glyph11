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
| ~95 B   | 116 ns   | 95 ns   | 96 ns    | 101 ns |
| 4 KB    | 728 ns   | 529 ns  | 554 ns   | 585 ns |
| 32 KB   | 5067 ns  | 3852 ns | 4203 ns  | 4226 ns |

**Multi-segment** (3 segments — linearization always counted):

| Payload | C# Ultra | Pure C  | C# (FFI) | Kotlin (FFI) |
|---------|---------:|--------:|---------:|-------------:|
| ~95 B   | 252 ns   | 100 ns  | 107 ns   | 112 ns |
| 4 KB    | 1346 ns  | 560 ns  | 600 ns   | 602 ns |
| 32 KB   | 9202 ns  | 4444 ns | 4634 ns  | 4773 ns |

Multi-segment input **must** be linearized into a contiguous buffer first — that
copy is in every number above. The managed column is the library's real path,
`TryExtractFullHeaderValidated`, which linearizes via `input.ToArray()` (a fresh
allocation every request). The single-slab native core lets the bindings linearize
into a **reused** scratch buffer, avoiding that per-request allocation — ~2× faster
at 32 KB. That gap is a usage advantage, not a parser difference: a managed caller
can match it by hand-rolling `CopyTo` + ROM (≈ contiguous + a memcpy). Numbers vary
run-to-run.
