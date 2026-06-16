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
| ~95 B   | 121 ns   | 94 ns   | 97 ns    | 103 ns |
| 4 KB    | 756 ns   | 556 ns  | 565 ns   | 586 ns |
| 32 KB   | 5363 ns  | 4154 ns | 4188 ns  | 4172 ns |

**Multi-segment** (3 segments → allocate a buffer per request, linearize, parse):

| Payload | C# Ultra | Pure C  | C# (FFI) | Kotlin (FFI) |
|---------|---------:|--------:|---------:|-------------:|
| ~95 B   | 265 ns   | 104 ns  | 121 ns   | 173 ns |
| 4 KB    | 1376 ns  | 574 ns  | 629 ns   | 743 ns |
| 32 KB   | 9369 ns  | 4624 ns | 4649 ns  | 5441 ns |

Single-segment parses the contiguous data in place. Multi-segment must first linearize
into a contiguous buffer — and **every** parser allocates that buffer per request here,
so the cost reflects each runtime's allocator:

- **managed** — `TryExtractFullHeaderValidated` allocates a GC array (`ToArray`) →
  **~1.8–2.2×** single-segment. GC pressure, not the copy (the copy is only ~425 ns).
- **pure C / C# FFI** — `malloc` / `NativeMemory` → **~1.1×**. Native allocation is cheap.
- **Kotlin** — a per-request FFM `Arena` → **~1.3×**. The arena costs more than raw `malloc`.

So the multi-segment cost is the **allocation**, and the C core's advantage is native
memory: a GC'd 32 KB array every request is what makes the managed path ~2×. (A hot path
would reuse one scratch buffer instead — then every parser drops to ~1.0–1.1×.) Numbers
vary run-to-run.
