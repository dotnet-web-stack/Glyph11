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
| ~95 B   | 118 ns   | 97 ns   | 98 ns    | 100 ns |
| 4 KB    | 727 ns   | 522 ns  | 562 ns   | 562 ns |
| 32 KB   | 5039 ns  | 3906 ns | 4122 ns  | 4182 ns |

**Multi-segment** (3 segments — every parser linearizes into a reused buffer, copy counted):

| Payload | C# Ultra | Pure C  | C# (FFI) | Kotlin (FFI) |
|---------|---------:|--------:|---------:|-------------:|
| ~95 B   | 130 ns   | 102 ns  | 110 ns   | 120 ns |
| 4 KB    | 753 ns   | 553 ns  | 612 ns   | 592 ns |
| 32 KB   | 5406 ns  | 4324 ns | 4567 ns  | 4795 ns |

Multi-segment input must be linearized into a contiguous buffer first — that
per-request copy is counted in every number above. To compare the **parsers** (not
buffer strategy), every path linearizes the same way — `CopyTo`/`memcpy` into a
**reused** scratch buffer, then parse — so multi-segment = contiguous + a `memcpy`
for all of them, and native stays ~1.2× ahead in both modes (the parse engine).

> The managed one-shot API `TryExtractFullHeaderValidated` instead allocates that
> buffer via `input.ToArray()` **every request** — ~9.2 µs vs ~5.4 µs at 32 KB. For a
> multi-segment hot path, hand-roll `CopyTo` + `TryExtractFullHeaderROM` (or, for the
> binding, linearize into a reused buffer before the native call). It's an API cost,
> not a parser difference — hence a note, not the comparison.

Numbers vary run-to-run.
