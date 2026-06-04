---
title: Parsers
weight: 3
---

Glyph11 provides two HTTP/1.1 header parsers with different security/performance tradeoffs.

## Parser Comparison

| Feature | UltraHardenedParser | FlexibleParser |
|---------|---------------------|----------------|
| **Namespace** | `Glyph11.Parser.UltraHardened` | `Glyph11.Parser.FlexibleParser` |
| **Validation** | RFC 9110/9112 compliant | Minimal |
| **Resource limits** | Configurable via `ParserLimits` | None |
| **Method validation** | Token characters only | None |
| **Header name validation** | Token characters only | None |
| **Header value validation** | Field-value characters only | None |
| **Bare LF rejection** | Yes | No |
| **Obs-fold rejection** | Yes | No |
| **HTTP version** | Format validated (`HTTP/X.Y`) | Not validated |
| **Malformed lines** | Throws `HttpParseException` | Silently skipped |
| **Semantic checks** | Enforced inline (smuggling, traversal, Host, ...) | None |
| **Multi-segment** | Auto-linearizes to ROM path | Auto-linearizes to ROM path |
| **SIMD-accelerated** | Yes (`SearchValues<byte>`) | N/A |

## Choosing a Parser

**Use `UltraHardenedParser`** (recommended) when:

- Parsing untrusted input from the network
- Building internet-facing HTTP servers
- Security compliance is required

**Use `FlexibleParser`** when:

- Input is pre-validated or from a trusted source
- Maximum throughput is the priority
- Operating behind a hardened reverse proxy

## Performance

`UltraHardenedParser` adds a small constant-factor overhead over `FlexibleParser` for full RFC compliance and semantic validation. Validation uses SIMD-accelerated `SearchValues<byte>` and `IndexOfAnyExcept` to minimize the cost; the semantic checks are fused into the header and path passes.

See the [Benchmarks](/Glyph11/benchmarks/) page for the latest ROM and multi-segment numbers across payload sizes.

{{< callout type="info" >}}
`UltraHardenedParser` performs both syntax and semantic validation inline. `FlexibleParser` performs neither — use it only for trusted, pre-validated input.
{{< /callout >}}
