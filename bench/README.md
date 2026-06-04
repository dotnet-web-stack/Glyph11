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
| ~95 B   | 114 ns   | 95 ns   | 95 ns    | 100 ns |
| 4 KB    | 710 ns   | 517 ns  | 548 ns   | 556 ns |
| 32 KB   | 5180 ns  | 3767 ns | 4120 ns  | 4134 ns |

**Multi-segment** (3 segments — each linearized into a *reused* buffer, then parsed):

| Payload | C# Ultra | Pure C  | C# (FFI) | Kotlin (FFI) |
|---------|---------:|--------:|---------:|-------------:|
| ~95 B   | 125 ns   | 99 ns   | 106 ns   | 110 ns |
| 4 KB    | 751 ns   | 546 ns  | 601 ns   | 585 ns |
| 32 KB   | 5606 ns  | 4222 ns | 4521 ns  | 4617 ns |

Every parser linearizes the segments into a **reused buffer**, so multi-segment =
contiguous + a `memcpy` for all of them and the native-vs-managed gap stays the
same as contiguous — it's the parse engine, not the allocation. (The managed
`TryExtractFullHeaderValidated` *convenience* API would `input.ToArray()` instead,
a per-call allocation that makes it ~1.6× slower at 32 KB; the bench linearizes
manually so the comparison is apples-to-apples.) Numbers vary run-to-run.
