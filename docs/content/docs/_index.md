---
title: Documentation
layout: docs
sidebar:
  open: true
cascade:
  type: docs
---

Glyph11 is a dependency-free, low-allocation HTTP/1.1 header parser for C#. It operates on `ReadOnlyMemory<byte>` and `ReadOnlySequence<byte>`, making it compatible with any network stack.

## Explore

{{< cards >}}
  {{< card link="getting-started" title="Getting Started" subtitle="Install the package and parse your first request." >}}
  {{< card link="architecture" title="Architecture" subtitle="Understand the ROM and linearize parsing paths." >}}
  {{< card link="parsers" title="Parsers" subtitle="Parser usage, validation, and limits." >}}
  {{< card link="security" title="Security" subtitle="Post-parse validation and attack detection." >}}
  {{< card link="api-reference" title="API Reference" subtitle="Complete type and method signatures." >}}
  {{< card link="performance" title="Performance" subtitle="Design characteristics and optimization tips." >}}
{{< /cards >}}
