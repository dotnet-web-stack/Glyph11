---
title: Performance
weight: 6
---

For detailed benchmark results, allocation tracking, and trend charts, see the [Benchmarks](/Glyph11/benchmarks/) page.

## Key Characteristics

- **ROM path is always zero-allocation** — no GC pressure regardless of request size
- **Multi-segment linearization** provides ROM-speed parsing with a single upfront allocation
- **Incomplete input** (no `\r\n\r\n`) returns `false` with zero allocation
- **SIMD-accelerated validation** (`SearchValues<byte>`, `IndexOfAnyExcept`) keeps the `UltraHardenedParser` within a small constant factor of the unvalidated `FlexibleParser`
- **Semantic validation** (smuggling, traversal, Host rules) is zero-allocation and fused into the parse pass

## Running Benchmarks

```bash
cd Benchmarks
dotnet run -c Release
```
