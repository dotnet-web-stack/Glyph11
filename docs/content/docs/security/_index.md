---
title: Security
weight: 4
---

Glyph11 delivers security through a single parser:

**`UltraHardenedParser`** enforces RFC 9110/9112 syntax rules, configurable resource limits, and semantic attack detection — all in one parse pass. Any violation throws `HttpParseException`.

**`FlexibleParser`** performs no validation and is for trusted, pre-validated input only.

## What UltraHardenedParser Catches

```
Input → [UltraHardenedParser] → Application
         │
         ├─ Token & size validation   (syntax)
         ├─ Smuggling detection       (semantic)
         ├─ Path traversal            (semantic)
         └─ Header conflicts          (semantic)
```

It rejects malformed input (invalid characters, oversized fields, missing delimiters) **and** valid-but-dangerous patterns like conflicting `Content-Length` headers or `Transfer-Encoding` combined with `Content-Length` — all during parsing, before your application sees the request.

{{< cards >}}
  {{< card link="parser-limits" title="Parser Limits" subtitle="Configure resource limits for DoS prevention." >}}
{{< /cards >}}
