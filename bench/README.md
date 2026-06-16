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
| ~95 B   | 116 ns   | 98 ns   | 97 ns    | 100 ns |
| 4 KB    | 730 ns   | 503 ns  | 502 ns   | 513 ns |
| 32 KB   | 5269 ns  | 3628 ns | 3675 ns  | 3696 ns |

The native (Pure C / FFI / Kotlin) numbers use the AVX2 build shipped for `linux-x64`
(`-march=x86-64-v3`), which inlines the 256-bit scanners — ~1.14× over the portable SSE2
build on the 32 KB header parse (the win is in the scanners, so it shows most on large,
header-heavy payloads). The managed C# parser is unchanged.

**Multi-segment** (3 segments → allocate a buffer per request, linearize, parse):

| Payload | C# Ultra | Pure C  | C# (FFI) | Kotlin (FFI) |
|---------|---------:|--------:|---------:|-------------:|
| ~95 B   | 255 ns   | 102 ns  | 120 ns   | 173 ns |
| 4 KB    | 1337 ns  | 570 ns  | 612 ns   | 769 ns |
| 32 KB   | 9008 ns  | 4539 ns | 4590 ns  | 5325 ns |

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

**Chunked body** (decode a chunked transfer-encoding body — strip the framing, copy the
payload into a reused output buffer):

| Decoded size | C# managed | Pure C | C# (FFI) | Kotlin (FFI) |
|--------------|-----------:|-------:|---------:|-------------:|
| 256 B        | 20 ns      | 13 ns  | 21 ns    | 30 ns |
| 4 KB         | 114 ns     | 71 ns  | 76 ns    | 89 ns |
| 32 KB        | 806 ns     | 625 ns | 740 ns   | 749 ns |

Chunked decode is memcpy-bound — the payload copy dominates, so all four land close, an
order of magnitude under header parsing. Pure C leads (a tight decode-to-output memcpy);
the managed path trails slightly because it loops the span parser chunk-by-chunk and
copies each payload separately, where the native decoders stream the whole body in one
call. The FFI paths add only P/Invoke / FFM call overhead over pure C.
