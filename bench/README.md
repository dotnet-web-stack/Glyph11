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

**Contiguous** (single buffer — parsed in place, no linearization):

| Payload | C# Ultra | Pure C  | C# (FFI) | Kotlin (FFI) |
|---------|---------:|--------:|---------:|-------------:|
| ~95 B   | 117 ns   | 96 ns   | 96 ns    | 101 ns |
| 4 KB    | 723 ns   | 519 ns  | 549 ns   | 556 ns |
| 32 KB   | 5123 ns  | 3808 ns | 4166 ns  | 4188 ns |

**Multi-segment** (3 segments → linearize into a contiguous buffer, then parse):

| Payload | C# Ultra | Pure C  | C# (FFI) | Kotlin (FFI) |
|---------|---------:|--------:|---------:|-------------:|
| ~95 B   | 255 ns   | 101 ns  | 107 ns   | 110 ns |
| 4 KB    | 1345 ns  | 551 ns  | 599 ns   | 584 ns |
| 32 KB   | 9028 ns  | 4217 ns | 4583 ns  | 4660 ns |

Single-segment parses the contiguous data in place — no buffer needed. Multi-segment
must first linearize. The native bindings reuse one scratch buffer (allocated once per
connection), so multi-segment costs only a `memcpy` over single-segment (~1.1×). The
managed `TryExtractFullHeaderValidated` allocates a fresh array (`input.ToArray()`)
**every request**, so its multi-segment pays a per-request GC allocation (~1.8–2.2×).
**That allocation, not the copy, is the multi-segment cost** — the copy itself is only
~425 ns at 32 KB (measured: parse 4114 + copy 425 ≈ copy-and-parse 4497). Numbers vary
run-to-run.
